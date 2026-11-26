using System.Runtime.CompilerServices;

namespace Cameo.Runtime.Vm;

/// <summary>
/// VM instruction set. Operands marked RK may be a register (>= 0) or a constant (~index, i.e. negative).
/// </summary>
public enum OpCode : byte
{
    Move,       // R[A] = R[B]
    LoadK,      // R[A] = K[B]
    LoadNil,    // R[A..A+B] = nil
    LoadBool,   // R[A] = (B != 0); if C != 0 then pc++
    LoadInt,    // R[A] = B
    GetUpval,   // R[A] = Up[B].Value
    SetUpval,   // Up[B].Value = RK[A]
    NewBox,     // R[A] = box(R[A])            (local captured by a closure)
    GetBox,     // R[A] = box(R[B]).Value
    SetBox,     // box(R[A]).Value = RK[B]
    GetGlobal,  // R[A] = G[K[B]]
    SetGlobal,  // G[K[B]] = RK[A]
    GetTable,   // R[A] = R[B][RK[C]]
    GetField,   // R[A] = R[B][K[C]]           (K[C] is a string)
    SetTable,   // R[A][RK[B]] = RK[C]
    SetField,   // R[A][K[B]] = RK[C]          (K[B] is a string)
    NewTable,   // R[A] = {}                   (B = array size hint, C = hash size hint)
    Self,       // R[A+1] = R[B]; R[A] = R[B][K[C]]
    Add, Sub, Mul, Div, Mod, Pow, IDiv,   // R[A] = RK[B] op RK[C]
    BAnd, BOr, BXor, Shl, Shr,
    Unm, Not, Len, BNot,                  // R[A] = op R[B]
    Concat,     // R[A] = R[B] .. ... .. R[C]
    Jmp,        // pc += B
    JmpIf,      // if R[A] is truthy then pc += B
    JmpIfNot,   // if R[A] is falsy then pc += B
    Eq, Lt, Le, // if ((RK[B] op RK[C]) != (A != 0)) then pc++   (next instruction is a Jmp)
    Call,       // R[A..A+C-2] = R[A](R[A+1..A+B-1]);  B == 0: args up to top;  C == 0: keep all results, set top
    TailCall,   // return R[A](R[A+1..A+B-1])          (always followed by Return A 0)
    Return,     // return R[A..A+B-2];  B == 0: up to top
    ForPrep,    // numeric for: prepare R[A..A+2]; R[A+3] = first value; if loop does not run then pc += B
    ForLoop,    // numeric for: step; if loop continues then R[A+3] = value, pc += B
    TForCall,   // R[A+3..A+2+C] = R[A](R[A+1], R[A+2])
    TForLoop,   // if R[A+3] != nil then R[A+2] = R[A+3], pc += B
    SetList,    // R[A][C+i] = R[A+i] for 1 <= i <= B;  B == 0: up to top
    Closure,    // R[A] = closure(Protos[B])
    VarArg,     // R[A..A+C-2] = ...;  C == 0: all, set top
    IsKind,     // R[A] = (R[B] is of runtime kind C)  -- Teal `is`; C is a TypeKindTest
    MathFn,     // R[A] = MathBuiltins.Apply((MathFn)C, number R[B])   -- sin(x), log2(x), ... as built-ins
    Tbc,        // mark R[A] as a to-be-closed variable (<close>)
    Close,      // close pending to-be-closed variables in registers >= A; B != 0: an open result list is live
    Trap,       // debugger: tell the debugger this line is reached, then run the instruction it replaced
                // (same operands; the original opcode is in Proto.OriginalCode). Never produced by the compiler.
    PrepareDotCall, // reserve receiver after function; retain only for registered Future instance natives
}

/// <summary>Runtime type tests used by the <c>IsKind</c> instruction (Teal's <c>is</c> operator).</summary>
public enum TypeKindTest
{
    Nil, Boolean, Number, Integer, Float, String, Table, Function, Thread, Userdata,
}

public readonly struct Instruction
{
    public readonly OpCode Op;
    public readonly int A, B, C;
    public Instruction(OpCode op, int a = 0, int b = 0, int c = 0) { Op = op; A = a; B = b; C = c; }
    public override string ToString() => $"{Op} {A} {B} {C}";
}

/// <summary>The identity of one class declaration.</summary>
public sealed class ClassTag(string id)
{
    public string Id => id;
    public override string ToString() => id;
}

/// <summary>A private or protected member of a class: which classes' code may use it.</summary>
public sealed class HiddenMember(string className, bool isProtected)
{
    public string ClassName => className;
    public bool Protected => isProtected;
    public readonly HashSet<ClassTag> Allowed = new(ReferenceEqualityComparer.Instance);
}

/// <summary>What the VM knows about a class, held by its metatables.</summary>
public sealed class ClassData
{
    public string Id = "";
    public string Name = "";
    public ClassTag? OwnerTag;
    /// <summary>The class's own identity if its code may use every hidden member of its objects
    /// (so no lookup is needed for it); null if it inherits members that are private to a parent.</summary>
    public ClassTag? FastTag;
    /// <summary>Hidden members by name (values are <see cref="HiddenMember"/> userdata), or null.</summary>
    public Table? Hidden;
    /// <summary>The identities of the class, its ancestors and its mixins.</summary>
    public readonly HashSet<string> Ids = new();
}

public readonly struct UpvalDesc
{
    /// <summary>True: capture the box stored in the enclosing function's register Index.
    /// False: capture the enclosing function's upvalue Index.</summary>
    public readonly bool FromParentRegister;
    public readonly int Index;
    public readonly string Name;
    public UpvalDesc(bool fromParentRegister, int index, string name) { FromParentRegister = fromParentRegister; Index = index; Name = name; }
}

/// <summary>Debug record: a local variable occupying a register over a pc range.</summary>
public readonly struct LocVar
{
    public readonly string Name;
    public readonly int Reg, StartPc, EndPc;
    public LocVar(string name, int reg, int startPc, int endPc) { Name = name; Reg = reg; StartPc = startPc; EndPc = endPc; }
}

/// <summary>A compiled function prototype.</summary>
public sealed class Proto
{
    public string Name = "?";
    public string ChunkName = "?";
    public int LineDefined;
    public Instruction[] Code = Array.Empty<Instruction>();
    public int[] Lines = Array.Empty<int>();
    public Value[] K = Array.Empty<Value>();
    /// <summary>Precomputed hash codes of string constants (0 for others).</summary>
    public int[] KHash = Array.Empty<int>();
    public Proto[] Protos = Array.Empty<Proto>();
    public UpvalDesc[] Upvals = Array.Empty<UpvalDesc>();
    public LocVar[] LocVars = Array.Empty<LocVar>();
    public int NumParams;
    /// <summary>Optional checker-resolved numeric parameter hints for the IL target.
    /// Float means a speculative exact-float entry path, never coercion of an integer.
    /// Runtime guards are mandatory because hosts and unchecked code can pass any value.</summary>
    public ValueKind?[] NumericParameters = [];
    public bool IsVarArg;
    public int MaxStack;
    /// <summary>Set for a function written inside a class declaration: identifies that class, so the
    /// VM can tell whether running code may use the class's private and protected members.</summary>
    public ClassTag? ClassTag;
    /// <summary>True for inline instance methods declared on a Cameo record.</summary>
    public bool IsCameoMethod;

    public int LineOf(int pc) => (uint)pc < (uint)Lines.Length ? Lines[pc] : LineDefined;

    /// <summary>The code as compiled, kept when a debugger has put traps into <see cref="Code"/> (null otherwise).</summary>
    public Instruction[]? OriginalCode;

    /// <summary>Hot-call counter and cached IL body for automatic tiering.</summary>
    internal int CallCount;
    internal bool TierAttempted;
    internal object? TierBody;

    /// <summary>The instruction at pc as the compiler wrote it, whether or not a debugger is attached.</summary>
    public Instruction At(int pc) => (OriginalCode ?? Code)[pc];

    public void FinishConstants()
    {
        KHash = new int[K.Length];
        for (int i = 0; i < K.Length; i++)
            if (K[i].Kind == ValueKind.String) KHash[i] = K[i].AsString.GetHashCode();
    }
}

public closed class LuaFunction
{
    public abstract string Name { get; }
}

public sealed class Closure : LuaFunction
{
    public readonly Proto Proto;
    public readonly Box[] Upvals;
    internal NativeFunction? TieredFunction;
    public Closure(Proto proto, Box[] upvals) { Proto = proto; Upvals = upvals; }
    public override string Name => Proto.Name;
}

/// <summary>
/// Signature of a host function. Arguments are read through <paramref name="ctx"/>; results are
/// produced with one of the <c>ctx.Return(...)</c> overloads, whose value must be returned.
/// </summary>
public delegate int NativeFn(ref CallContext ctx);

public sealed class NativeFunction : LuaFunction
{
    public readonly NativeFn Fn;
    private readonly string _name;
    internal int Special; // see LuaState.SpecialPcall
    internal Type? InstanceReceiverType { get; init; }
    internal bool FutureInstanceMethod
    {
        get => InstanceReceiverType == typeof(LuaFuture);
        init => InstanceReceiverType = value ? typeof(LuaFuture) : null;
    }
    public NativeFunction(string name, NativeFn fn) { _name = name; Fn = fn; }
    public override string Name => _name;
}

/// <summary>Arguments and result channel of a native function call.</summary>
public struct CallContext
{
    public readonly LuaState L;
    public readonly LuaThread Thread;
    public readonly NativeFunction Function;
    private readonly int _base;
    /// <summary>Number of arguments passed.</summary>
    public readonly int Count;
    /// <summary>Index in the thread's stack of argument 0 (results are written from here).</summary>
    internal int Base => _base;

    internal CallContext(LuaState l, LuaThread th, NativeFunction f, int @base, int count)
    {
        L = l; Thread = th; Function = f; _base = @base; Count = count;
    }

    /// <summary>Argument by 0-based index; nil when absent.</summary>
    public Value this[int i]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (uint)i < (uint)Count ? Thread.Stack[_base + i] : default;
    }

    public bool IsNoneOrNil(int i) => i >= Count || Thread.Stack[_base + i].IsNil;

    /// <summary>Copies arguments [from, Count) into a new array.</summary>
    public Value[] ArgsFrom(int from)
    {
        if (from >= Count) return Array.Empty<Value>();
        var r = new Value[Count - from];
        Array.Copy(Thread.Stack, _base + from, r, 0, r.Length);
        return r;
    }

    // ------------------------------------------------------------ results

    public int Return() => 0;
    public int Return(Value a) { Thread.Stack[_base] = a; return 1; }
    public int Return(Value a, Value b)
    {
        Thread.EnsureStack(_base + 2);
        var s = Thread.Stack; s[_base] = a; s[_base + 1] = b; return 2;
    }
    public int Return(Value a, Value b, Value c)
    {
        Thread.EnsureStack(_base + 3);
        var s = Thread.Stack; s[_base] = a; s[_base + 1] = b; s[_base + 2] = c; return 3;
    }
    public int Return(ReadOnlySpan<Value> values)
    {
        Thread.EnsureStack(_base + values.Length);
        values.CopyTo(Thread.Stack.AsSpan(_base));
        return values.Length;
    }
    /// <summary>Returns arguments [from, Count) unchanged (e.g. for <c>select</c>).</summary>
    public int ReturnArgsFrom(int from)
    {
        if (from >= Count) return 0;
        var s = Thread.Stack;
        int n = Count - from;
        if (from != 0) Array.Copy(s, _base + from, s, _base, n);
        return n;
    }
    /// <summary>Used by coroutine.yield: yields arguments [0, Count).</summary>
    internal int YieldAll() => ~Count;
    /// <summary>Yields with no values (used when a task suspends on a future).</summary>
    internal int YieldNone() => ~0;

    // ------------------------------------------------------------ argument checking

    public LuaException ArgError(int i, string message) =>
        new LuaException($"bad argument #{i + 1} to '{L.QualifiedName(Function)}' ({message})");

    public LuaException TypeError(int i, string expected)
    {
        string got = i >= Count ? "no value" : LuaState.TypeNameOf(this[i]);
        return ArgError(i, $"{expected} expected, got {got}");
    }

    public void CheckAny(int i)
    {
        if (i >= Count) throw ArgError(i, "value expected");
    }

    public long CheckInt(int i)
    {
        Value v = this[i];
        if (v.Kind == ValueKind.Integer) return v.I;
        if (v.TryToInteger(out long r)) return r;
        if (v.IsNumber || (v.IsString && v.TryToDouble(out _))) throw ArgError(i, "number has no integer representation");
        throw TypeError(i, "number");
    }
    public long OptInt(int i, long def) => IsNoneOrNil(i) ? def : CheckInt(i);

    public double CheckNumber(int i)
    {
        Value v = this[i];
        if (v.Kind == ValueKind.Float) return v.N;
        if (v.TryToDouble(out double d)) return d;
        throw TypeError(i, "number");
    }
    public double OptNumber(int i, double def) => IsNoneOrNil(i) ? def : CheckNumber(i);

    /// <summary>String argument; numbers are converted as Lua does.</summary>
    public string CheckString(int i)
    {
        Value v = this[i];
        if (v.Kind == ValueKind.String) return (string)v.O!;
        if (v.IsNumber) return v.ToString();
        throw TypeError(i, "string");
    }
    public string OptString(int i, string def) => IsNoneOrNil(i) ? def : CheckString(i);

    /// <summary>Argument as .NET text: a string (decoded from UTF-8), a number, or a <c>utf16</c> value.</summary>
    public string CheckText(int i)
    {
        Value v = this[i];
        if (v.O is Utf16String u) return u.Text;
        return LuaText.Decode(CheckString(i));
    }
    /// <summary>Returns .NET text as a VM string (UTF-8).</summary>
    public int ReturnText(string? text) => Return(text == null ? Value.Nil : Value.FromText(text));

    public Table CheckTable(int i)
    {
        Value v = this[i];
        if (v.Kind == ValueKind.Table) return (Table)v.O!;
        throw TypeError(i, "table");
    }

    public Value CheckFunction(int i)
    {
        Value v = this[i];
        if (v.Kind == ValueKind.Function) return v;
        throw TypeError(i, "function");
    }

    public bool ToBoolean(int i) => this[i].IsTruthy;
}

public enum ErrorCategory : byte { None, Index, Call, Arith }

/// <summary>An error raised by (or into) a running program. Carries an arbitrary error value.</summary>
public sealed class LuaException : Exception
{
    public Value Value;
    /// <summary>Lets the interpreter describe the offending operand ("local 'x'", "field 'y'").</summary>
    public ErrorCategory Category;
    /// <summary>True once position information has been added (or deliberately omitted).</summary>
    public bool Traced;
    // The traceback is captured as a cheap snapshot and only rendered to text when asked for,
    // so errors that are caught and discarded by pcall stay inexpensive.
    private (Proto Proto, int Pc)[]? _trace;
    private string? _traceback;

    /// <summary>"stack traceback:" text for where the error was raised, if known.</summary>
    public string? Traceback
    {
        get
        {
            if (_traceback == null && _trace != null)
            {
                var sb = new System.Text.StringBuilder("stack traceback:");
                foreach (var (proto, pc) in _trace)
                {
                    if (proto == null) sb.Append("\n\t...");
                    else sb.Append("\n\t").Append(proto.ChunkName).Append(':').Append(proto.LineOf(pc - 1)).Append(": in ").Append(proto.Name);
                }
                _traceback = sb.ToString();
            }
            return _traceback;
        }
        set => _traceback = value;
    }

    internal void CaptureTraceback(LuaThread th)
    {
        const int Top = 12, Bottom = 10;
        int n = th.FrameCount;
        bool cut = n > Top + Bottom;
        var t = new (Proto, int)[cut ? Top + Bottom + 1 : n];
        int w = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            if (cut && w == Top)
            {
                t[w++] = (null!, 0);
                i = Bottom;
                continue;
            }
            ref Frame f = ref th.Frames[i];
            t[w++] = (f.Cl.Proto, f.PC);
        }
        _trace = t;
    }

    public LuaException(string message) : base(message) { Value = Value.FromString(message); }
    public LuaException(Value value, bool traced = true) : base(value.IsString ? value.AsString : value.ToString())
    {
        Value = value; Traced = traced;
    }

    /// <summary>The error value as .NET text.</summary>
    public override string Message => LuaText.Decode(Value.IsString ? Value.AsString : Value.ToString());
}

/// <summary>Thrown by <c>os.exit</c>. It is not a Lua error: <c>pcall</c> does not catch it, and it
/// propagates to the host, which typically ends the process with <see cref="Code"/>.</summary>
public sealed class LuaExitException : Exception
{
    public int Code { get; }
    public LuaExitException(int code) : base("os.exit(" + code + ")") { Code = code; }
}
