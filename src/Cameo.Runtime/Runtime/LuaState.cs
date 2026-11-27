using System.Runtime.CompilerServices;
using System.Text;

namespace Cameo.Runtime.Vm;

/// <summary>
/// A VM instance: globals, the main thread and the operations shared by the
/// interpreter and native libraries.
/// </summary>
public sealed partial class LuaState
{
    internal const int SpecialPcall = 1, SpecialXpcall = 2;
    /// <summary>Resource limits (stack, native nesting, string size). May be adjusted before running code.</summary>
    public VmLimits Limits { get; } = new();
    private int MaxNativeDepth => Limits.MaxNativeDepth;
    /// <summary>How many __call metamethods may be followed to reach a function.</summary>
    internal const int MaxCallChain = 100;
    /// <summary>Per-VM registry of Cameo record instance metatables by record id (for cross-module table-literal blessing).</summary>
    internal Dictionary<string, Table>? CameoRecordMetas;

    // ---- host-requested abort (watchdog / cancellation) --------------------------------------
    private int _abort; // 0 = none, else (int)AbortReason + 1; written from any thread

    /// <summary>Asks the VM to stop the running program. Thread-safe. The program is interrupted
    /// at its next loop iteration or call (and inside long pattern matches and scheduler waits)
    /// with a <see cref="LuaAbortException"/> that pcall cannot catch.</summary>
    public void RequestAbort(AbortReason reason)
    {
        Interlocked.CompareExchange(ref _abort, (int)reason + 1, 0);
        foreach (LuaState w in _workers.Keys) w.RequestAbort(reason); // a stopped program's workers stop too
    }

    // ---- parallel workers ---------------------------------------------------------------------
    private readonly System.Collections.Concurrent.ConcurrentDictionary<LuaState, bool> _workers = new();

    /// <summary>Creates the VM for a parallel worker (set by the engine so that workers can
    /// <c>require</c> modules); by default a bare VM with the standard libraries.</summary>
    public Func<LuaState>? WorkerFactory;

    /// <summary>Number of parallel workers of this VM that are running.</summary>
    public int RunningWorkers => _workers.Count;

    /// <summary>True when running under <see cref="Stdlib.CameoLibraries.Hermetic"/>.</summary>
    internal bool IsHermetic;
    /// <summary>True when running under <see cref="Stdlib.CameoLibraries.Sandboxed"/>.</summary>
    internal bool IsSandboxed;
    /// <summary>Factory for isolated sandboxed child VMs (<see cref="Stdlib.CameoLibraries.Sandboxed"/>).</summary>
    internal Func<LuaState>? SandboxedWorkerFactory;
    /// <summary>Factory for strictly deterministic hermetic worker VMs (<see cref="Stdlib.CameoLibraries.Hermetic"/>).</summary>
    internal Func<LuaState>? HermeticWorkerFactory;

    /// <summary>Creates a fresh worker VM, carrying this one's limits, policies, and output streams.</summary>
    internal LuaState NewWorker(bool isolated = false)
    {
        LuaState w;
        if (IsHermetic)
        {
            if (HermeticWorkerFactory != null) w = HermeticWorkerFactory();
            else
            {
                w = new LuaState { AllowDebug = false, IsHermetic = true };
                Stdlib.StdlibRegistry.OpenAll(w, Stdlib.CameoLibraries.Hermetic, allowDebug: false);
            }
        }
        else if (isolated)
        {
            if (SandboxedWorkerFactory != null) w = SandboxedWorkerFactory();
            else
            {
                w = new LuaState { AllowDebug = false, IsSandboxed = true };
                Stdlib.StdlibRegistry.OpenAll(w, Stdlib.CameoLibraries.Sandboxed, allowDebug: false);
            }
        }
        else if (WorkerFactory != null) w = WorkerFactory();
        else
        {
            w = new LuaState { AllowDebug = AllowDebug };
            Stdlib.StdlibRegistry.OpenAll(w, Stdlib.CameoLibraries.All, AllowDebug);
        }
        w.IsHermetic = IsHermetic;
        w.IsSandboxed = isolated || IsSandboxed;
        w.Limits.MaxStackSlots = Limits.MaxStackSlots;
        w.Limits.MaxNativeDepth = Limits.MaxNativeDepth;
        w.Limits.MaxStringLength = Limits.MaxStringLength;
        w.Limits.MaxArrayLength = Limits.MaxArrayLength;
        w.Limits.MaxParallelWorkers = Limits.MaxParallelWorkers;
        w.AllowNetwork = (isolated || IsHermetic) ? false : AllowNetwork;
        w.AllowProcesses = (isolated || IsHermetic) ? false : AllowProcesses;
        // A custom factory cannot remove a parent's restrictions. Each policy owns
        // immutable rule copies, so the same instances are safe to share.
        w.ProcessPolicy = ProcessPolicy;
        w.FilePolicy = (isolated || IsHermetic) ? (FilePolicy ?? FilePolicy.DenyAll) : FilePolicy;
        w.NetworkPolicy = NetworkPolicy;
        w.AllowDebug = (isolated || IsHermetic) ? false : AllowDebug;
        w.AllowDatabase = (isolated || IsHermetic) ? false : AllowDatabase;
        w.DatabaseFactory = (isolated || IsHermetic) ? null : DatabaseFactory;
        if (StepBudget > 0)
        {
            w.StepBudget = Math.Max(1, _stepsRemaining);
            w.ResetStepBudget();
        }
        w.AutoTierCompiler = AutoTierCompiler;
        w.JitTierThreshold = JitTierThreshold;
        w.Stdout = Stdout; w.Stderr = Stderr; w.StdoutBytes = StdoutBytes; w.StderrBytes = StderrBytes;
        w._outLock = _outLock; // one lock per shared stream
        w.Stdin = TextReader.Null;
        w.ScriptArgs = ScriptArgs;
        if (_extensionOpeners != null)
        {
            foreach (Action<LuaState> opener in _extensionOpeners)
                w.RegisterExtension(opener);
        }
        _workers[w] = true;
        int a = Volatile.Read(ref _abort);
        if (a != 0) w.RequestAbort((AbortReason)(a - 1));
        return w;
    }

    private List<Action<LuaState>>? _extensionOpeners;
    internal List<Interop.IHostTypeMarshaller>? HostTypeMarshallers { get; private set; }

    /// <summary>
    /// Registers a custom CLR type marshaller (such as <c>SharpUnits</c> quantities) for
    /// <see cref="ScriptEngine.Register"/>, <see cref="ScriptEngine.Expose"/>, and <see cref="ScriptEngine.DefineValue"/>.
    /// </summary>
    public void RegisterHostTypeMarshaller(Interop.IHostTypeMarshaller marshaller)
    {
        ArgumentNullException.ThrowIfNull(marshaller);
        HostTypeMarshallers ??= new List<Interop.IHostTypeMarshaller>();
        if (!HostTypeMarshallers.Contains(marshaller)) HostTypeMarshallers.Add(marshaller);
    }

    /// <summary>
    /// Opens a deterministic extension library on this VM and registers it so child worker VMs
    /// spawned via <see cref="NewWorker"/> (including <c>parallel.map</c> and <c>Future.parallel</c>)
    /// inherit it automatically.
    /// </summary>
    public void RegisterExtension(Action<LuaState> opener)
    {
        ArgumentNullException.ThrowIfNull(opener);
        _extensionOpeners ??= new List<Action<LuaState>>();
        if (!_extensionOpeners.Contains(opener)) _extensionOpeners.Add(opener);
        opener(this);
    }

    internal void WorkerFinished(LuaState w) => _workers.TryRemove(w, out _);

    // Workers run on threads of their own, never on the .NET thread pool: a pool full of busy
    // workers would starve the timers and I/O completions everything else depends on. At most
    // Limits.MaxParallelWorkers run at a time per VM; the rest wait their turn. (The bound is
    // per VM, so a worker waiting for workers of its own cannot deadlock on its parent's slots.)
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LuaState Worker, Action Run)> _pendingWorkers = new();
    private int _activeWorkers;

    /// <summary>Queues <paramref name="run"/> (the body of worker <paramref name="w"/>) to run on a worker thread.</summary>
    internal void StartWorker(LuaState w, Action run)
    {
        _pendingWorkers.Enqueue((w, run));
        PumpWorkers();
    }

    // Thread-safe: called by the VM thread and by worker threads as they finish.
    private void PumpWorkers()
    {
        while (true)
        {
            int n = Volatile.Read(ref _activeWorkers);
            if (n >= Math.Max(1, Limits.MaxParallelWorkers)) return;
            if (Interlocked.CompareExchange(ref _activeWorkers, n + 1, n) != n) continue;
            if (!_pendingWorkers.TryDequeue(out var next))
            {
                Interlocked.Decrement(ref _activeWorkers);
                if (_pendingWorkers.IsEmpty) return;
                continue; // one was queued between the two checks
            }
            var thread = new Thread(() =>
            {
                try { next.Run(); }
                finally
                {
                    WorkerFinished(next.Worker);
                    Interlocked.Decrement(ref _activeWorkers);
                    PumpWorkers();
                }
            }) { IsBackground = true, Name = "tlvm worker" };
            thread.Start();
        }
    }

    /// <summary>Forgets workers that have not started (after an abort: their run is over).</summary>
    internal void DropPendingWorkers()
    {
        while (_pendingWorkers.TryDequeue(out var dropped)) WorkerFinished(dropped.Worker);
    }

    // Identity of the standard library's functions and tables, so that a value sent to another
    // VM refers to that VM's own `print`, `math`, ... rather than to a copy.
    internal readonly Dictionary<object, string> StdPaths = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<string, Value> StdByPath = new();

    /// <summary>Records the standard library as it is now (call after opening the libraries).</summary>
    public void SnapshotStdlib()
    {
        void Add(string path, in Value v)
        {
            if (v.O is not (Table or LuaFunction)) return;
            if (StdPaths.TryAdd(v.O, path)) StdByPath[path] = v;
        }
        Add("_G", Value.FromTable(Globals));
        Add("\0string", Value.FromTable(StringMeta));
        Add("\0utf16", Value.FromTable(Utf16Meta));
        Add("\0future", Value.FromTable(FutureMeta));
        Add("\0futureerror", Value.FromTable(FutureErrorMeta));
        Add("\0ioerror", Value.FromTable(IoErrorMeta));
        Add("\0floatarray", Value.FromTable(FloatArrayMeta));
        Add("\0query", Value.FromTable(QueryMeta));
        Add("\0list", Value.FromTable(ListMeta));
        Add("\0dict", Value.FromTable(DictMeta));
        // the metatables of typed error records and host objects (DbError, CsvError, ...): a
        // record sent to another VM then keeps its type instead of failing to cross
        foreach (KeyValuePair<string, Table> m in _metas) Add("\0meta:" + m.Key, Value.FromTable(m.Value));
        foreach (KeyValuePair<Value, Value> g in Globals.Pairs())
        {
            if (!g.Key.IsString) continue;
            Add(g.Key.AsString, g.Value);
        }
        foreach (KeyValuePair<Value, Value> g in Globals.Pairs())
        {
            if (!g.Key.IsString || !g.Value.IsTable || g.Value.AsTable == Globals) continue;
            foreach (KeyValuePair<Value, Value> m in g.Value.AsTable.Pairs())
                if (m.Key.IsString) Add(g.Key.AsString + "." + m.Key.AsString, m.Value);
        }
    }

    // ---- permissions ----------------------------------------------------------------------------
    /// <summary>Whether the program may make network requests (<c>aio.httpGet</c>, ...). Off by default.</summary>
    public bool AllowNetwork;
    /// <summary>Whether the program may start other programs (<c>aio.exec</c>). Off by default.</summary>
    public bool AllowProcesses;
    /// <summary>Optional immutable process allowlist, consulted in addition to
    /// <see cref="AllowProcesses"/> and inherited unchanged by parallel/sandbox workers.</summary>
    public ProcessPolicy? ProcessPolicy;
    /// <summary>Optional immutable filesystem allowlist inherited unchanged by workers and sandboxes.</summary>
    public FilePolicy? FilePolicy;
    /// <summary>Optional immutable HTTP/HTTPS allowlist consulted in addition to <see cref="AllowNetwork"/>.</summary>
    public NetworkPolicy? NetworkPolicy;
    /// <summary>Whether the <c>debug</c> library is exposed on this state.</summary>
    public bool AllowDebug = true;

    internal bool AuthorizeFile(string path, bool write, out string canonical)
    {
        if (FilePolicy is not { } policy)
        {
            canonical = path;
            return true;
        }
        return policy.TryAuthorize(path, write, out canonical);
    }

    // ---- deterministic step budget & auto-tiering --------------------------------------------
    private long _stepsRemaining = long.MaxValue;
    internal long StepBudget;

    /// <summary>Number of bytecode steps (loop back-edges and calls) executed under the active <see cref="CameoRuntimeOptions.MaxSteps"/> budget.</summary>
    public long StepsExecuted => StepBudget > 0 ? Math.Max(0, StepBudget - _stepsRemaining) : 0;

    internal void ResetStepBudget() => _stepsRemaining = StepBudget > 0 ? StepBudget : long.MaxValue;

    /// <summary>Optional hook registered by <c>Cameo.Jit</c> for automatic hot-function compilation.</summary>
    internal Func<Closure, NativeFunction?>? AutoTierCompiler;

    /// <summary>Call-count threshold for automatic IL compilation (0 = disabled).</summary>
    internal int JitTierThreshold;

    /// <summary>Clears a pending abort request and resets any configured step budget so the state can run code again.</summary>
    public void ClearAbort()
    {
        Volatile.Write(ref _abort, 0);
        ResetStepBudget();
    }

    /// <summary>True when an abort was requested and not yet acted on (compiled code polls this in loops).</summary>
    internal bool AbortRequested { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _abort != 0; }

    /// <summary>Fast-path poll for backward jumps and calls: checks abort flag and decrements step budget.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool PollStep() => _abort != 0 || --_stepsRemaining <= 0;

    /// <summary>Four slots for preparing a numeric for loop outside the interpreter's stack.</summary>
    internal readonly Value[] ForScratch = new Value[4];

    /// <summary>Throws if an abort was requested. Long-running native code should call this now and then.</summary>
    public void CheckAbort()
    {
        int a = Volatile.Read(ref _abort);
        if (a != 0) throw new LuaAbortException((AbortReason)(a - 1));
    }

    /// <summary>Slow path invoked when <see cref="PollStep"/> trips either an abort or the step budget.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void CheckAbortOrStep()
    {
        CheckAbort();
        if (StepBudget <= 0)
        {
            _stepsRemaining = long.MaxValue;
            return;
        }
        if (_stepsRemaining <= 0)
        {
            RequestAbort(AbortReason.StepLimit);
            throw new LuaAbortException(AbortReason.StepLimit);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowAbort() => CheckAbortOrStep();

    /// <summary>Raises "array too large" for numeric arrays beyond <see cref="VmLimits.MaxArrayLength"/>.</summary>
    public void CheckArrayLength(long length)
    {
        if (length > Limits.MaxArrayLength) throw new LuaException("array too large");
    }

    /// <summary>Raises "resulting string too large" for strings beyond <see cref="VmLimits.MaxStringLength"/>.</summary>
    public void CheckStringLength(long length)
    {
        if (length > Limits.MaxStringLength) throw new LuaException("resulting string too large");
    }

    public readonly Table Globals = new();
    /// <summary>Metatable shared by all strings (its __index is the string library).</summary>
    public readonly Table StringMeta = new();
    public readonly LuaThread MainThread = new(true);
    public LuaThread CurrentThread { get; internal set; }

    /// <summary>Metatable shared by all <c>utf16</c> values.</summary>
    public readonly Table Utf16Meta = new();
    /// <summary>Metatables of <c>Future</c> values and <c>FutureError</c> records.</summary>
    public readonly Table FutureMeta = new();
    public readonly Table FutureErrorMeta = new();
    /// <summary>Metatable of the IoError records carried by failed asynchronous I/O.</summary>
    public readonly Table IoErrorMeta = new();
    /// <summary>Metatable shared by all FloatArray values.</summary>
    public readonly Table FloatArrayMeta = new();
    /// <summary>Per-VM state of the json library (its null and array markers).</summary>
    internal object? JsonShared;

    private readonly Dictionary<string, Table> _metas = new();
    /// <summary>The metatable for a kind of host object (created on first use), e.g. "Stream".</summary>
    public Table MetaFor(string name)
    {
        if (!_metas.TryGetValue(name, out Table? t))
        {
            t = new Table();
            t.SetStr("__name", name);
            _metas[name] = t;
        }
        return t;
    }

    /// <summary>Whether the program may open databases (<c>db.open</c>). Off by default.</summary>
    public bool AllowDatabase;
    /// <summary>Opens the database a program names in <c>db.open(name)</c>. Set by the host (any
    /// ADO.NET provider will do); without it there are no databases to open. The connection is
    /// returned closed; the VM opens, uses and disposes it.</summary>
    public Func<string, System.Data.Common.DbConnection>? DatabaseFactory;

    /// <summary>Metatable shared by all Query values.</summary>
    public readonly Table QueryMeta = new();
    /// <summary>Metatable shared by all List values.</summary>
    public readonly Table ListMeta = new();
    /// <summary>Metatable shared by all Dict values.</summary>
    public readonly Table DictMeta = new();
    /// <summary>The cooperative scheduler that runs asynchronous tasks and future callbacks.</summary>
    public readonly Scheduler Scheduler = new();

    /// <summary>A future completed by a .NET task (from whatever thread it finishes on).</summary>
    public LuaFuture FutureFromTask(Task<Value> task)
    {
        var f = new LuaFuture(this);
        int epoch = Scheduler.BeginExternal();
        task.ContinueWith(t => Scheduler.PostExternal(() =>
        {
            if (!Scheduler.EndExternal(epoch)) return; // the run that started it was aborted
            if (t.IsCanceled) f.Fail(new FutureError(FutureErrorKind.Cancelled, "cancelled"));
            else if (t.IsFaulted) f.Fail(new FutureError(FutureErrorKind.Failed, LuaText.Encode(t.Exception!.GetBaseException().Message)));
            else f.Complete(t.Result);
        }), TaskScheduler.Default);
        return f;
    }

    /// <summary>Text sinks/sources for the standard streams. VM strings are bytes; what is written
    /// here has been decoded as UTF-8 (and stdin text is encoded as UTF-8 for the program).</summary>
    public TextWriter Stdout = Console.Out;
    public TextWriter Stderr = Console.Error;
    public TextReader Stdin = Console.In;
    /// <summary>When set, standard output/error bytes are written here unmodified instead of
    /// being decoded into <see cref="Stdout"/>/<see cref="Stderr"/>.</summary>
    public Stream? StdoutBytes, StderrBytes;
    /// <summary>When set, standard input bytes are read from here instead of <see cref="Stdin"/>.</summary>
    public Stream? StdinBytes;

    private System.Text.Decoder? _outDecoder, _errDecoder;
    private object _outLock = new(); // shared with parallel workers, which write to the same streams
    private readonly Queue<byte> _stdinQueue = new();

    /// <summary>Writes a VM (byte) string to standard output.</summary>
    public void WriteOut(string bytes) { lock (_outLock) WriteStd(bytes, StdoutBytes, Stdout, ref _outDecoder); }
    /// <summary>Writes a VM (byte) string to standard error.</summary>
    public void WriteErr(string bytes) { lock (_outLock) WriteStd(bytes, StderrBytes, Stderr, ref _errDecoder); }

    private static void WriteStd(string bytes, Stream? raw, TextWriter text, ref System.Text.Decoder? decoder)
    {
        if (raw != null)
        {
            raw.Write(LuaText.ToBytes(bytes));
            return;
        }
        bool ascii = true;
        foreach (char ch in bytes) if (ch >= 0x80) { ascii = false; break; }
        if (ascii && decoder == null)
        {
            text.Write(bytes);
            return;
        }
        // A stateful decoder copes with a multi-byte character split across two writes.
        decoder ??= new System.Text.UTF8Encoding(false).GetDecoder();
        byte[] b = LuaText.ToBytes(bytes);
        char[] chars = new char[decoder.GetCharCount(b, 0, b.Length, false)];
        int n = decoder.GetChars(b, 0, b.Length, chars, 0, false);
        text.Write(chars, 0, n);
    }

    public void FlushOut() { lock (_outLock) { if (StdoutBytes != null) StdoutBytes.Flush(); else Stdout.Flush(); } }
    public void FlushErr() { lock (_outLock) { if (StderrBytes != null) StderrBytes.Flush(); else Stderr.Flush(); } }

    private bool FillStdin()
    {
        if (_stdinQueue.Count > 0) return true;
        if (StdinBytes != null)
        {
            int b = StdinBytes.ReadByte();
            if (b < 0) return false;
            _stdinQueue.Enqueue((byte)b);
            return true;
        }
        int c = Stdin.Read();
        if (c < 0) return false;
        Span<char> chars = stackalloc char[2];
        chars[0] = (char)c;
        int len = 1;
        if (char.IsHighSurrogate((char)c) && Stdin.Peek() is int lo && lo >= 0 && char.IsLowSurrogate((char)lo))
        {
            chars[1] = (char)Stdin.Read();
            len = 2;
        }
        Span<byte> buf = stackalloc byte[8];
        int n = System.Text.Encoding.UTF8.GetBytes(chars.Slice(0, len), buf);
        for (int i = 0; i < n; i++) _stdinQueue.Enqueue(buf[i]);
        return true;
    }

    /// <summary>Next byte of standard input without consuming it, or -1 at end of input.</summary>
    public int PeekIn() => FillStdin() ? _stdinQueue.Peek() : -1;
    /// <summary>Reads one byte of standard input, or -1 at end of input.</summary>
    public int ReadIn() => FillStdin() ? _stdinQueue.Dequeue() : -1;

    /// <summary>Loads a module by name for <c>require</c>; returns the module's value. Set by the host.</summary>
    public Func<LuaState, string, Value>? ModuleLoader;
    /// <summary>Compiles source text for <c>load</c>; returns a function value. Set by the host.</summary>
    public Func<LuaState, string, string, Value>? ChunkLoader;
    /// <summary>Cache of loaded modules (package.loaded).</summary>
    public readonly Table LoadedModules = new();
    /// <summary>Script arguments exposed as the global <c>arg</c>.</summary>
    public string[] ScriptArgs = Array.Empty<string>();
    /// <summary>Set by warn("@off"), cleared by warn("@on").</summary>
    internal bool WarningsOff;

    private int _nativeDepth;
    /// <summary>True while a native function called directly by Lua code is running
    /// (false when it was called by another native).</summary>
    internal bool DirectNativeCall;
    /// <summary>Traceback of the error currently being handled by an xpcall message handler.</summary>
    public string? PendingTraceback;

    public LuaState()
    {
        CurrentThread = MainThread;
        MainThread.Limits = Limits;
        Scheduler.Poll = CheckAbort;
    }

    // ------------------------------------------------------------------ helpers


    public static string TypeNameOf(in Value v)
    {
        if (v.Kind == ValueKind.Table && ((Table)v.O!).Metatable is { } mt)
        {
            Value n = mt.GetStr(Meta.Name);
            if (n.IsString) return n.AsString;
        }
        else if (v.Kind == ValueKind.Userdata && v.O is IUserdata u)
        {
            if (u.Metatable is { } umt)
            {
                Value n = umt.GetStr(Meta.Name);
                if (n.IsString) return n.AsString;
            }
            return u.TypeName;
        }
        return v.TypeName;
    }

    public Table? GetMetatable(in Value v) => v.Kind switch
    {
        ValueKind.Table => ((Table)v.O!).Metatable,
        ValueKind.String => StringMeta,
        ValueKind.Userdata => (v.O as IUserdata)?.Metatable,
        _ => null,
    };

    /// <summary>Metamethod lookup by a pre-hashed name.</summary>
    public Value GetMetamethod(in Value v, MetaKey key)
    {
        Table? mt = GetMetatable(v);
        return mt == null ? default : mt.GetStr(key.Name, key.Hash);
    }

    public Value GetMetamethod(in Value v, string name)
    {
        Table? mt = GetMetatable(v);
        return mt == null ? default : mt.GetStr(name);
    }

    /// <summary>Registers a native function in a library table.</summary>
    public void Register(Table lib, string name, NativeFn fn) =>
        lib.SetStr(name, Value.FromFunction(new NativeFunction(name, fn)));

    /// <summary>Registers a native instance method whose direct Cameo dot calls supply <c>self</c>
    /// only when the receiver matches <typeparamref name="TReceiver"/>.</summary>
    /// <summary>Registers a native method for Cameo direct dot-call dispatch on instances of <typeparamref name="TReceiver"/>.</summary>
    public void RegisterInstance<TReceiver>(Table lib, string name, NativeFn fn) =>
        lib.SetStr(name, Value.FromFunction(new NativeFunction(name, fn) { InstanceReceiverType = typeof(TReceiver) }));

    /// <summary>Metatable for Cameo <c>Result&lt;T, E&gt;</c> userdata when the Result prelude is opened.</summary>
    internal Table? ResultMeta;

    public void SetGlobal(string name, Value v) => Globals.SetStr(name, v);
    public Value GetGlobal(string name) => Globals.GetStr(name);

    // ------------------------------------------------------------------ calling

    /// <summary>Calls a function value from host code and returns all results.</summary>
    public Value[] Call(Value fn, params ReadOnlySpan<Value> args)
    {
        var th = CurrentThread;
        int top = th.CallTop;
        th.EnsureStack(top + args.Length + 2);
        th.Stack[top] = fn;
        args.CopyTo(th.Stack.AsSpan(top + 1));
        int n = CallAt(th, top, args.Length);
        if (n == 0) return Array.Empty<Value>();
        var r = new Value[n];
        Array.Copy(th.Stack, top, r, 0, n);
        return r;
    }

    /// <summary>Calls a function and returns its first result (nil if none) without allocating.</summary>
    public Value Call1(in Value fn)
    {
        var th = CurrentThread;
        int top = th.CallTop;
        th.EnsureStack(top + 2);
        th.Stack[top] = fn;
        return CallAt(th, top, 0) > 0 ? th.Stack[top] : default;
    }

    public Value Call1(in Value fn, in Value a)
    {
        var th = CurrentThread;
        int top = th.CallTop;
        th.EnsureStack(top + 3);
        var s = th.Stack;
        s[top] = fn; s[top + 1] = a;
        return CallAt(th, top, 1) > 0 ? th.Stack[top] : default;
    }

    public Value Call1(in Value fn, in Value a, in Value b)
    {
        var th = CurrentThread;
        int top = th.CallTop;
        th.EnsureStack(top + 4);
        var s = th.Stack;
        s[top] = fn; s[top + 1] = a; s[top + 2] = b;
        return CallAt(th, top, 2) > 0 ? th.Stack[top] : default;
    }

    public Value Call1(in Value fn, in Value a, in Value b, in Value c)
    {
        var th = CurrentThread;
        int top = th.CallTop;
        th.EnsureStack(top + 5);
        var s = th.Stack;
        s[top] = fn; s[top + 1] = a; s[top + 2] = b; s[top + 3] = c;
        return CallAt(th, top, 3) > 0 ? th.Stack[top] : default;
    }

    /// <summary>
    /// Calls the value at stack[funcIndex] with nargs arguments following it.
    /// Results are left at stack[funcIndex..]; returns their count.
    /// </summary>
    internal int CallAt(LuaThread th, int funcIndex, int nargs)
    {
        int savedCallTop = th.CallTop;
        for (int callChain = 0; ; callChain++)
        {
            if (callChain > MaxCallChain) throw new LuaException("'__call' chain too long; possible loop");
            Value f = th.Stack[funcIndex];
            if (f.O is Closure cl)
            {
                PushLuaFrame(th, cl, funcIndex + 1, nargs, funcIndex, -1, false);
                ExecStatus st;
                try
                {
                    st = Execute(th, th.FrameCount - 1);
                }
                finally
                {
                    th.CallTop = savedCallTop; // also on errors, so failed calls do not leak stack space
                }
                if (st == ExecStatus.Yield) throw new LuaException("attempt to yield across a native-call boundary");
                return th.Top - funcIndex;
            }
            if (f.O is NativeFunction nf)
            {
                if (++_nativeDepth > MaxNativeDepth) { _nativeDepth--; throw new LuaException("stack overflow (native call depth)"); }
                int n;
                bool savedDirect = DirectNativeCall;
                DirectNativeCall = false;
                try
                {
                    th.CallTop = funcIndex + 1 + nargs;
                    th.EnsureStack(th.CallTop + 8);
                    var ctx = new CallContext(this, th, nf, funcIndex + 1, nargs);
                    n = nf.Fn(ref ctx);
                }
                catch (LuaException e) when (MarkTraced(e))
                {
                    throw; // never reached: the filter only marks the exception
                }
                finally
                {
                    DirectNativeCall = savedDirect;
                    _nativeDepth--;
                    th.CallTop = savedCallTop;
                }
                if (n < 0) throw new LuaException(th.IsMain ? "attempt to yield from outside a coroutine" : "attempt to yield across a native-call boundary");
                Array.Copy(th.Stack, funcIndex + 1, th.Stack, funcIndex, n);
                return n;
            }
            Value h = GetMetamethod(f, Meta.Call);
            if (h.IsNil) throw new LuaException($"attempt to call a {TypeNameOf(f)} value") { Category = ErrorCategory.Call };
            th.EnsureStack(funcIndex + nargs + 3);
            Array.Copy(th.Stack, funcIndex, th.Stack, funcIndex + 1, nargs + 1);
            th.Stack[funcIndex] = h;
            nargs++;
        }
    }

    // An error raised by a native that was called from another native has no Lua source
    // position of its own (as in Lua, where level 1 is then a C function).
    private static bool MarkTraced(LuaException e)
    {
        e.Traced = true;
        return false;
    }

    /// <summary>Name of a native function as "lib.name" when it lives in a loaded library.</summary>
    public string QualifiedName(NativeFunction f)
    {
        foreach (var lib in LoadedModules.Pairs())
        {
            if (!lib.Key.IsString || !lib.Value.IsTable) continue;
            foreach (var kv in lib.Value.AsTable.Pairs())
            {
                if (ReferenceEquals(kv.Value.O, f) && kv.Key.IsString)
                    return lib.Key.AsString == "_G" ? kv.Key.AsString : lib.Key.AsString + "." + kv.Key.AsString;
            }
        }
        return f.Name;
    }

    internal void PushLuaFrame(LuaThread th, Closure cl, int @base, int nargs, int retDest, int nresults, bool isProtected)
    {
        Proto p = cl.Proto;
        th.EnsureStack(@base + p.MaxStack + 8);
        Value[] varargs = Array.Empty<Value>();
        int np = p.NumParams;
        if (nargs > np)
        {
            if (p.IsVarArg)
            {
                // Varargs live outside the value stack but are charged to the same budget, so
                // recursion that piles up arguments ends in "stack overflow", not in exhausting memory.
                if (th.VarargSlots + (nargs - np) > th.Limits.MaxStackSlots) throw new LuaException("stack overflow");
                varargs = new Value[nargs - np];
                Array.Copy(th.Stack, @base + np, varargs, 0, varargs.Length);
                th.VarargSlots += varargs.Length;
            }
        }
        else
        {
            var s = th.Stack;
            for (int j = nargs; j < np; j++) s[@base + j] = default;
        }
        if (th.FrameCount == th.Frames.Length) Array.Resize(ref th.Frames, th.Frames.Length * 2);
        ref Frame f = ref th.Frames[th.FrameCount++];
        f.Cl = cl;
        f.Base = @base;
        f.PC = 0;
        f.RetDest = retDest;
        f.NResults = nresults;
        f.Varargs = varargs;
        f.Protected = isProtected;
        f.HasHandler = false;
        f.Finish = 0;
        f.TbcBase = th.TbcCount;
    }

    // ------------------------------------------------------------------ to-be-closed variables

    /// <summary>Registers stack[index] as a to-be-closed variable.</summary>
    internal void MarkTbc(LuaThread th, int index, Proto p, int pc, int reg)
    {
        Value v = th.Stack[index];
        if (v.Kind == ValueKind.Box) v = ((Box)v.O!).Value;
        if (!v.IsTruthy) return; // nil and false are ignored
        if (GetMetamethod(v, Meta.Close).IsNil)
            throw new LuaException($"variable '{LocalName(p, pc, reg) ?? "?"}' got a non-closable value");
        if (th.TbcCount == th.Tbc.Length) Array.Resize(ref th.Tbc, th.Tbc.Length * 2);
        th.Tbc[th.TbcCount++] = index;
    }

    /// <summary>Calls __close (innermost first) for pending variables down to <paramref name="keep"/>
    /// entries whose stack index is at least <paramref name="fromIndex"/>.</summary>
    internal void CloseTbc(LuaThread th, int keep, int fromIndex, Value error)
    {
        while (th.TbcCount > keep && th.Tbc[th.TbcCount - 1] >= fromIndex)
        {
            int index = th.Tbc[--th.TbcCount];
            Value v = th.Stack[index];
            if (v.Kind == ValueKind.Box) v = ((Box)v.O!).Value;
            Value h = GetMetamethod(v, Meta.Close);
            if (h.IsNil) throw new LuaException("metamethod 'close' is missing");
            Call1(h, v, error);
        }
    }

    // During error unwinding: close the variables of the frames about to be popped. An error
    // raised by a __close handler replaces the error being propagated.
    private void CloseTbcOnUnwind(LuaThread th, int firstPoppedFrame, LuaException le)
    {
        int keep = th.Frames[firstPoppedFrame].TbcBase;
        while (th.TbcCount > keep)
        {
            try
            {
                CloseTbc(th, keep, 0, le.Value);
            }
            catch (LuaException e)
            {
                le.Value = e.Value;
            }
        }
    }

    // ------------------------------------------------------------------ indexing

    /// <summary>For an object of a class with hidden members: raises an error if <paramref name="key"/>
    /// names a private or protected member that code of class <paramref name="caller"/> (null: code
    /// outside any class, or a library function) may not use.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void GuardCheck(Table t, in Value key, ClassTag? caller)
    {
        if (IsHiddenFrom(t, key, caller, out HiddenMember? m))
            throw new LuaException($"'{key.AsString}' is {(m!.Protected ? "protected" : "private")} in class '{m.ClassName}'");
    }

    internal static bool IsHiddenFrom(Table t, in Value key, ClassTag? caller, out HiddenMember? member)
    {
        member = null;
        if (key.Kind != ValueKind.String || t.Metatable?.Class?.Hidden is not { } hidden) return false;
        Value h = hidden.Get(key);
        if (h.IsNil) return false;
        member = (HiddenMember)h.O!;
        return caller == null || !member.Allowed.Contains(caller);
    }

    /// <summary>The class whose code is running on this thread (for library functions: their caller's).</summary>
    internal static ClassTag? RunningClass(LuaThread th) => th.FrameCount > 0 ? th.Frames[th.FrameCount - 1].Cl?.Proto.ClassTag : null;

    public Value Index(in Value obj, in Value key) => Index(obj, key, null);

    /// <summary>Indexing on behalf of code of a class (compiled code passes its class).</summary>
    public Value Index(in Value obj, in Value key, ClassTag? caller)
    {
        Value o = obj;
        if (o.O is Table { Guarded: true } guarded && (guarded.GuardTag == null || !ReferenceEquals(guarded.GuardTag, caller))) GuardCheck(guarded, key, caller);
        for (int loop = 0; loop < 100; loop++)
        {
            Value h;
            if (o.Kind == ValueKind.Table)
            {
                Table t = (Table)o.O!;
                Value v = t.Get(key);
                if (!v.IsNil) return v;
                Table? mt = t.Metatable;
                if (mt == null) return default;
                // a property of a class: its getter answers, with the object alone
                if (mt.Getters != null && mt.Getters.Get(key) is { IsNil: false } getter) return Call1(getter, o);
                h = mt.GetStr(Meta.Index.Name, Meta.Index.Hash);
                if (h.IsNil) return default;
            }
            else
            {
                // a[i] on a numeric array is answered here, without a metamethod call
                if (key.Kind == ValueKind.Integer && o.O is Stdlib.FloatArray fa)
                    return (ulong)(key.I - 1) < (ulong)fa.Data.Length ? Value.FromFloat(fa.Data[key.I - 1]) : default;
                h = GetMetamethod(o, Meta.Index);
                if (h.IsNil) throw IndexError(o, key);
            }
            if (h.Kind == ValueKind.Function) return Call1(h, o, key);
            o = h;
        }
        throw new LuaException("'__index' chain too long; possible loop");
    }

    public Value IndexStr(in Value obj, string key, int hash, ClassTag? caller = null)
    {
        if (obj.Kind == ValueKind.Table)
        {
            Table t = (Table)obj.O!;
            if (t.Guarded && (t.GuardTag == null || !ReferenceEquals(t.GuardTag, caller))) return Index(obj, Value.FromString(key), caller);
            Value v = t.GetStr(key, hash);
            if (!v.IsNil || t.Metatable == null) return v;
        }
        return Index(obj, Value.FromString(key), caller);
    }

    public void SetIndex(in Value obj, in Value key, in Value val) => SetIndex(obj, key, val, null);

    public void SetIndex(in Value obj, in Value key, in Value val, ClassTag? caller)
    {
        Value o = obj;
        if (o.O is Table { Guarded: true } guarded && (guarded.GuardTag == null || !ReferenceEquals(guarded.GuardTag, caller))) GuardCheck(guarded, key, caller);
        for (int loop = 0; loop < 100; loop++)
        {
            Value h;
            if (o.Kind == ValueKind.Table)
            {
                Table t = (Table)o.O!;
                Table? mt = t.Metatable;
                if (mt == null) { t.Set(key, val); return; }
                if (mt.Setters != null && mt.Setters.Get(key) is { IsNil: false } setter) { Call1(setter, o, val); return; }
                h = mt.GetStr(Meta.Newindex.Name, Meta.Newindex.Hash);
                if (h.IsNil || !t.Get(key).IsNil) { t.Set(key, val); return; }
            }
            else
            {
                if (key.Kind == ValueKind.Integer && o.O is Stdlib.FloatArray fa && !fa.Frozen && (ulong)(key.I - 1) < (ulong)fa.Data.Length
                    && (val.Kind == ValueKind.Float || val.Kind == ValueKind.Integer))
                {
                    fa.Data[key.I - 1] = val.AsDouble;
                    return;
                }
                h = GetMetamethod(o, Meta.Newindex);
                if (h.IsNil) throw IndexError(o, key);
            }
            if (h.Kind == ValueKind.Function) { Call1(h, o, key, val); return; }
            o = h;
        }
        throw new LuaException("'__newindex' chain too long; possible loop");
    }

    private static LuaException IndexError(in Value o, in Value key) =>
        new LuaException($"attempt to index a {TypeNameOf(o)} value") { Category = ErrorCategory.Index };

    // ------------------------------------------------------------------ arithmetic

    private static MetaKey[] ArithEvents => Meta.Arith;

    /// <summary>Binary arithmetic/bitwise operation with full Lua semantics (coercions and metamethods).</summary>
    public Value Arith(OpCode op, in Value a, in Value b)
    {
        if (ArithNumbers(op, a, b, out Value r)) return r;
        // string -> number coercion
        if ((a.IsString || b.IsString) && op < OpCode.BAnd)
        {
            Value na = a, nb = b;
            bool ok = true;
            if (a.IsString) ok &= LuaNumber.TryParse(a.AsString, out na);
            if (b.IsString) ok &= LuaNumber.TryParse(b.AsString, out nb);
            if (ok && na.IsNumber && nb.IsNumber && ArithNumbers(op, na, nb, out r)) return r;
        }
        MetaKey ev = ArithEvents[op - OpCode.Add];
        Value h = GetMetamethod(a, ev);
        if (h.IsNil) h = GetMetamethod(b, ev);
        if (!h.IsNil) return Call1(h, a, b);
        if (op >= OpCode.BAnd)
        {
            if (a.IsNumber && b.IsNumber) throw new LuaException("number has no integer representation");
            Value bad = a.IsNumber ? b : a;
            throw new LuaException($"attempt to perform bitwise operation on a {TypeNameOf(bad)} value");
        }
        {
            // with a string operand the failure is reported by the string arithmetic metamethods
            if (a.IsString || b.IsString)
                throw new LuaException($"attempt to {ev.Name.Substring(2)} a '{a.TypeName}' with a '{b.TypeName}'");
            Value bad = (a.IsNumber || (a.IsString && LuaNumber.TryParse(a.AsString, out _))) ? b : a;
            throw new LuaException($"attempt to perform arithmetic on a {TypeNameOf(bad)} value") { Category = ErrorCategory.Arith };
        }
    }

    private static bool ArithNumbers(OpCode op, in Value a, in Value b, out Value r)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer)
        {
            long x = a.I, y = b.I;
            switch (op)
            {
                case OpCode.Add: r = Value.FromInt(unchecked(x + y)); return true;
                case OpCode.Sub: r = Value.FromInt(unchecked(x - y)); return true;
                case OpCode.Mul: r = Value.FromInt(unchecked(x * y)); return true;
                case OpCode.Div: r = Value.FromFloat((double)x / y); return true;
                case OpCode.Pow: r = Value.FromFloat(Math.Pow(x, y)); return true;
                case OpCode.IDiv: r = Value.FromInt(IntFloorDiv(x, y)); return true;
                case OpCode.Mod: r = Value.FromInt(IntMod(x, y)); return true;
                case OpCode.BAnd: r = Value.FromInt(x & y); return true;
                case OpCode.BOr: r = Value.FromInt(x | y); return true;
                case OpCode.BXor: r = Value.FromInt(x ^ y); return true;
                case OpCode.Shl: r = Value.FromInt(ShiftLeft(x, y)); return true;
                case OpCode.Shr: r = Value.FromInt(ShiftLeft(x, unchecked(-y))); return true;
            }
        }
        else if (a.IsNumber && b.IsNumber)
        {
            if (op >= OpCode.BAnd)
            {
                if (a.TryToInteger(out long x) && b.TryToInteger(out long y))
                    return ArithNumbers(op, Value.FromInt(x), Value.FromInt(y), out r);
                r = default;
                return false;
            }
            double p = a.AsDouble, q = b.AsDouble;
            switch (op)
            {
                case OpCode.Add: r = Value.FromFloat(p + q); return true;
                case OpCode.Sub: r = Value.FromFloat(p - q); return true;
                case OpCode.Mul: r = Value.FromFloat(p * q); return true;
                case OpCode.Div: r = Value.FromFloat(p / q); return true;
                case OpCode.Pow: r = Value.FromFloat(Math.Pow(p, q)); return true;
                case OpCode.IDiv: r = Value.FromFloat(Math.Floor(p / q)); return true;
                case OpCode.Mod: r = Value.FromFloat(FloatMod(p, q)); return true;
            }
        }
        r = default;
        return false;
    }

    // Verified: verification/IntArith.dfy, method IntFloorDiv (floor quotient wrapped to 64 bits)
    public static long IntFloorDiv(long x, long y)
    {
        if (y == 0) throw new LuaException("attempt to divide by zero");
        if (y == -1) return unchecked(-x);
        long q = x / y;
        if ((x % y != 0) && ((x ^ y) < 0)) q--;
        return q;
    }

    // Verified: verification/IntArith.dfy, method IntMod and lemma DivModIdentity
    public static long IntMod(long x, long y)
    {
        if (y == 0) throw new LuaException("attempt to perform 'n%0'");
        if (y == -1) return 0;
        long r = x % y;
        if (r != 0 && (r ^ y) < 0) r += y;
        return r;
    }

    public static double FloatMod(double a, double b)
    {
        double r = a % b; // C fmod semantics
        if (r > 0 ? b < 0 : (r < 0 && b > 0)) r += b; // the result takes the sign of the divisor
        return r;
    }

    // Verified: verification/Bits.dfy, methods ShiftLeft / ShiftRight
    public static long ShiftLeft(long x, long n)
    {
        if (n <= -64 || n >= 64) return 0;
        return n >= 0 ? (long)((ulong)x << (int)n) : (long)((ulong)x >> (int)(-n));
    }

    public Value Unm(in Value a)
    {
        if (a.Kind == ValueKind.Integer) return Value.FromInt(unchecked(-a.I));
        if (a.Kind == ValueKind.Float) return Value.FromFloat(-a.N);
        if (a.IsString && LuaNumber.TryParse(a.AsString, out Value n)) return Unm(n);
        Value h = GetMetamethod(a, Meta.Unm);
        if (!h.IsNil) return Call1(h, a, a);
        throw new LuaException($"attempt to perform arithmetic on a {TypeNameOf(a)} value");
    }

    public Value BNot(in Value a)
    {
        if (a.Kind == ValueKind.Integer) return Value.FromInt(~a.I);
        if (a.IsNumber)
        {
            if (a.TryToInteger(out long i)) return Value.FromInt(~i);
            throw new LuaException("number has no integer representation");
        }
        Value h = GetMetamethod(a, Meta.Bnot);
        if (!h.IsNil) return Call1(h, a, a);
        throw new LuaException($"attempt to perform bitwise operation on a {TypeNameOf(a)} value");
    }

    public Value Len(in Value a)
    {
        if (a.Kind == ValueKind.String) return Value.FromInt(a.AsString.Length);
        if (a.Kind == ValueKind.Table)
        {
            Table t = (Table)a.O!;
            if (t.Metatable != null)
            {
                Value h = t.Metatable.GetStr(Meta.Len);
                if (!h.IsNil) return Call1(h, a);
            }
            return Value.FromInt(t.Length);
        }
        if (a.O is Stdlib.FloatArray fa) return Value.FromInt(fa.Data.Length);
        Value mh = GetMetamethod(a, Meta.Len);
        if (!mh.IsNil) return Call1(mh, a);
        throw new LuaException($"attempt to get length of a {TypeNameOf(a)} value");
    }

    // ------------------------------------------------------------------ comparison

    /// <summary>Equality with the __eq metamethod.</summary>
    public bool Equals(in Value a, in Value b)
    {
        if (a.RawEquals(b)) return true;
        if (a.Kind != b.Kind || (a.Kind != ValueKind.Table && a.Kind != ValueKind.Userdata)) return false;
        Value h = GetMetamethod(a, Meta.Eq);
        if (h.IsNil) h = GetMetamethod(b, Meta.Eq);
        if (h.IsNil) return false;
        return Call1(h, a, b).IsTruthy;
    }

    public bool LessThan(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer)
        {
            if (b.Kind == ValueKind.Integer) return a.I < b.I;
            if (b.Kind == ValueKind.Float) return IntLtFloat(a.I, b.N);
        }
        else if (a.Kind == ValueKind.Float)
        {
            if (b.Kind == ValueKind.Float) return a.N < b.N;
            if (b.Kind == ValueKind.Integer) return FloatLtInt(a.N, b.I);
        }
        else if (a.Kind == ValueKind.String && b.Kind == ValueKind.String)
            return string.CompareOrdinal(a.AsString, b.AsString) < 0;
        return CompareMeta(a, b, Meta.Lt);
    }

    public bool LessEqual(in Value a, in Value b)
    {
        if (a.Kind == ValueKind.Integer)
        {
            if (b.Kind == ValueKind.Integer) return a.I <= b.I;
            if (b.Kind == ValueKind.Float) return IntLeFloat(a.I, b.N);
        }
        else if (a.Kind == ValueKind.Float)
        {
            if (b.Kind == ValueKind.Float) return a.N <= b.N;
            if (b.Kind == ValueKind.Integer) return FloatLeInt(a.N, b.I);
        }
        else if (a.Kind == ValueKind.String && b.Kind == ValueKind.String)
            return string.CompareOrdinal(a.AsString, b.AsString) <= 0;
        return CompareMeta(a, b, Meta.Le);
    }

    private bool CompareMeta(in Value a, in Value b, MetaKey ev)
    {
        Value h = GetMetamethod(a, ev);
        if (h.IsNil) h = GetMetamethod(b, ev);
        if (!h.IsNil) return Call1(h, a, b).IsTruthy;
        string t1 = TypeNameOf(a), t2 = TypeNameOf(b);
        if (t1 == t2) throw new LuaException($"attempt to compare two {t1} values");
        throw new LuaException($"attempt to compare {t1} with {t2}");
    }

    private const double TwoPow63 = 9223372036854775808.0;
    private static bool FitsDouble(long i) => i >= -(1L << 53) && i <= (1L << 53);

    // Verified: verification/IntFloat.dfy, method IntLtFloat (exact order between long and double)
    private static bool IntLtFloat(long i, double f)
    {
        if (FitsDouble(i)) return i < f;
        if (double.IsNaN(f)) return false;
        if (f >= TwoPow63) return true;
        if (f > -TwoPow63) return i < (long)Math.Ceiling(f);
        return false;
    }
    // Verified: verification/IntFloat.dfy, method IntLeFloat
    private static bool IntLeFloat(long i, double f)
    {
        if (FitsDouble(i)) return i <= f;
        if (double.IsNaN(f)) return false;
        if (f >= TwoPow63) return true;
        if (f >= -TwoPow63) return i <= (long)Math.Floor(f);
        return false;
    }
    // Verified: verification/IntFloat.dfy, method FloatLtInt
    private static bool FloatLtInt(double f, long i)
    {
        if (FitsDouble(i)) return f < i;
        if (double.IsNaN(f)) return false;
        if (f >= TwoPow63) return false;
        if (f >= -TwoPow63) return (long)Math.Floor(f) < i;
        return true;
    }
    // Verified: verification/IntFloat.dfy, method FloatLeInt
    private static bool FloatLeInt(double f, long i)
    {
        if (FitsDouble(i)) return f <= i;
        if (double.IsNaN(f)) return false;
        if (f >= TwoPow63) return false;
        if (f > -TwoPow63) return (long)Math.Ceiling(f) <= i;
        return true;
    }

    // ------------------------------------------------------------------ strings

    /// <summary><c>tostring</c> semantics including __tostring and __name.</summary>
    public string ToStringMeta(in Value v)
    {
        if (v.Kind == ValueKind.Table || v.Kind == ValueKind.Userdata)
        {
            Table? mt = GetMetatable(v);
            if (mt != null)
            {
                Value h = mt.GetStr(Meta.Tostring);
                if (!h.IsNil)
                {
                    Value r = Call1(h, v);
                    if (!r.IsString)
                    {
                        if (r.IsNumber) return r.ToString();
                        throw new LuaException("'__tostring' must return a string");
                    }
                    return r.AsString;
                }
                Value n = mt.GetStr(Meta.Name);
                if (n.IsString) return n.AsString + ": 0x" + RuntimeHelpers.GetHashCode(v.O).ToString("x8");
            }
        }
        return v.ToString();
    }

    /// <summary>Concatenates stack[from..to] (inclusive), right-associatively, honouring __concat.</summary>
    internal Value Concat(LuaThread th, int from, int to)
    {
        var stack = th.Stack;
        bool simple = true;
        for (int i = from; i <= to; i++)
        {
            ValueKind k = stack[i].Kind;
            if (k != ValueKind.String && k != ValueKind.Integer && k != ValueKind.Float) { simple = false; break; }
        }
        if (simple)
        {
            if (to - from == 1)
            {
                string l = ConcatPart(stack[from]), r = ConcatPart(stack[to]);
                CheckStringLength((long)l.Length + r.Length);
                return Value.FromString(string.Concat(l, r));
            }
            long total = 0;
            for (int i = from; i <= to; i++) total += stack[i].Kind == ValueKind.String ? ((string)stack[i].O!).Length : 24;
            CheckStringLength(total);
            var sb = new StringBuilder();
            for (int i = from; i <= to; i++) sb.Append(ConcatPart(stack[i]));
            return Value.FromString(sb.ToString());
        }
        Value acc = stack[to];
        for (int i = to - 1; i >= from; i--)
        {
            acc = Concat2(th.Stack[i], acc);
        }
        return acc;
    }

    private static string ConcatPart(in Value v) => v.Kind == ValueKind.String ? (string)v.O! : v.ToString();

    public Value Concat2(in Value a, in Value b)
    {
        bool sa = a.IsString || a.IsNumber, sb = b.IsString || b.IsNumber;
        if (sa && sb)
        {
            string l = ConcatPart(a), r = ConcatPart(b);
            CheckStringLength((long)l.Length + r.Length);
            return Value.FromString(string.Concat(l, r));
        }
        Value h = GetMetamethod(a, Meta.Concat);
        if (h.IsNil) h = GetMetamethod(b, Meta.Concat);
        if (!h.IsNil) return Call1(h, a, b);
        Value bad = sa ? b : a;
        throw new LuaException($"attempt to concatenate a {TypeNameOf(bad)} value");
    }

    // ------------------------------------------------------------------ error positions

    /// <summary>"chunk:line:" for the Lua function at the given level (1 = innermost running Lua function).</summary>
    public string Where(int level)
    {
        var th = CurrentThread;
        int idx = th.FrameCount - level;
        if (level <= 0 || idx < 0) return "";
        if (level == 1 && !DirectNativeCall) return "";
        ref Frame f = ref th.Frames[idx];
        return $"{f.Cl.Proto.ChunkName}:{f.Cl.Proto.LineOf(f.PC - 1)}: ";
    }

    /// <summary>Traceback text for the current point of execution.</summary>
    public string Traceback()
    {
        var e = new LuaException("");
        e.CaptureTraceback(CurrentThread);
        return e.Traceback!;
    }

    // ------------------------------------------------------------------ running chunks

    /// <summary>Wraps a compiled main-chunk prototype in a callable function value.</summary>
    public Value MakeFunction(Proto proto)
    {
        Debugger?.Instrument(proto); // every chunk and module passes through here
        return Value.FromFunction(new Closure(proto, Array.Empty<Box>()));
    }

    /// <summary>The debugger attached to this VM, if any (see <see cref="Debugging.Debugger"/>).</summary>
    public Debugging.Debugger? Debugger { get; internal set; }

    /// <summary>Runs a compiled chunk on the main thread and returns its results.</summary>
    public Value[] Run(Proto proto, params ReadOnlySpan<Value> args) => Call(MakeFunction(proto), args);
}
