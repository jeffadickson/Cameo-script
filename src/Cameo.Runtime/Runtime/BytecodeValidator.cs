namespace Cameo.Runtime.Vm;

/// <summary>
/// Static structural validator for compiled <see cref="Proto"/> bytecode graphs.
/// Verifies opcode validity, register bounds, constant/upvalue/proto indices, and jump targets
/// recursively so malformed or untrusted bytecode cannot cause out-of-bounds VM access.
/// </summary>
public static class BytecodeValidator
{
    /// <summary>Maximum nested function prototype depth accepted by validation.</summary>
    public const int MaxProtoDepth = 200;

    /// <summary>Validates <paramref name="proto"/> and all nested prototypes; returns null when valid,
    /// or a human-readable diagnostic describing the first structural violation.</summary>
    public static string? Validate(Proto? proto)
    {
        if (proto == null) return "prototype is null";
        return ValidateProto(proto, 0);
    }

    /// <summary>Throws <see cref="LuaException"/> when <paramref name="proto"/> fails structural validation.</summary>
    public static void EnsureValid(Proto proto)
    {
        if (Validate(proto) is { } problem)
            throw new LuaException("invalid bytecode: " + problem);
    }

    private static string? ValidateProto(Proto p, int depth)
    {
        if (depth > MaxProtoDepth) return "prototype nesting exceeds maximum depth";
        if (p.Code == null || p.K == null || p.Protos == null || p.Upvals == null)
            return $"prototype '{p.Name}' has null bytecode tables";
        if (p.NumParams < 0 || p.MaxStack < p.NumParams || p.MaxStack > 60_000)
            return $"prototype '{p.Name}' has invalid stack/parameter count (params={p.NumParams}, maxStack={p.MaxStack})";

        int codeLen = p.Code.Length;
        int kLen = p.K.Length;
        int maxStack = p.MaxStack;
        int upLen = p.Upvals.Length;
        int protoLen = p.Protos.Length;

        bool ValidReg(int r) => r >= 0 && r < maxStack;
        bool ValidK(int idx) => idx >= 0 && idx < kLen;
        bool ValidRK(int rk) => rk >= 0 ? rk < maxStack : (~rk) < kLen;
        bool ValidStringK(int idx) => ValidK(idx) && p.K[idx].Kind == ValueKind.String;

        for (int pc = 0; pc < codeLen; pc++)
        {
            Instruction i = p.At(pc);
            switch (i.Op)
            {
                case OpCode.Move:
                case OpCode.Unm:
                case OpCode.BNot:
                case OpCode.Len:
                case OpCode.Not:
                    if (!ValidReg(i.A) || !ValidReg(i.B))
                        return $"pc {pc} ({i.Op}): register out of bounds";
                    break;

                case OpCode.LoadK:
                    if (!ValidReg(i.A) || !ValidK(i.B))
                        return $"pc {pc} (LoadK): register or constant index out of bounds";
                    break;

                case OpCode.LoadBool:
                    if (!ValidReg(i.A))
                        return $"pc {pc} (LoadBool): register out of bounds";
                    if (i.C != 0 && pc + 2 > codeLen)
                        return $"pc {pc} (LoadBool): skip target out of bounds";
                    break;

                case OpCode.LoadNil:
                    if (i.B < 0 || i.A < 0 || i.A + i.B >= maxStack)
                        return $"pc {pc} (LoadNil): register range out of bounds";
                    break;

                case OpCode.LoadInt:
                    if (!ValidReg(i.A))
                        return $"pc {pc} (LoadInt): register out of bounds";
                    break;

                case OpCode.GetUpval:
                    if (!ValidReg(i.A) || i.B < 0 || i.B >= upLen)
                        return $"pc {pc} (GetUpval): register or upvalue index out of bounds";
                    break;

                case OpCode.SetUpval:
                    if (!ValidRK(i.A) || i.B < 0 || i.B >= upLen)
                        return $"pc {pc} (SetUpval): operand or upvalue index out of bounds";
                    break;

                case OpCode.NewBox:
                    if (!ValidReg(i.A))
                        return $"pc {pc} (NewBox): register out of bounds";
                    break;

                case OpCode.GetBox:
                    if (!ValidReg(i.A) || !ValidReg(i.B))
                        return $"pc {pc} (GetBox): register out of bounds";
                    break;

                case OpCode.SetBox:
                    if (!ValidReg(i.A) || !ValidRK(i.B))
                        return $"pc {pc} (SetBox): operand out of bounds";
                    break;

                case OpCode.GetGlobal:
                    if (!ValidReg(i.A) || !ValidStringK(i.B))
                        return $"pc {pc} (GetGlobal): register or string constant out of bounds";
                    break;

                case OpCode.SetGlobal:
                    if (!ValidRK(i.A) || !ValidStringK(i.B))
                        return $"pc {pc} (SetGlobal): operand or string constant out of bounds";
                    break;

                case OpCode.GetTable:
                    if (!ValidReg(i.A) || !ValidReg(i.B) || !ValidRK(i.C))
                        return $"pc {pc} (GetTable): operand out of bounds";
                    break;

                case OpCode.SetTable:
                    if (!ValidReg(i.A) || !ValidRK(i.B) || !ValidRK(i.C))
                        return $"pc {pc} (SetTable): operand out of bounds";
                    break;

                case OpCode.GetField:
                    if (!ValidReg(i.A) || !ValidReg(i.B) || !ValidStringK(i.C))
                        return $"pc {pc} (GetField): operand out of bounds";
                    break;

                case OpCode.SetField:
                    if (!ValidReg(i.A) || !ValidStringK(i.B) || !ValidRK(i.C))
                        return $"pc {pc} (SetField): operand out of bounds";
                    break;

                case OpCode.NewTable:
                    if (!ValidReg(i.A) || i.B < 0 || i.C < 0)
                        return $"pc {pc} (NewTable): invalid register or capacity";
                    break;

                case OpCode.SetList:
                    if (!ValidReg(i.A) || i.B < 0 || i.C < 0 || (i.B > 0 && i.A + i.B >= maxStack))
                        return $"pc {pc} (SetList): register or batch out of bounds";
                    break;

                case OpCode.Self:
                    if (!ValidReg(i.A) || !ValidReg(i.A + 1) || !ValidReg(i.B) || !ValidStringK(i.C))
                        return $"pc {pc} (Self): operand out of bounds";
                    break;

                case OpCode.Add:
                case OpCode.Sub:
                case OpCode.Mul:
                case OpCode.Div:
                case OpCode.IDiv:
                case OpCode.Mod:
                case OpCode.Pow:
                case OpCode.BAnd:
                case OpCode.BOr:
                case OpCode.BXor:
                case OpCode.Shl:
                case OpCode.Shr:
                    if (!ValidReg(i.A) || !ValidRK(i.B) || !ValidRK(i.C))
                        return $"pc {pc} ({i.Op}): operand out of bounds";
                    break;

                case OpCode.Eq:
                case OpCode.Lt:
                case OpCode.Le:
                    if (!ValidRK(i.B) || !ValidRK(i.C))
                        return $"pc {pc} ({i.Op}): operand out of bounds";
                    if (pc + 2 > codeLen)
                        return $"pc {pc} ({i.Op}): skip target out of bounds";
                    break;

                case OpCode.Concat:
                    if (!ValidReg(i.A) || i.B < 0 || i.C < i.B || i.C >= maxStack)
                        return $"pc {pc} (Concat): register range out of bounds";
                    break;

                case OpCode.Jmp:
                case OpCode.JmpIf:
                case OpCode.JmpIfNot:
                    if (i.Op != OpCode.Jmp && !ValidReg(i.A))
                        return $"pc {pc} ({i.Op}): register out of bounds";
                    if (pc + 1 + i.B < 0 || pc + 1 + i.B > codeLen)
                        return $"pc {pc} ({i.Op}): jump target {pc + 1 + i.B} out of bounds [0, {codeLen}]";
                    break;

                case OpCode.Call:
                case OpCode.TailCall:
                    if (!ValidReg(i.A) || i.B < 0 || (i.B > 1 && i.A + i.B - 1 >= maxStack))
                        return $"pc {pc} ({i.Op}): call register window out of bounds";
                    if (i.Op == OpCode.Call && (i.C < 0 || (i.C > 1 && i.A + i.C - 2 >= maxStack)))
                        return $"pc {pc} (Call): result register window out of bounds";
                    break;

                case OpCode.PrepareDotCall:
                    if (!ValidReg(i.A) || !ValidReg(i.A + 1) || i.B < 0 || (i.B > 0 && i.A + i.B - 1 >= maxStack))
                        return $"pc {pc} (PrepareDotCall): register window out of bounds";
                    break;

                case OpCode.Return:
                    if (i.B < 0 || (i.B > 1 && (!ValidReg(i.A) || i.A + i.B - 2 >= maxStack)))
                        return $"pc {pc} (Return): register window out of bounds";
                    break;

                case OpCode.ForPrep:
                case OpCode.ForLoop:
                    if (i.A < 0 || i.A + 3 >= maxStack)
                        return $"pc {pc} ({i.Op}): loop register block out of bounds";
                    if (pc + 1 + i.B < 0 || pc + 1 + i.B > codeLen)
                        return $"pc {pc} ({i.Op}): loop jump target out of bounds";
                    break;

                case OpCode.TForCall:
                    if (i.A < 0 || i.A + 5 >= maxStack || i.C < 1 || i.A + 2 + i.C >= maxStack)
                        return $"pc {pc} (TForCall): iterator register block out of bounds";
                    break;

                case OpCode.TForLoop:
                    if (i.A < 0 || i.A + 3 >= maxStack)
                        return $"pc {pc} (TForLoop): iterator register block out of bounds";
                    if (pc + 1 + i.B < 0 || pc + 1 + i.B > codeLen)
                        return $"pc {pc} (TForLoop): loop jump target out of bounds";
                    break;

                case OpCode.Closure:
                    if (!ValidReg(i.A) || i.B < 0 || i.B >= protoLen)
                        return $"pc {pc} (Closure): register or proto index out of bounds";
                    break;

                case OpCode.VarArg:
                    if (!ValidReg(i.A) || i.C < 0 || (i.C > 1 && i.A + i.C - 2 >= maxStack))
                        return $"pc {pc} (VarArg): register window out of bounds";
                    break;

                case OpCode.IsKind:
                    if (!ValidReg(i.A) || !ValidReg(i.B) || !Enum.IsDefined((TypeKindTest)i.C))
                        return $"pc {pc} (IsKind): operand out of bounds";
                    break;

                case OpCode.MathFn:
                    if (!ValidReg(i.A) || !ValidReg(i.B) || !Enum.IsDefined((MathFn)i.C))
                        return $"pc {pc} (MathFn): operand out of bounds";
                    break;

                case OpCode.Tbc:
                case OpCode.Close:
                    if (!ValidReg(i.A))
                        return $"pc {pc} ({i.Op}): register out of bounds";
                    break;

                case OpCode.Trap:
                    break;

                default:
                    return $"pc {pc}: unknown opcode {(int)i.Op}";
            }
        }

        for (int idx = 0; idx < protoLen; idx++)
        {
            Proto child = p.Protos[idx];
            if (child == null) return $"prototype '{p.Name}': child proto {idx} is null";
            foreach (UpvalDesc u in child.Upvals)
            {
                if (u.FromParentRegister && !ValidReg(u.Index))
                    return $"prototype '{child.Name}': upvalue stack index {u.Index} out of bounds";
                if (!u.FromParentRegister && (u.Index < 0 || u.Index >= upLen))
                    return $"prototype '{child.Name}': upvalue index {u.Index} out of bounds";
            }
            if (ValidateProto(child, depth + 1) is { } err) return err;
        }
        return null;
    }
}
