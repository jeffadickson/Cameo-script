using Cameo.Runtime.Vm;

namespace Cameo.Jit;

/// <summary>Conservative numeric register selection for guarded generated IL.</summary>
internal static class NumericAnalysis
{
    // Finite powerset lattice. Other includes booleans, tables and every non-number;
    // Nil is kept explicit so a read before assignment can never become numeric zero.
    private const byte Integer = 1, Float = 2, Other = 4, Nil = 8, Any = 15;

    internal static ValueKind?[] Analyze(Proto proto, ValueKind?[]? parameterHints = null)
    {
        Instruction[] code = proto.OriginalCode ?? proto.Code;
        int width = proto.MaxStack + 1;
        var result = new ValueKind?[width];
        // Analysis is optional. Bound its O(instructions * registers) storage, including
        // hostile prototypes supplied by a host. Refusing optimization preserves semantics.
        if (code.Length == 0 || (long)width * code.Length > 1_000_000) return result;
        var states = new byte[code.Length][];
        var observed = new byte[width];
        var entry = new byte[width];
        parameterHints ??= proto.NumericParameters;
        Array.Fill(entry, Nil);
        for (int r = 0; r < proto.NumParams; r++)
        {
            entry[r] = r < parameterHints.Length ? Kind(parameterHints[r]) : Any;
            observed[r] |= entry[r];
        }
        states[0] = entry;
        var pending = new Queue<int>();
        var queued = new bool[code.Length];
        pending.Enqueue(0); queued[0] = true;
        void Merge(int pc, byte[] state)
        {
            if ((uint)pc >= (uint)code.Length) return;
            bool changed = false;
            if (states[pc] is not { } old) { states[pc] = (byte[])state.Clone(); changed = true; }
            else for (int r = 0; r < width; r++)
            {
                byte next = (byte)(old[r] | state[r]);
                if (next != old[r]) { old[r] = next; changed = true; }
            }
            if (changed && !queued[pc]) { queued[pc] = true; pending.Enqueue(pc); }
        }
        while (pending.TryDequeue(out int pc))
        {
            queued[pc] = false;
            var state = (byte[])states[pc].Clone();
            Instruction i = code[pc];
            byte Read(int r) { observed[r] |= state[r]; return state[r]; }
            byte Constant(int k) => Kind(proto.K[k].Kind);
            byte RK(int r) => r < 0 ? Constant(~r) : Read(r);
            void Write(int r, byte kind) { state[r] = kind; observed[r] |= kind; }
            void ReadRange(int first, int count) { for (int r = first; r < first + count; r++) Read(r); }
            byte Numeric(byte a, byte b, bool floatOnly = false)
            {
                if ((a & ~3) != 0 || (b & ~3) != 0) return Any;
                if (floatOnly) return Float;
                return a == Integer && b == Integer ? Integer : a == Float || b == Float ? Float : (byte)3;
            }
            byte LoopKind()
            {
                byte init = Read(i.A), step = Read(i.A + 2);
                return Numeric(init, step);
            }
            switch (i.Op)
            {
                case OpCode.Move: Write(i.A, Read(i.B)); break;
                case OpCode.LoadK: Write(i.A, Constant(i.B)); break;
                case OpCode.LoadInt: Write(i.A, Integer); break;
                case OpCode.LoadBool: Write(i.A, Other); break;
                case OpCode.LoadNil:
                    for (int r = i.A; r <= i.A + i.B; r++) Write(r, Nil);
                    break;
                case OpCode.GetGlobal or OpCode.GetUpval: Write(i.A, Any); break;
                case OpCode.SetGlobal or OpCode.SetUpval: RK(i.A); break;
                case OpCode.GetTable: Read(i.B); RK(i.C); Write(i.A, Any); break;
                case OpCode.GetField: Read(i.B); Write(i.A, Any); break;
                case OpCode.SetTable: Read(i.A); RK(i.B); RK(i.C); break;
                case OpCode.SetField: Read(i.A); RK(i.C); break;
                case OpCode.NewTable: Write(i.A, Other); break;
                case OpCode.Self:
                    byte self = Read(i.B); Write(i.A, Any); Write(i.A + 1, self); break;
                case OpCode.Add or OpCode.Sub or OpCode.Mul or OpCode.IDiv or OpCode.Mod:
                    Write(i.A, Numeric(RK(i.B), RK(i.C))); break;
                case OpCode.Div or OpCode.Pow:
                    Write(i.A, Numeric(RK(i.B), RK(i.C), floatOnly: true)); break;
                case OpCode.BAnd or OpCode.BOr or OpCode.BXor or OpCode.Shl or OpCode.Shr:
                    byte numeric = Numeric(RK(i.B), RK(i.C)); Write(i.A, numeric == Any ? Any : Integer); break;
                case OpCode.Unm:
                    byte operand = Read(i.B); Write(i.A, (operand & ~3) == 0 ? operand : Any); break;
                case OpCode.BNot or OpCode.Len: Read(i.B); Write(i.A, Any); break;
                case OpCode.Not or OpCode.IsKind: Read(i.B); Write(i.A, Other); break;
                case OpCode.MathFn: Read(i.B); Write(i.A, Float); break;
                case OpCode.Concat: ReadRange(i.B, i.C - i.B + 1); Write(i.A, Any); break;
                case OpCode.Eq or OpCode.Lt or OpCode.Le: RK(i.B); RK(i.C); break;
                case OpCode.JmpIf or OpCode.JmpIfNot: Read(i.A); break;
                case OpCode.Jmp: break;
                case OpCode.PrepareDotCall:
                    ReadRange(i.A, i.B == 0 ? width - i.A : i.B);
                    // The prepared open array can retain or drop its receiver. Do not specialize shuffled slots.
                    for (int r = i.A + 1; r < (i.B == 0 ? width : i.A + i.B); r++) Write(r, Any);
                    break;
                case OpCode.Call or OpCode.TailCall:
                    ReadRange(i.A, i.B == 0 ? width - i.A : i.B);
                    if (i.Op == OpCode.Call)
                        for (int r = i.A; r < (i.C == 0 ? width : i.A + i.C - 1); r++) Write(r, Any);
                    break;
                case OpCode.Return: ReadRange(i.A, i.B == 0 ? width - i.A : i.B - 1); break;
                case OpCode.SetList: ReadRange(i.A, i.B == 0 ? width - i.A : i.B + 1); break;
                case OpCode.NewBox: Read(i.A); Write(i.A, Other); break;
                case OpCode.GetBox: Read(i.B); Write(i.A, Any); break;
                case OpCode.SetBox: Read(i.A); RK(i.B); break;
                case OpCode.Closure:
                    foreach (UpvalDesc u in proto.Protos[i.B].Upvals)
                        if (u.FromParentRegister) Read(u.Index);
                    Write(i.A, Other);
                    break;
                case OpCode.VarArg:
                    for (int r = i.A; r < (i.C == 0 ? width : i.A + i.C - 1); r++) Write(r, Any);
                    break;
                case OpCode.Tbc: Read(i.A); break;
                case OpCode.Close: break;
                case OpCode.TForCall:
                    ReadRange(i.A, 3);
                    for (int r = i.A + 3; r < i.A + 3 + i.C; r++) Write(r, Any);
                    break;
                case OpCode.TForLoop:
                    Read(i.A + 3);
                    Merge(pc + 1, state);
                    Write(i.A + 2, Any);
                    Merge(pc + 1 + i.B, state);
                    continue;
                case OpCode.ForPrep:
                    ReadRange(i.A, 3);
                    Merge(pc + 1 + i.B, state); // skipped loop never assigns its visible index
                    Write(i.A + 3, LoopKind());
                    Merge(pc + 1, state);
                    continue;
                case OpCode.ForLoop:
                    Merge(pc + 1, state); // exiting preserves the last visible index
                    Write(i.A + 3, LoopKind());
                    Merge(pc + 1 + i.B, state);
                    continue;
                default: return result; // a future opcode is never optimized by accident
            }
            switch (i.Op)
            {
                case OpCode.Return or OpCode.TailCall: break;
                case OpCode.Jmp: Merge(pc + 1 + i.B, state); break;
                case OpCode.JmpIf or OpCode.JmpIfNot:
                    Merge(pc + 1, state); Merge(pc + 1 + i.B, state); break;
                case OpCode.Eq or OpCode.Lt or OpCode.Le:
                    Merge(pc + 1, state); Merge(pc + 2, state); break;
                case OpCode.LoadBool: Merge(pc + (i.C == 0 ? 1 : 2), state); break;
                default: Merge(pc + 1, state); break;
            }
        }
        for (int r = 0; r < width; r++)
            result[r] = observed[r] == Integer ? ValueKind.Integer : observed[r] == Float ? ValueKind.Float : null;
        return result;
    }

    private static byte Kind(ValueKind? kind) => kind switch
    {
        ValueKind.Integer => Integer,
        ValueKind.Float => Float,
        null => Any,
        ValueKind.Nil => Nil,
        _ => Other,
    };
}
