using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Debugging;

/// <summary>Why the program stopped.</summary>
public enum StopReason { Entry, Breakpoint, Step, Pause }

/// <summary>One level of the call stack of a stopped program.</summary>
public sealed record StackFrameInfo(int Index, string Function, string Source, int Line);

/// <summary>A variable (or table entry) of a stopped program. <see cref="Reference"/> is not 0
/// when the value has children that <see cref="Debugger.Children"/> can list.</summary>
public sealed record VariableInfo(string Name, string Value, string Type, int Reference);

/// <summary>
/// Source-level debugging of a VM: breakpoints, stepping, pausing, and inspection of the call
/// stack and variables while the program is stopped. Protocol-neutral: the command-line tool
/// puts the Debug Adapter Protocol on top of it for editors, and a host application can use
/// it directly.
///
/// How it works: attaching replaces the first instruction of every source line with a trap
/// (the original is kept), so a program that is not being debugged pays nothing and the
/// interpreter loop has no per-instruction check. At a trap the VM thread calls
/// <see cref="OnLine"/>; if the debugger decides to stop, that thread waits inside the call
/// until another thread calls <see cref="Continue"/> or one of the step methods. While it
/// waits, the inspection methods may be called from that other thread.
/// </summary>
public sealed class Debugger
{
    private readonly LuaState _state;
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _resume = new(0);

    private enum Mode { Run, StepIn, StepOver, StepOut }
    private Mode _mode = Mode.Run;
    private LuaThread? _stepThread;
    private int _stepDepth;
    private int _stepLine;
    private Proto? _stepProto;
    private volatile bool _pauseRequested;

    // set while stopped
    private LuaThread? _thread;
    private int _pc;
    private readonly List<Value> _handles = new(); // variablesReference - 1 -> the table it stands for

    /// <summary>Raised on the VM thread when the program stops; the handler must return promptly
    /// (the VM waits after it returns, until told to go on).</summary>
    public event Action<StopReason>? Stopped;

    /// <summary>True while the program is held at a stop.</summary>
    public bool IsStopped { get; private set; }

    private Debugger(LuaState state) { _state = state; }

    /// <summary>Attaches a debugger to the VM. Do this before running the program: functions
    /// compiled earlier are not instrumented.</summary>
    public static Debugger Attach(LuaState state, bool stopOnEntry = false)
    {
        var d = new Debugger(state);
        if (stopOnEntry) d._pauseRequested = true;
        state.Debugger = d;
        return d;
    }

    /// <summary>Paths are compared in this form, so "./a.tl" and its absolute path are the same file.</summary>
    public static string Normalize(string source)
    {
        try { return Path.GetFullPath(source); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return source; }
    }

    /// <summary>Replaces the breakpoints of one source file. Returns, for each requested line,
    /// whether it can ever be hit is not known here: lines without code are simply never reached.</summary>
    public void SetBreakpoints(string source, IEnumerable<int> lines)
    {
        lock (_lock) _breakpoints[Normalize(source)] = new HashSet<int>(lines);
    }

    // ------------------------------------------------------------------ instrumentation

    internal void Instrument(Proto p)
    {
        if (p.OriginalCode == null && p.Code.Length > 0)
        {
            var original = (Instruction[])p.Code.Clone();
            var patched = (Instruction[])p.Code.Clone();
            int last = -1;
            for (int pc = 0; pc < patched.Length; pc++)
            {
                int line = p.LineOf(pc);
                if (line == last) continue;
                last = line;
                // Same operands, so an instruction that reads its neighbour's operands (a
                // comparison reading the jump after it) still finds them.
                patched[pc] = new Instruction(OpCode.Trap, original[pc].A, original[pc].B, original[pc].C);
            }
            p.OriginalCode = original;
            p.Code = patched;
        }
        foreach (Proto child in p.Protos) if (child.OriginalCode == null) Instrument(child);
    }

    // ------------------------------------------------------------------ the VM thread

    internal void OnLine(LuaState L, LuaThread th, Proto p, int pc)
    {
        int line = p.LineOf(pc);
        StopReason reason;
        if (_pauseRequested)
        {
            _pauseRequested = false;
            reason = _mode == Mode.Run && !_everStopped ? StopReason.Entry : StopReason.Pause;
        }
        else if (HitsBreakpoint(p, line)) reason = StopReason.Breakpoint;
        else if (ShouldStep(th, p, line)) reason = StopReason.Step;
        else return;

        _everStopped = true;
        _mode = Mode.Run;
        _thread = th;
        _pc = pc;
        lock (_lock) _handles.Clear();
        IsStopped = true;
        Stopped?.Invoke(reason);
        // Held here until Continue/Step...; an abort (watchdog, cancellation) also lets it go.
        while (!_resume.Wait(50))
        {
            try { L.CheckAbort(); }
            catch (LuaAbortException) { IsStopped = false; _thread = null; throw; }
        }
        IsStopped = false;
        _thread = null;
    }

    private bool _everStopped;

    private bool HitsBreakpoint(Proto p, int line)
    {
        lock (_lock)
        {
            if (_breakpoints.Count == 0) return false;
            return _breakpoints.TryGetValue(SourceOf(p), out HashSet<int>? lines) && lines.Contains(line);
        }
    }

    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);

    private string SourceOf(Proto p)
    {
        if (!_sources.TryGetValue(p.ChunkName, out string? full)) _sources[p.ChunkName] = full = Normalize(p.ChunkName);
        return full;
    }

    private bool ShouldStep(LuaThread th, Proto p, int line)
    {
        switch (_mode)
        {
            case Mode.StepIn:
                return !(ReferenceEquals(th, _stepThread) && th.FrameCount == _stepDepth && ReferenceEquals(p, _stepProto) && line == _stepLine);
            case Mode.StepOver:
                if (!ReferenceEquals(th, _stepThread)) return false; // another coroutine: not this step's business
                return th.FrameCount < _stepDepth || (th.FrameCount == _stepDepth && !(ReferenceEquals(p, _stepProto) && line == _stepLine));
            case Mode.StepOut:
                return ReferenceEquals(th, _stepThread) && th.FrameCount < _stepDepth;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ control (any thread)

    /// <summary>Lets a stopped program run on.</summary>
    public void Continue() => Go(Mode.Run);
    /// <summary>Runs to the next line, entering functions that are called.</summary>
    public void StepIn() => Go(Mode.StepIn);
    /// <summary>Runs to the next line of the current function (or of its caller, once it returns).</summary>
    public void StepOver() => Go(Mode.StepOver);
    /// <summary>Runs until the current function has returned.</summary>
    public void StepOut() => Go(Mode.StepOut);
    /// <summary>Stops a running program at the next line it reaches.</summary>
    public void Pause() => _pauseRequested = true;

    private void Go(Mode mode)
    {
        if (!IsStopped) return;
        LuaThread th = _thread!;
        _mode = mode;
        _stepThread = th;
        _stepDepth = th.FrameCount;
        Frame top = th.Frames[th.FrameCount - 1];
        _stepProto = top.Cl.Proto;
        _stepLine = _stepProto.LineOf(_pc);
        _resume.Release();
    }

    // ------------------------------------------------------------------ inspection (while stopped)

    private LuaThread StoppedThread => _thread ?? throw new InvalidOperationException("the program is not stopped");

    /// <summary>The call stack, innermost first.</summary>
    public IReadOnlyList<StackFrameInfo> StackTrace()
    {
        LuaThread th = StoppedThread;
        var frames = new List<StackFrameInfo>();
        for (int i = th.FrameCount - 1; i >= 0; i--)
        {
            ref Frame f = ref th.Frames[i];
            if (f.Cl == null) continue;
            Proto p = f.Cl.Proto;
            int pc = i == th.FrameCount - 1 ? _pc : Math.Max(f.PC - 1, 0);
            frames.Add(new StackFrameInfo(i, FunctionName(p), SourceOf(p), p.LineOf(pc)));
        }
        return frames;
    }

    // "function 'add'" is how tracebacks name it; a call stack reads better with just "add"
    private static string FunctionName(Proto p)
    {
        string name = LuaText.Decode(p.Name);
        return name.StartsWith("function '", StringComparison.Ordinal) && name.EndsWith('\'') ? name.Substring(10, name.Length - 11) : name;
    }

    /// <summary>The local variables of a frame (by its <see cref="StackFrameInfo.Index"/>), innermost declarations last.</summary>
    public IReadOnlyList<VariableInfo> Locals(int frame)
    {
        LuaThread th = StoppedThread;
        ref Frame f = ref th.Frames[frame];
        Proto p = f.Cl.Proto;
        int pc = frame == th.FrameCount - 1 ? _pc : Math.Max(f.PC - 1, 0);
        var seen = new Dictionary<string, VariableInfo>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (LocVar v in p.LocVars)
        {
            if (v.StartPc > pc || (v.EndPc >= 0 && pc >= v.EndPc) || v.Name.StartsWith('(')) continue;
            string name = LuaText.Decode(v.Name);
            if (!seen.ContainsKey(name)) order.Add(name);
            seen[name] = Describe(name, th.Stack[f.Base + v.Reg]); // a later declaration shadows an earlier one
        }
        return order.Select(n => seen[n]).ToList();
    }

    /// <summary>The variables of enclosing functions that a frame's function uses.</summary>
    public IReadOnlyList<VariableInfo> Upvalues(int frame)
    {
        ref Frame f = ref StoppedThread.Frames[frame];
        var list = new List<VariableInfo>();
        for (int i = 0; i < f.Cl.Upvals.Length; i++)
            list.Add(Describe(i < f.Cl.Proto.Upvals.Length ? LuaText.Decode(f.Cl.Proto.Upvals[i].Name) : "upvalue " + i, f.Cl.Upvals[i].Value));
        return list;
    }

    /// <summary>The global variables, by name.</summary>
    public IReadOnlyList<VariableInfo> Globals() => Entries(_state.Globals);

    /// <summary>The entries of a table shown earlier with a non-zero <see cref="VariableInfo.Reference"/>.</summary>
    public IReadOnlyList<VariableInfo> Children(int reference)
    {
        Value v;
        lock (_lock)
        {
            if (reference < 1 || reference > _handles.Count) return Array.Empty<VariableInfo>();
            v = _handles[reference - 1];
        }
        return v.O is Table t ? Entries(t) : Array.Empty<VariableInfo>();
    }

    private const int MaxEntries = 500;

    private List<VariableInfo> Entries(Table t)
    {
        var list = new List<VariableInfo>();
        foreach (KeyValuePair<Value, Value> kv in t.Pairs())
        {
            if (list.Count >= MaxEntries) { list.Add(new VariableInfo("…", "more entries not shown", "", 0)); break; }
            string name = kv.Key.IsString ? LuaText.Decode(kv.Key.AsString) : "[" + Show(kv.Key) + "]";
            list.Add(Describe(name, kv.Value));
        }
        if (t == _state.Globals) list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private VariableInfo Describe(string name, Value v)
    {
        if (v.Kind == ValueKind.Box) v = ((Box)v.O!).Value; // a local captured by a closure lives in a cell
        int reference = 0;
        if (v.O is Table)
        {
            lock (_lock) { _handles.Add(v); reference = _handles.Count; }
        }
        return new VariableInfo(name, Show(v), LuaState.TypeNameOf(v), reference);
    }

    // No metamethods are run to show a value: inspecting must not execute the program's code.
    private static string Show(in Value v) => v.Kind switch
    {
        ValueKind.String => "\"" + LuaText.Decode(v.AsString) + "\"",
        ValueKind.Table => $"table ({v.AsTable.Length} items)",
        _ => LuaText.Decode(v.ToString()),
    };

    /// <summary>Looks a name up as the program would at the stopped line: local, then upvalue,
    /// then global; dots and constant indexes (<c>a.b[1].c</c>) are followed without running
    /// metamethods. Null when there is no such variable.</summary>
    public VariableInfo? Evaluate(int frame, string expression)
    {
        string[] parts = expression.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        Value current;
        string root = LuaText.Encode(parts[0]);
        LuaThread th = StoppedThread;
        ref Frame f = ref th.Frames[frame];
        Proto p = f.Cl.Proto;
        int pc = frame == th.FrameCount - 1 ? _pc : Math.Max(f.PC - 1, 0);
        int reg = -1;
        foreach (LocVar v in p.LocVars)
            if (v.Name == root && v.StartPc <= pc && (v.EndPc < 0 || pc < v.EndPc)) reg = v.Reg;
        int up = Array.FindIndex(p.Upvals, u => u.Name == root);
        if (reg >= 0) current = th.Stack[f.Base + reg];
        else if (up >= 0 && up < f.Cl.Upvals.Length) current = f.Cl.Upvals[up].Value;
        else current = _state.Globals.GetStr(root);
        if (current.Kind == ValueKind.Box) current = ((Box)current.O!).Value;
        for (int i = 1; i < parts.Length; i++)
        {
            if (current.O is not Table t) return null;
            string key = parts[i].Trim('"', '\'');
            current = long.TryParse(parts[i], out long n) ? t.GetInt(n) : t.GetStr(LuaText.Encode(key));
        }
        if (current.IsNil && parts.Length == 1 && reg < 0 && up < 0) return null;
        return Describe(expression, current);
    }
}
