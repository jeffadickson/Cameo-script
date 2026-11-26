using System.Reflection;
using System.Reflection.Emit;
using Cameo.Runtime.Vm;
using OpCode = Cameo.Runtime.Vm.OpCode;
using Label = System.Reflection.Emit.Label;

namespace Cameo.Jit;

/// <summary>Why a function was not compiled.</summary>
public enum JitErrorKind
{
    /// <summary>The function uses something this compiler does not translate yet.</summary>
    Unsupported,
    /// <summary>This runtime cannot generate code (an ahead-of-time compiled host).</summary>
    Unavailable,
    /// <summary>The value is not a Teal function (a native function, or not a function).</summary>
    NotAFunction,
}

public sealed record class JitError(JitErrorKind Kind, string Message);

/// <summary>The compiled body: reads its arguments from the thread's stack at <c>base</c>,
/// writes its results there and returns how many.</summary>
public delegate int CompiledBody(LuaState L, LuaThread th, int @base, int nargs, Closure cl, Value[] k);

/// <summary>
/// Translates bytecode into resumable IL. Proven same-kind numeric registers become
/// primitive locals behind exact-kind entry guards; dynamic operations use
/// <see cref="JitOps"/>. Numeric loops keep primitive counters and calls save continuations.
///
/// What it does not translate is refused, never approximated: closures, varargs, generic for
/// loops and to-be-closed variables. Compiled callees use explicit frames; ordinary
/// callees still use <see cref="LuaState.Call"/>, so a compiled function cannot yield.
/// </summary>
public static class IlCompiler
{
    private static readonly Type V = typeof(Value);
    private static MethodInfo Op(string name) => typeof(JitOps).GetMethod(name) ?? throw new MissingMethodException(name);
    private static readonly MethodInfo FromInt = V.GetMethod(nameof(Value.FromInt))!, FromFloat = V.GetMethod(nameof(Value.FromFloat), [typeof(double)])!;
    private static readonly MethodInfo FromBool = V.GetMethod(nameof(Value.FromBool))!;

    /// <summary>The first thing in a function this compiler cannot translate, or null.</summary>
    public static string? Unsupported(Proto p)
    {
        if (BytecodeValidator.Validate(p) is { } bad) return $"invalid bytecode ({bad})";
        return null;
    }

    public static bool TryCompile(Proto p, out CompiledBody? body, out JitError? error)
    {
        body = null;
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            error = new JitError(JitErrorKind.Unavailable, "this runtime cannot generate code");
            return false;
        }
        if (Unsupported(p) is { } why)
        {
            error = new JitError(JitErrorKind.Unsupported, $"{p.Name} was not compiled: {why}");
            return false;
        }
        try
        {
            // Snapshot hints once: mutable host metadata must never weaken the guards
            // protecting the primitive loads emitted from this same snapshot.
            ValueKind?[] hints = (ValueKind?[])p.NumericParameters.Clone();
            CompiledStep specialized = Emit(p, specialize: true, hints, out int slots);
            CompiledStep? generic = null;
            if (hints.Any(kind => kind is ValueKind.Integer or ValueKind.Float))
            {
                generic = Emit(p, specialize: false, hints, out int genericSlots);
                slots = Math.Max(slots, genericSlots);
            }
            body = new CompiledProgram(specialized, generic, hints, slots).Run;
            error = null;
            return true;
        }
        catch (NotSupportedException e)
        {
            error = new JitError(JitErrorKind.Unsupported, $"{p.Name} was not compiled: {e.Message}");
            return false;
        }
    }

    private sealed class Loop
    {
        public required LocalBuilder IsInt, Index, Count, Step, FIndex, FLimit, FStep;
    }

    private static CompiledStep Emit(Proto p, bool specialize, ValueKind?[] hints, out int frameSlots)
    {
        var method = new DynamicMethod("teal_" + p.Name, typeof(int),
            [typeof(LuaState), typeof(LuaThread), typeof(int), typeof(int), typeof(Closure), typeof(Value[]), typeof(CompiledFrame)],
            typeof(JitOps).Module, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        var locals = new List<LocalBuilder>();
        LocalBuilder Local(Type type) { var local = il.DeclareLocal(type); locals.Add(local); return local; }
        Instruction[] code = p.OriginalCode ?? p.Code;
        ValueKind?[] numeric = specialize ? NumericAnalysis.Analyze(p, hints) : new ValueKind?[p.MaxStack + 1];

        var reg = new LocalBuilder[p.MaxStack + 1];
        for (int r = 0; r < reg.Length; r++) reg[r] = Local(numeric[r] == ValueKind.Integer ? typeof(long) : numeric[r] == ValueKind.Float ? typeof(double) : V);
        LocalBuilder valueScratch = il.DeclareLocal(V); // only used between adjacent load/store instructions
        LocalBuilder line = Local(typeof(int)), result = Local(typeof(int));
        LocalBuilder forState = Local(typeof(ForState));
        var labels = new Label[code.Length + 1];
        for (int i = 0; i <= code.Length; i++) labels[i] = il.DefineLabel();
        var loops = new Dictionary<int, Loop>();        // by the register the loop's control values start at
        Label done = il.DefineLabel();
        // The results of a call that keeps all of them (f(g()), return g(), {g()}) are held in
        // one array until the next instruction, which uses them, takes them.
        LocalBuilder open = Local(typeof(Value[]));
        Label dispatch = il.DefineLabel(), initialize = il.DefineLabel(), save = il.DefineLabel();
        var resumes = new List<Label> { initialize };
        void Frame() => il.Emit(OpCodes.Ldarg_S, (byte)6);
        void FrameCall(string name) => il.Emit(OpCodes.Call, typeof(CompiledFrame).GetMethod(name)!);

        // Every explicit call is a continuation boundary. The generated body exits before
        // the callee runs; the dispatcher returns its results and resumes exactly here.
        void RequestCall(Instruction instruction, bool tail)
        {
            L(); Call(nameof(JitOps.Poll));
            Load(instruction.A);
            if (instruction.B == 0) GatherFrom(instruction.A + 1);
            else NewArray(instruction.B - 1, j => Load(instruction.A + 1 + j));
            Frame(); Int(resumes.Count); Int(tail ? 1 : 0);
            FrameCall(nameof(CompiledFrame.Request));
            Frame(); il.Emit(OpCodes.Ldloc, line); il.Emit(OpCodes.Stfld, typeof(CompiledFrame).GetField(nameof(CompiledFrame.Line))!);
            if (tail) { Int(-2); Return(); return; }
            il.Emit(OpCodes.Br, save);
            Label resume = il.DefineLabel(); resumes.Add(resume); il.MarkLabel(resume);
        }
        int openFrom = -1;                                // the register its first value stands for

        // ---- small emitters
        void L() => il.Emit(OpCodes.Ldarg_0);
        void K() => il.Emit(OpCodes.Ldarg_S, (byte)5);
        void Cl() => il.Emit(OpCodes.Ldarg_S, (byte)4);
        void Int(int n) => il.Emit(OpCodes.Ldc_I4, n);
        void Call(string name) => il.Emit(OpCodes.Call, Op(name));
        void Load(int r)
        {
            il.Emit(OpCodes.Ldloc, reg[r]);
            if (numeric[r] is { } kind) il.Emit(OpCodes.Call, kind == ValueKind.Integer ? FromInt : FromFloat);
        }
        void Store(int r)
        {
            if (numeric[r] is { } kind)
            {
                il.Emit(OpCodes.Stloc, valueScratch);
                il.Emit(OpCodes.Ldloca, valueScratch);
                il.Emit(OpCodes.Call, V.GetProperty(kind == ValueKind.Integer ? nameof(Value.I) : nameof(Value.N))!.GetMethod!);
            }
            il.Emit(OpCodes.Stloc, reg[r]);
        }
        ValueKind? NumberKind(int operand) => operand >= 0 ? numeric[operand]
            : p.K[~operand].Kind is ValueKind.Integer or ValueKind.Float ? p.K[~operand].Kind : null;
        void Number(int operand, bool asFloat)
        {
            ValueKind kind = NumberKind(operand)!.Value;
            if (operand >= 0) il.Emit(OpCodes.Ldloc, reg[operand]);
            else if (kind == ValueKind.Integer) il.Emit(OpCodes.Ldc_I8, p.K[~operand].I);
            else il.Emit(OpCodes.Ldc_R8, p.K[~operand].N);
            if (asFloat && kind == ValueKind.Integer) il.Emit(OpCodes.Conv_R8);
        }
        void StoreNumber(int destination, ValueKind kind)
        {
            if (numeric[destination] == kind) il.Emit(OpCodes.Stloc, reg[destination]);
            else { il.Emit(OpCodes.Call, kind == ValueKind.Integer ? FromInt : FromFloat); Store(destination); }
        }
        bool Binary(Instruction instruction)
        {
            if (NumberKind(instruction.B) is not { } left || NumberKind(instruction.C) is not { } right) return false;
            bool floating = instruction.Op == OpCode.Div || left == ValueKind.Float || right == ValueKind.Float;
            Number(instruction.B, floating); Number(instruction.C, floating);
            il.Emit(instruction.Op switch { OpCode.Add => OpCodes.Add, OpCode.Sub => OpCodes.Sub, OpCode.Mul => OpCodes.Mul, _ => OpCodes.Div });
            StoreNumber(instruction.A, floating ? ValueKind.Float : ValueKind.Integer);
            return true;
        }
        void LoadConstant(int index)
        {
            Value v = p.K[index];
            if (v.Kind == ValueKind.Integer) { il.Emit(OpCodes.Ldc_I8, v.I); il.Emit(OpCodes.Call, FromInt); }
            else if (v.Kind == ValueKind.Float) { il.Emit(OpCodes.Ldc_R8, v.N); il.Emit(OpCodes.Call, FromFloat); }
            else { K(); Int(index); il.Emit(OpCodes.Ldelem, V); }
        }
        // an operand that is a register (>= 0) or a constant (< 0)
        void RK(int x) { if (x >= 0) Load(x); else LoadConstant(~x); }
        void Return() { il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Leave, done); }
        Label At(int pc) => (uint)pc <= (uint)code.Length ? labels[pc] : throw new NotSupportedException("a jump leaves the function");
        void NewArray(int count, Action<int> element)
        {
            Int(count);
            il.Emit(OpCodes.Newarr, V);
            for (int j = 0; j < count; j++)
            {
                il.Emit(OpCodes.Dup);
                Int(j);
                element(j);
                il.Emit(OpCodes.Stelem, V);
            }
        }

        // the values from register `first` up to the open results, then the open results
        void GatherFrom(int first)
        {
            if (openFrom < first) throw new NotSupportedException("a variable number of values is used away from the call that made them");
            int fixedCount = openFrom - first;
            NewArray(fixedCount, j => Load(first + j));
            il.Emit(OpCodes.Ldloc, open);
            Call(nameof(JitOps.Gather));
            openFrom = -1;
        }

        il.BeginExceptionBlock();
        il.Emit(OpCodes.Br, dispatch);
        il.MarkLabel(initialize);

        // parameters: from the caller's stack; the other registers start as nil (IL locals are zeroed)
        for (int r = 0; r < p.NumParams; r++)
        {
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3); Int(r);
            Call(nameof(JitOps.Arg));
            Store(r);
        }
        if (p.IsVarArg)
        {
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3); Int(p.NumParams); Frame();
            FrameCall(nameof(CompiledFrame.InitVarargs));
        }

        int lastLine = -1;
        for (int pc = 0; pc < code.Length; pc++)
        {
            il.MarkLabel(labels[pc]);
            Instruction i = code[pc];
            int at = p.LineOf(pc);
            if (at != lastLine) { Int(at); il.Emit(OpCodes.Stloc, line); lastLine = at; }

            switch (i.Op)
            {
                case OpCode.Move: Load(i.B); Store(i.A); break;
                case OpCode.LoadK: LoadConstant(i.B); Store(i.A); break;
                case OpCode.LoadNil:
                    for (int r = i.A; r <= i.A + i.B; r++) { il.Emit(OpCodes.Ldloca, reg[r]); il.Emit(OpCodes.Initobj, V); }
                    break;
                case OpCode.LoadBool:
                    Int(i.B != 0 ? 1 : 0); il.Emit(OpCodes.Call, FromBool); Store(i.A);
                    if (i.C != 0) il.Emit(OpCodes.Br, At(pc + 2));
                    break;
                case OpCode.LoadInt: il.Emit(OpCodes.Ldc_I8, (long)i.B); il.Emit(OpCodes.Call, FromInt); Store(i.A); break;

                case OpCode.GetUpval: il.Emit(OpCodes.Ldarg_S, (byte)4); Int(i.B); Call(nameof(JitOps.GetUpval)); Store(i.A); break;
                case OpCode.SetUpval: RK(i.A); il.Emit(OpCodes.Ldarg_S, (byte)4); Int(i.B); Call(nameof(JitOps.SetUpval)); break;
                case OpCode.GetGlobal: K(); Int(i.B); Int(p.KHash[i.B]); L(); Call(nameof(JitOps.GetGlobal)); Store(i.A); break;
                case OpCode.SetGlobal: RK(i.A); K(); Int(i.B); Int(p.KHash[i.B]); L(); Call(nameof(JitOps.SetGlobal)); break;

                case OpCode.GetTable: Load(i.B); RK(i.C); L(); Cl(); Call(nameof(JitOps.GetTable)); Store(i.A); break;
                case OpCode.GetField: Load(i.B); K(); Int(i.C); Int(p.KHash[i.C]); L(); Cl(); Call(nameof(JitOps.GetField)); Store(i.A); break;
                case OpCode.SetTable: Load(i.A); RK(i.B); RK(i.C); L(); Cl(); Call(nameof(JitOps.SetTable)); break;
                case OpCode.SetField: Load(i.A); LoadConstant(i.B); RK(i.C); L(); Cl(); Call(nameof(JitOps.SetTable)); break;
                case OpCode.NewTable: Int(i.B); Int(i.C); Call(nameof(JitOps.NewTable)); Store(i.A); break;
                case OpCode.Self:
                    // the object is read before either register is written: A + 1 may be B
                    Load(i.B); K(); Int(i.C); Int(p.KHash[i.C]); L(); Cl(); Call(nameof(JitOps.GetField));
                    Load(i.B); Store(i.A + 1);
                    Store(i.A);
                    break;
                case OpCode.SetList:
                    Load(i.A);
                    if (i.B == 0) GatherFrom(i.A + 1); else NewArray(i.B, j => Load(i.A + 1 + j));
                    Int(i.C + 1);
                    Call(nameof(JitOps.SetList));
                    break;

                case OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.Div:
                    if (!Binary(i))
                    {
                        RK(i.B); RK(i.C); L(); Call(i.Op.ToString()); Store(i.A);
                    }
                    break;
                case OpCode.Mod: RK(i.B); RK(i.C); L(); Call(nameof(JitOps.Mod)); Store(i.A); break;
                case OpCode.IDiv: RK(i.B); RK(i.C); L(); Call(nameof(JitOps.IDiv)); Store(i.A); break;
                case OpCode.Pow or OpCode.BAnd or OpCode.BOr or OpCode.BXor or OpCode.Shl or OpCode.Shr:
                    RK(i.B); RK(i.C); L(); Int((int)i.Op); Call(nameof(JitOps.Arith)); Store(i.A); break;
                case OpCode.Unm:
                    if (numeric[i.B] is { } unaryKind)
                    {
                        Number(i.B, unaryKind == ValueKind.Float); il.Emit(OpCodes.Neg); StoreNumber(i.A, unaryKind);
                    }
                    else { Load(i.B); L(); Call(nameof(JitOps.Unm)); Store(i.A); }
                    break;
                case OpCode.BNot: Load(i.B); L(); Call(nameof(JitOps.BNot)); Store(i.A); break;
                case OpCode.Len: Load(i.B); L(); Call(nameof(JitOps.Len)); Store(i.A); break;
                case OpCode.Not: Load(i.B); Call(nameof(JitOps.Not)); Store(i.A); break;
                case OpCode.Concat:
                    // right to left, as the language defines it
                    Load(i.C - 1); Load(i.C); L(); Call(nameof(JitOps.Concat));
                    for (int r = i.C - 2; r >= i.B; r--)
                    {
                        Store(i.A);
                        Load(r); Load(i.A); L(); Call(nameof(JitOps.Concat));
                    }
                    Store(i.A);
                    break;
                case OpCode.MathFn: Load(i.B); Int(i.C); Call(nameof(JitOps.MathFn)); Store(i.A); break;
                case OpCode.IsKind: Load(i.B); Int(i.C); Call(nameof(JitOps.IsKind)); Store(i.A); break;

                case OpCode.Jmp:
                    if (i.B < 0) { L(); Call(nameof(JitOps.Poll)); }        // a loop: let the host stop it
                    il.Emit(OpCodes.Br, At(pc + 1 + i.B));
                    break;
                case OpCode.JmpIf: Load(i.A); Call(nameof(JitOps.Truthy)); il.Emit(OpCodes.Brtrue, At(pc + 1 + i.B)); break;
                case OpCode.JmpIfNot: Load(i.A); Call(nameof(JitOps.Truthy)); il.Emit(OpCodes.Brfalse, At(pc + 1 + i.B)); break;
                case OpCode.Eq or OpCode.Lt or OpCode.Le:
                    // if the comparison's outcome differs from A, the next instruction is skipped
                    if (NumberKind(i.B) is { } comparisonKind && NumberKind(i.C) == comparisonKind)
                    {
                        Number(i.B, comparisonKind == ValueKind.Float); Number(i.C, comparisonKind == ValueKind.Float);
                        if (i.Op == OpCode.Eq) il.Emit(OpCodes.Ceq);
                        else if (i.Op == OpCode.Lt) il.Emit(OpCodes.Clt);
                        else
                        {
                            // !(a > b or unordered): NaN must make <= false.
                            il.Emit(comparisonKind == ValueKind.Float ? OpCodes.Cgt_Un : OpCodes.Cgt);
                            Int(0); il.Emit(OpCodes.Ceq);
                        }
                    }
                    else
                    {
                        RK(i.B); RK(i.C); L();
                        Call(i.Op == OpCode.Eq ? nameof(JitOps.Eq) : i.Op == OpCode.Lt ? nameof(JitOps.Lt) : nameof(JitOps.Le));
                    }
                    il.Emit(i.A != 0 ? OpCodes.Brfalse : OpCodes.Brtrue, At(pc + 2));
                    break;

                case OpCode.PrepareDotCall:
                    Load(i.A);
                    if (i.B == 0) GatherFrom(i.A + 1);
                    else NewArray(i.B - 1, j => Load(i.A + 1 + j));
                    Call(nameof(JitOps.PrepareDotArguments));
                    il.Emit(OpCodes.Stloc, open);
                    openFrom = i.A + 1;
                    break;

                case OpCode.Call:
                {
                    int nres = i.C - 1;
                    RequestCall(i, tail: false);
                    Frame(); Int(nres); FrameCall(nameof(CompiledFrame.TakeResults));
                    if (i.C == 0)
                    {
                        // keep every result for the next instruction
                        il.Emit(OpCodes.Stloc, open);
                        openFrom = i.A;
                        break;
                    }
                    for (int j = 0; j < nres; j++)
                    {
                        il.Emit(OpCodes.Dup); Int(j); il.Emit(OpCodes.Ldelem, V); Store(i.A + j);
                    }
                    il.Emit(OpCodes.Pop);
                    break;
                }
                case OpCode.TailCall:
                {
                    RequestCall(i, tail: true);
                    break;
                }
                case OpCode.Return:
                {
                    int n = i.B - 1;
                    if (i.B == 0)
                    {
                        if (pc > 0 && code[pc - 1].Op == OpCode.TailCall) { Call(nameof(JitOps.Unreachable)); Return(); break; }
                        GatherFrom(i.A);
                        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
                        Call(nameof(JitOps.RetN));
                        Return();
                        break;
                    }
                    if (n == 0) Call(nameof(JitOps.Ret0));
                    else if (n == 1) { Load(i.A); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2); Call(nameof(JitOps.Ret1)); }
                    else
                    {
                        NewArray(n, j => Load(i.A + j));
                        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
                        Call(nameof(JitOps.RetN));
                    }
                    Return();
                    break;
                }

                case OpCode.ForPrep:
                {
                    var loop = new Loop
                    {
                        IsInt = Local(typeof(bool)),
                        Index = Local(typeof(long)), Count = Local(typeof(long)), Step = Local(typeof(long)),
                        FIndex = Local(typeof(double)), FLimit = Local(typeof(double)), FStep = Local(typeof(double)),
                    };
                    loops[i.A] = loop;
                    Load(i.A); Load(i.A + 1); Load(i.A + 2); L();
                    Call(nameof(JitOps.ForPrep));
                    il.Emit(OpCodes.Stloc, forState);
                    void Field(string name, LocalBuilder to)
                    {
                        il.Emit(OpCodes.Ldloc, forState);
                        il.Emit(OpCodes.Ldfld, typeof(ForState).GetField(name)!);
                        il.Emit(OpCodes.Stloc, to);
                    }
                    il.Emit(OpCodes.Ldloc, forState);
                    il.Emit(OpCodes.Ldfld, typeof(ForState).GetField(nameof(ForState.Skip))!);
                    il.Emit(OpCodes.Brtrue, At(pc + 1 + i.B));
                    Field(nameof(ForState.IsInt), loop.IsInt);
                    Field(nameof(ForState.Index), loop.Index); Field(nameof(ForState.Count), loop.Count); Field(nameof(ForState.Step), loop.Step);
                    Field(nameof(ForState.FIndex), loop.FIndex); Field(nameof(ForState.FLimit), loop.FLimit); Field(nameof(ForState.FStep), loop.FStep);
                    // the loop variable's first value
                    Label isFloat = il.DefineLabel(), set = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, loop.IsInt); il.Emit(OpCodes.Brfalse, isFloat);
                    il.Emit(OpCodes.Ldloc, loop.Index); il.Emit(OpCodes.Call, FromInt); il.Emit(OpCodes.Br, set);
                    il.MarkLabel(isFloat);
                    il.Emit(OpCodes.Ldloc, loop.FIndex); il.Emit(OpCodes.Call, FromFloat);
                    il.MarkLabel(set);
                    Store(i.A + 3);
                    break;
                }
                case OpCode.ForLoop:
                {
                    if (!loops.TryGetValue(i.A, out Loop? loop)) throw new NotSupportedException("a for loop is entered from outside");
                    Label body = At(pc + 1 + i.B), exit = il.DefineLabel(), isFloat = il.DefineLabel(), up = il.DefineLabel(), take = il.DefineLabel();
                    L(); Call(nameof(JitOps.Poll));
                    il.Emit(OpCodes.Ldloc, loop.IsInt); il.Emit(OpCodes.Brfalse, isFloat);
                    // integers: a count of remaining iterations, so the index cannot overflow
                    il.Emit(OpCodes.Ldloc, loop.Count); il.Emit(OpCodes.Brfalse, exit);
                    il.Emit(OpCodes.Ldloc, loop.Count); il.Emit(OpCodes.Ldc_I8, 1L); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, loop.Count);
                    il.Emit(OpCodes.Ldloc, loop.Index); il.Emit(OpCodes.Ldloc, loop.Step); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, loop.Index);
                    il.Emit(OpCodes.Ldloc, loop.Index); il.Emit(OpCodes.Call, FromInt); Store(i.A + 3);
                    il.Emit(OpCodes.Br, body);
                    // floats: step, then compare with the limit in the direction of the step
                    il.MarkLabel(isFloat);
                    il.Emit(OpCodes.Ldloc, loop.FIndex); il.Emit(OpCodes.Ldloc, loop.FStep); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, loop.FIndex);
                    il.Emit(OpCodes.Ldloc, loop.FStep); il.Emit(OpCodes.Ldc_R8, 0.0); il.Emit(OpCodes.Bgt, up);
                    il.Emit(OpCodes.Ldloc, loop.FLimit); il.Emit(OpCodes.Ldloc, loop.FIndex); il.Emit(OpCodes.Ble, take);
                    il.Emit(OpCodes.Br, exit);
                    il.MarkLabel(up);
                    il.Emit(OpCodes.Ldloc, loop.FIndex); il.Emit(OpCodes.Ldloc, loop.FLimit); il.Emit(OpCodes.Ble, take);
                    il.Emit(OpCodes.Br, exit);
                    il.MarkLabel(take);
                    il.Emit(OpCodes.Ldloc, loop.FIndex); il.Emit(OpCodes.Call, FromFloat); Store(i.A + 3);
                    il.Emit(OpCodes.Br, body);
                    il.MarkLabel(exit);
                    break;
                }

                case OpCode.NewBox:
                    Load(i.A); Call(nameof(JitOps.NewBox)); Store(i.A);
                    break;
                case OpCode.GetBox:
                    Load(i.B); Call(nameof(JitOps.GetBox)); Store(i.A);
                    break;
                case OpCode.SetBox:
                    Load(i.A); RK(i.B); Call(nameof(JitOps.SetBox));
                    break;
                case OpCode.Closure:
                {
                    UpvalDesc[] descs = p.Protos[i.B].Upvals;
                    var parentRegs = new List<int>();
                    foreach (UpvalDesc u in descs)
                        if (u.FromParentRegister) parentRegs.Add(u.Index);
                    Cl(); Int(i.B);
                    NewArray(parentRegs.Count, j => Load(parentRegs[j]));
                    Call(nameof(JitOps.MakeClosure));
                    Store(i.A);
                    break;
                }
                case OpCode.VarArg:
                {
                    int want = i.C - 1;
                    Frame(); Int(want); FrameCall(nameof(CompiledFrame.TakeVarargs));
                    if (want < 0)
                    {
                        il.Emit(OpCodes.Stloc, open);
                        openFrom = i.A;
                    }
                    else if (want == 0)
                    {
                        il.Emit(OpCodes.Pop);
                    }
                    else
                    {
                        il.Emit(OpCodes.Stloc, open);
                        for (int j = 0; j < want; j++)
                        {
                            il.Emit(OpCodes.Ldloc, open); Int(j); il.Emit(OpCodes.Ldelem, V);
                            Store(i.A + j);
                        }
                    }
                    break;
                }
                case OpCode.TForCall:
                {
                    L(); Call(nameof(JitOps.Poll));
                    Load(i.A);
                    NewArray(2, j => Load(i.A + 1 + j));
                    Frame(); Int(resumes.Count); Int(0);
                    FrameCall(nameof(CompiledFrame.Request));
                    Frame(); il.Emit(OpCodes.Ldloc, line); il.Emit(OpCodes.Stfld, typeof(CompiledFrame).GetField(nameof(CompiledFrame.Line))!);
                    il.Emit(OpCodes.Br, save);
                    Label resume = il.DefineLabel(); resumes.Add(resume); il.MarkLabel(resume);
                    Frame(); Int(i.C); FrameCall(nameof(CompiledFrame.TakeResults));
                    il.Emit(OpCodes.Stloc, open);
                    for (int j = 0; j < i.C; j++)
                    {
                        il.Emit(OpCodes.Ldloc, open); Int(j); il.Emit(OpCodes.Ldelem, V);
                        Store(i.A + 3 + j);
                    }
                    break;
                }
                case OpCode.TForLoop:
                {
                    Label exit = il.DefineLabel();
                    Load(i.A + 3);
                    il.Emit(OpCodes.Stloc, valueScratch);
                    il.Emit(OpCodes.Ldloca, valueScratch);
                    il.Emit(OpCodes.Call, V.GetProperty(nameof(Value.IsNil))!.GetMethod!);
                    il.Emit(OpCodes.Brtrue, exit);
                    il.Emit(OpCodes.Ldloc, valueScratch);
                    Store(i.A + 2);
                    L(); Call(nameof(JitOps.Poll));
                    il.Emit(OpCodes.Br, At(pc + 1 + i.B));
                    il.MarkLabel(exit);
                    break;
                }
                case OpCode.Tbc:
                    L(); Frame(); Int(i.A); Load(i.A);
                    il.Emit(OpCodes.Call, typeof(JitOps).GetMethod(nameof(JitOps.MarkTbc), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!);
                    break;
                case OpCode.Close:
                    L(); Frame(); Int(i.A);
                    il.Emit(OpCodes.Ldloca, valueScratch); il.Emit(OpCodes.Initobj, V); il.Emit(OpCodes.Ldloc, valueScratch);
                    il.Emit(OpCodes.Call, typeof(JitOps).GetMethod(nameof(JitOps.CloseFrom), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!);
                    break;

                default:
                    throw new NotSupportedException($"it uses the instruction {i.Op}, at line {at}");
            }
        }
        // falling off the end cannot happen (the compiler ends every function with a Return)
        il.MarkLabel(labels[code.Length]);
        Call(nameof(JitOps.Unreachable));
        Return();

        // Snapshot only at call boundaries. Primitive locals are boxed here, never in the
        // arithmetic loop itself. All continuation labels are inside the same try region.
        il.MarkLabel(save);
        LocalBuilder saved = il.DeclareLocal(typeof(object[]));
        Frame(); Int(locals.Count); FrameCall(nameof(CompiledFrame.SaveArea)); il.Emit(OpCodes.Stloc, saved);
        for (int slot = 0; slot < locals.Count; slot++)
        {
            LocalBuilder local = locals[slot];
            il.Emit(OpCodes.Ldloc, saved); Int(slot); il.Emit(OpCodes.Ldloc, local);
            if (local.LocalType.IsValueType) il.Emit(OpCodes.Box, local.LocalType);
            il.Emit(OpCodes.Stelem_Ref);
        }
        Int(-1); Return();

        il.MarkLabel(dispatch);
        Frame(); il.Emit(OpCodes.Ldfld, typeof(CompiledFrame).GetField(nameof(CompiledFrame.Resume))!);
        il.Emit(OpCodes.Brfalse, initialize);
        Frame(); il.Emit(OpCodes.Ldfld, typeof(CompiledFrame).GetField(nameof(CompiledFrame.Saved))!); il.Emit(OpCodes.Stloc, saved);
        for (int slot = 0; slot < locals.Count; slot++)
        {
            LocalBuilder local = locals[slot];
            il.Emit(OpCodes.Ldloc, saved); Int(slot); il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(local.LocalType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, local.LocalType);
            il.Emit(OpCodes.Stloc, local);
        }
        Frame(); il.Emit(OpCodes.Ldfld, typeof(CompiledFrame).GetField(nameof(CompiledFrame.Resume))!);
        il.Emit(OpCodes.Switch, resumes.ToArray());
        Call(nameof(JitOps.Unreachable)); Return();

        // errors get the position of the line that was running
        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Ldstr, p.ChunkName);
        il.Emit(OpCodes.Ldloc, line);
        Call(nameof(JitOps.Locate));
        il.Emit(OpCodes.Throw);
        il.EndExceptionBlock();

        il.MarkLabel(done);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        // Count every saved register and loop/auxiliary local, plus the two unspilled
        // scratch locals. This is a logical storage budget, not a byte-accurate GC limit.
        frameSlots = checked(locals.Count + 2);
        return method.CreateDelegate<CompiledStep>();
    }
}
