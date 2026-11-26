using System.Runtime.CompilerServices;
using Closure = Cameo.Runtime.Vm.Closure;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;

namespace Cameo.Jit;

/// <summary>The state of one numeric for loop in compiled code.</summary>
public struct ForState
{
    public bool Skip, IsInt;
    public long Index, Count, Step;
    public double FIndex, FLimit, FStep;
}

/// <summary>
/// What the generated IL calls. Each operation has the interpreter's fast path (integers and
/// floats, plain tables) written so that the .NET JIT inlines it, and falls back to the same
/// LuaState methods native library functions use, so metamethods and errors behave as in the
/// interpreter.
/// </summary>
public static class JitOps
{
    /// <summary>Shares direct Cameo dot-call argument selection with the interpreter.</summary>
    public static Value[] PrepareDotArguments(Value function, Value[] arguments) =>
        DotCallPreparation.PrepareArguments(function, arguments);

    private const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;
    private const MethodImplOptions Cold = MethodImplOptions.NoInlining;

    // ------------------------------------------------------------------ arithmetic

    [MethodImpl(Inline)]
    public static Value Add(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return Value.FromInt(unchecked(a.I + b.I));
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Float) return Value.FromFloat(a.N + b.N);
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Float) return Value.FromFloat(a.I + b.N);
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Integer) return Value.FromFloat(a.N + b.I);
        return L.Arith(OpCode.Add, a, b);
    }

    [MethodImpl(Inline)]
    public static Value Sub(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return Value.FromInt(unchecked(a.I - b.I));
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Float) return Value.FromFloat(a.N - b.N);
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Float) return Value.FromFloat(a.I - b.N);
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Integer) return Value.FromFloat(a.N - b.I);
        return L.Arith(OpCode.Sub, a, b);
    }

    [MethodImpl(Inline)]
    public static Value Mul(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return Value.FromInt(unchecked(a.I * b.I));
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Float) return Value.FromFloat(a.N * b.N);
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Float) return Value.FromFloat(a.I * b.N);
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Integer) return Value.FromFloat(a.N * b.I);
        return L.Arith(OpCode.Mul, a, b);
    }

    [MethodImpl(Inline)]
    public static Value Div(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Float) return Value.FromFloat(a.N / b.N);
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return Value.FromFloat((double)a.I / b.I);
        return L.Arith(OpCode.Div, a, b);
    }

    // % and // on integers: the verified floor division (verification/IntArith.dfy); a zero
    // divisor takes the general path, which raises the error
    [MethodImpl(Inline)]
    public static Value Mod(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer && b.I != 0) return Value.FromInt(LuaState.IntMod(a.I, b.I));
        return L.Arith(OpCode.Mod, a, b);
    }

    [MethodImpl(Inline)]
    public static Value IDiv(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer && b.I != 0) return Value.FromInt(LuaState.IntFloorDiv(a.I, b.I));
        return L.Arith(OpCode.IDiv, a, b);
    }

    /// <summary>Pow and the bitwise operators: no inline path.</summary>
    public static Value Arith(Value a, Value b, LuaState L, int op) => L.Arith((OpCode)op, a, b);

    public static Value Unm(Value a, LuaState L) => L.Unm(a);
    public static Value BNot(Value a, LuaState L) => L.BNot(a);
    public static Value Len(Value a, LuaState L) => L.Len(a);
    [MethodImpl(Inline)] public static Value Not(Value a) => Value.FromBool(!a.IsTruthy);
    [MethodImpl(Inline)] public static bool Truthy(Value a) => a.IsTruthy;
    public static Value Concat(Value a, Value b, LuaState L) => L.Concat2(a, b);

    public static Value MathFn(Value a, int fn)
    {
        double x;
        if (a.Kind == ValueKind.Float) x = a.N;
        else if (a.Kind == ValueKind.Integer) x = a.I;
        else if (!a.TryToDouble(out x))
            throw new LuaException($"bad argument #1 to '{((MathFn)fn).ToString().ToLowerInvariant()}' (number expected, got {LuaState.TypeNameOf(a)})");
        return Value.FromFloat(MathBuiltins.Apply((MathFn)fn, x));
    }

    public static Value IsKind(Value a, int test) => Value.FromBool(LuaState.IsKind(a, (TypeKindTest)test));

    // ------------------------------------------------------------------ comparison

    [MethodImpl(Inline)]
    public static bool Eq(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return a.I == b.I;
        if (a.Kind != b.Kind && !(a.IsNumber && b.IsNumber)) return false;
        return L.Equals(a, b);
    }

    [MethodImpl(Inline)]
    public static bool Lt(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return a.I < b.I;
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Float) return a.N < b.N;
        return L.LessThan(a, b);
    }

    [MethodImpl(Inline)]
    public static bool Le(Value a, Value b, LuaState L)
    {
        if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return a.I <= b.I;
        if (a.Kind == ValueKind.Float && b.Kind == ValueKind.Float) return a.N <= b.N;
        return L.LessEqual(a, b);
    }

    // ------------------------------------------------------------------ tables, globals, upvalues

    [MethodImpl(Inline)]
    public static Value GetTable(Value obj, Value key, LuaState L, Closure cl)
    {
        if (obj.Kind == ValueKind.Table && key.Kind == ValueKind.Integer)
        {
            Table t = Unsafe.As<Table>(obj.O!);
            ref readonly Value v = ref t.GetIntRef(key.I);
            if (!v.IsNil || t.Metatable == null) return v;
        }
        else if (obj.O is FloatArray fa && key.Kind == ValueKind.Integer)
        {
            double[] data = fa.Data;
            long i = key.I - 1;
            if ((ulong)i < (ulong)data.Length) return Value.FromFloat(data[i]);
        }
        return L.Index(obj, key, cl.Proto.ClassTag);
    }

    [MethodImpl(Inline)]
    public static void SetTable(Value obj, Value key, Value value, LuaState L, Closure cl)
    {
        if (obj.Kind == ValueKind.Table)
        {
            // a plain table: the interpreter's own shortcut
            Table t = Unsafe.As<Table>(obj.O!);
            if (t.Metatable == null)
            {
                if (key.Kind == ValueKind.Integer) t.SetInt(key.I, value); else t.Set(key, value);
                return;
            }
        }
        else if (obj.O is FloatArray fa && key.Kind == ValueKind.Integer && value.Kind == ValueKind.Float)
        {
            double[] data = fa.Data;
            long i = key.I - 1;
            if ((ulong)i < (ulong)data.Length) { data[i] = value.N; return; }
        }
        L.SetIndex(obj, key, value, cl.Proto.ClassTag);
    }

    public static Value GetField(Value obj, Value[] k, int index, int hash, LuaState L, Closure cl) =>
        L.IndexStr(obj, Unsafe.As<string>(k[index].O!), hash, cl.Proto.ClassTag);

    public static Value GetGlobal(Value[] k, int index, int hash, LuaState L) =>
        L.Globals.GetStr(Unsafe.As<string>(k[index].O!), hash);

    public static void SetGlobal(Value value, Value[] k, int index, int hash, LuaState L) =>
        L.Globals.SetStr(Unsafe.As<string>(k[index].O!), hash, value);

    [MethodImpl(Inline)] public static Value GetUpval(Closure cl, int index) => cl.Upvals[index].Value;
    [MethodImpl(Inline)] public static void SetUpval(Value value, Closure cl, int index) => cl.Upvals[index].Value = value;

    public static Value NewTable(int array, int hash) => Value.FromTable(new Table(array, hash));

    public static void SetList(Value table, Value[] items, int first)
    {
        Table t = (Table)table.O!;
        for (int i = 0; i < items.Length; i++) t.SetInt(first + i, items[i]);
    }

    // ------------------------------------------------------------------ calls, arguments, results

    [MethodImpl(Inline)]
    public static Value Arg(LuaThread th, int @base, int nargs, int index) => index < nargs ? th.Stack[@base + index] : default;

    public static Value Call0(Value f, LuaState L) => L.Call1(f);
    public static Value Call1(Value f, Value a, LuaState L) => L.Call1(f, a);
    public static Value Call2(Value f, Value a, Value b, LuaState L) => L.Call1(f, a, b);
    public static Value Call3(Value f, Value a, Value b, Value c, LuaState L) => L.Call1(f, a, b, c);

    /// <summary>A call with any number of arguments; the result has at least <paramref name="want"/> values.</summary>
    public static Value[] CallN(Value f, Value[] args, int want, LuaState L)
    {
        Value[] r = L.Call(f, args);
        if (r.Length < want) Array.Resize(ref r, want);
        return r;
    }

    /// <summary>All the results of a call (for f(g()), return g() and {g()}).</summary>
    public static Value[] CallAll(Value f, Value[] args, LuaState L) => L.Call(f, args);

    /// <summary>Fixed values followed by the results of the last call.</summary>
    public static Value[] Gather(Value[] head, Value[] tail)
    {
        if (head.Length == 0) return tail;
        var all = new Value[head.Length + tail.Length];
        head.CopyTo(all, 0);
        tail.CopyTo(all, head.Length);
        return all;
    }

    public static int Ret0() => 0;

    public static int Ret1(Value v, LuaThread th, int @base)
    {
        th.EnsureStack(@base + 1);
        th.Stack[@base] = v;
        return 1;
    }

    public static int RetN(Value[] values, LuaThread th, int @base)
    {
        th.EnsureStack(@base + values.Length);
        values.CopyTo(th.Stack, @base);
        return values.Length;
    }

    // ------------------------------------------------------------------ loops

    /// <summary>Stops a compiled loop when the host asked for an abort or exhausted the step budget.</summary>
    [MethodImpl(Inline)]
    public static void Poll(LuaState L)
    {
        if (L.PollStep()) L.CheckAbortOrStep();
    }

    public static ForState ForPrep(Value init, Value limit, Value step, LuaState L)
    {
        // the interpreter's own preparation (verified in verification/ForLoop.dfy), on scratch slots
        Value[] s = L.ForScratch;
        s[0] = init; s[1] = limit; s[2] = step;
        var state = new ForState();
        if (LuaState.OpForPrep(s, 0)) { state.Skip = true; return state; }
        if (s[0].Kind == ValueKind.Integer)
        {
            state.IsInt = true;
            state.Index = s[0].I;
            state.Count = s[1].I;       // the number of further iterations, as an unsigned count
            state.Step = s[2].I;
        }
        else
        {
            state.FIndex = s[0].N;
            state.FLimit = s[1].N;
            state.FStep = s[2].N;
        }
        return state;
    }

    [MethodImpl(Cold)]
    public static int Unreachable() => throw new InvalidOperationException("compiled code reached an instruction that cannot be reached");

    /// <summary>Adds "chunk:line:" to an error raised by an operation in compiled code, as the interpreter does.</summary>
    public static Exception Locate(Exception e, string chunk, int line)
    {
        if (e is LuaException { Traced: false, Value.IsString: true } le)
            return new LuaException(Value.FromString($"{chunk}:{line}: {le.Value.AsString}"), traced: true);
        return e;
    }

    // ------------------------------------------------------------------ boxes, closures, <close>

    [MethodImpl(Inline)]
    public static Value NewBox(Value initial) => Value.FromBox(new Box(initial));

    [MethodImpl(Inline)]
    public static Value GetBox(Value box) => ((Box)box.O!).Value;

    [MethodImpl(Inline)]
    public static void SetBox(Value box, Value value) => ((Box)box.O!).Value = value;

    public static Value MakeClosure(Closure parent, int protoIndex, Value[] capturedParentRegs)
    {
        Proto np = parent.Proto.Protos[protoIndex];
        UpvalDesc[] descs = np.Upvals;
        Box[] ups = descs.Length == 0 ? [] : new Box[descs.Length];
        int regCursor = 0;
        for (int j = 0; j < ups.Length; j++)
            ups[j] = descs[j].FromParentRegister
                ? (Box)capturedParentRegs[regCursor++].O!
                : parent.Upvals[descs[j].Index];
        return Value.FromFunction(new Closure(np, ups));
    }

    internal static void MarkTbc(LuaState L, CompiledFrame frame, int reg, Value val)
    {
        if (!val.IsNil && !(val.Kind == ValueKind.Boolean && val.I == 0))
        {
            Value mm = L.GetMetatable(val)?.GetStr("__close") ?? Value.Nil;
            if (mm.IsNil)
                throw new LuaException("variable got a non-closable value");
        }
        (frame.Tbc ??= new()).Add((reg, val));
    }

    internal static void CloseFrom(LuaState L, CompiledFrame frame, int fromReg, Value errVal)
    {
        if (frame.Tbc is not { Count: > 0 } list) return;
        Exception? firstErr = null;
        Value activeErr = errVal;
        while (list.Count > 0 && list[^1].Reg >= fromReg)
        {
            Value v = list[^1].Val;
            list.RemoveAt(list.Count - 1);
            if (v.IsNil || (v.Kind == ValueKind.Boolean && v.I == 0)) continue;
            Value mm = L.GetMetatable(v)?.GetStr("__close") ?? Value.Nil;
            if (mm.IsNil) continue;
            try
            {
                L.Call(mm, v, activeErr);
            }
            catch (Exception ex) when (ex is not LuaAbortException)
            {
                firstErr = ex;
                activeErr = ex is LuaException le ? le.Value : Value.FromText(ex.Message);
            }
        }
        if (firstErr != null) throw firstErr;
    }
}
