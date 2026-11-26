using System.Runtime.CompilerServices;

namespace Cameo.Runtime.Vm;

internal enum ExecStatus { Done, Yield }

public sealed partial class LuaState
{
    /// <summary>
    /// Runs the frame on top of <paramref name="th"/> (and everything it calls) until the frame
    /// count drops back to <paramref name="baseDepth"/>. Results of the bottom frame are left at
    /// its RetDest with th.Top marking their end.
    /// </summary>
    internal ExecStatus Execute(LuaThread th, int baseDepth)
    {
        if (++_nativeDepth > MaxNativeDepth)
        {
            _nativeDepth--;
            PopFrames(th, baseDepth);
            throw new LuaException("stack overflow (native call depth)");
        }
        th.ExecDepth++;
        try
        {
            return ExecuteLoop(th, baseDepth);
        }
        finally
        {
            th.ExecDepth--;
            _nativeDepth--;
        }
    }

    // Error handling lives here, outside the hot loop, so that the JIT can keep the
    // interpreter's state (pc, stack, base) in machine registers.
    private ExecStatus ExecuteLoop(LuaThread th, int baseDepth)
    {
        while (true)
        {
            try
            {
                if (th.HasPendingThrow)
                {
                    // resumed with an error (a task whose awaited future failed): raise it here,
                    // inside the coroutine, so that its own pcall frames can catch it
                    th.HasPendingThrow = false;
                    Value pending = th.PendingThrow;
                    th.PendingThrow = default;
                    throw new LuaException(pending, true);
                }
                return RunFrames(th, baseDepth);
            }
            catch (LuaAbortException)
            {
                // host abort: not an error the program may handle; drop this activation's frames
                PopFrames(th, baseDepth);
                throw;
            }
            catch (Exception e) when (e is not LuaExitException)
            {
                // Runtime error. Add position info once, then unwind to the nearest pcall
                // frame owned by this activation, or propagate to the caller.
                LuaException le = e as LuaException ?? new LuaException(Value.FromString(DescribeHostException(e)), false);
                if (!le.Traced)
                {
                    le.Traced = true;
                    if (th.FrameCount > baseDepth)
                    {
                        ref Frame top = ref th.Frames[th.FrameCount - 1];
                        Proto p = top.Cl.Proto;
                        int pc = th.SavedPC;
                        top.PC = pc;
                        if (le.Value.IsString)
                        {
                            string info = le.Category != ErrorCategory.None ? OperandInfo(p, pc - 1, le.Category, th.Stack, top.Base) : "";
                            le.Value = Value.FromString($"{p.ChunkName}:{p.LineOf(pc - 1)}: {le.Value.AsString}{info}");
                        }
                        le.CaptureTraceback(th);
                    }
                }
                int h = -1;
                for (int fi = th.FrameCount - 1; fi > baseDepth; fi--)
                    if (th.Frames[fi].Protected) { h = fi; break; }
                if (h < 0)
                {
                    if (th.FrameCount > baseDepth && th.TbcCount > th.Frames[baseDepth].TbcBase) CloseTbcOnUnwind(th, baseDepth, le);
                    PopFrames(th, baseDepth);
                    if (ReferenceEquals(le, e)) throw;
                    throw le;
                }
                if (th.TbcCount > th.Frames[h].TbcBase) CloseTbcOnUnwind(th, h, le);
                int dest = th.Frames[h].RetDest;
                int want = th.Frames[h].NResults;
                bool hasHandler = th.Frames[h].HasHandler;
                PopFrames(th, h);
                if (hasHandler)
                {
                    FinishXpcall(th, dest, want, le);
                    continue;
                }
                var s = th.Stack;
                if (want != 0) s[dest] = Value.False;
                if (want < 0)
                {
                    s[dest + 1] = le.Value;
                    th.Top = dest + 2;
                }
                else if (want >= 2)
                {
                    s[dest + 1] = le.Value;
                    for (int j = 2; j < want; j++) s[dest + j] = default;
                }
                // loop: resume the frame that called pcall
            }
        }
    }

    // The hot loop. It deliberately contains no exception handling and delegates every
    // slow or rare path to a separate non-inlined method: the JIT only produces good code
    // for this method while its number of locals and temporaries stays small.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ExecStatus RunFrames(LuaThread th, int baseDepth)
    {
        Closure cl;
        Proto p;
        Instruction[] code;
        Value[] k;
        Value[] stack;
        int @base;
        int pc;
        int callRa, callNargs, callNres;

        // Outer loop: one iteration per frame switch (call, return, metamethod frame).
        // `continue frames` (C# 15 labeled continue) re-enters it to load the new top frame.
        frames: while (true)
        {
            {
                ref Frame fr = ref th.Frames[th.FrameCount - 1];
                cl = fr.Cl;
                p = cl.Proto;
                code = p.Code;
                k = p.K;
                @base = fr.Base;
                pc = fr.PC;
                stack = th.Stack;
                th.CallTop = @base + p.MaxStack;
            }

        while (true)
        {
            ref readonly Instruction i = ref code[pc++];
            dispatch:
            switch (i.Op)
            {
                case OpCode.Trap:
                    // A debugger marked this instruction as the start of a line: report it (the
                    // debugger may hold the program here), then run the real instruction.
                    th.SavedPC = pc;
                    th.Frames[th.FrameCount - 1].PC = pc;
                    DebugTrap(th, p, pc - 1);
                    i = ref p.OriginalCode![pc - 1];
                    goto dispatch;
                case OpCode.Move:
                    Value.Copy(ref stack[@base + i.A], stack[@base + i.B]);
                    continue;
                case OpCode.LoadK:
                    Value.Copy(ref stack[@base + i.A], k[i.B]);
                    continue;
                case OpCode.LoadNil:
                    {
                        int a = @base + i.A;
                        for (int j = 0; j <= i.B; j++) stack[a + j] = default;
                        continue;
                    }
                case OpCode.LoadBool:
                    Value.SetBool(ref stack[@base + i.A], i.B != 0);
                    if (i.C != 0) pc++;
                    continue;
                case OpCode.LoadInt:
                    Value.SetInt(ref stack[@base + i.A], i.B);
                    continue;
                case OpCode.GetUpval:
                    Value.Copy(ref stack[@base + i.A], cl.Upvals[i.B].Value);
                    continue;
                case OpCode.SetUpval:
                    Value.Copy(ref cl.Upvals[i.B].Value, in (i.A >= 0 ? ref stack[@base + i.A] : ref k[~i.A]));
                    continue;
                case OpCode.NewBox:
                    stack[@base + i.A] = Value.FromBox(new Box(stack[@base + i.A]));
                    continue;
                case OpCode.GetBox:
                    Value.Copy(ref stack[@base + i.A], Unsafe.As<Box>(stack[@base + i.B].O!).Value);
                    continue;
                case OpCode.SetBox:
                    Value.Copy(ref Unsafe.As<Box>(stack[@base + i.A].O!).Value, in (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]));
                    continue;
                case OpCode.GetGlobal:
                    Value.Copy(ref stack[@base + i.A], Globals.GetStrRef(Unsafe.As<string>(k[i.B].O!), p.KHash[i.B]));
                    continue;
                case OpCode.SetGlobal:
                    Globals.SetStr(Unsafe.As<string>(k[i.B].O!), p.KHash[i.B], i.A >= 0 ? stack[@base + i.A] : k[~i.A]);
                    continue;

                case OpCode.GetTable:
                    {
                        ref readonly Value o = ref stack[@base + i.B];
                        if (o.Kind == ValueKind.Table)
                        {
                            Table t = Unsafe.As<Table>(o.O!);
                            ref readonly Value key = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                            if (t.Guarded && (t.GuardTag == null || !ReferenceEquals(t.GuardTag, p.ClassTag))) { th.SavedPC = pc; GuardCheck(t, key, p.ClassTag); }
                            ref readonly Value v = ref (key.Kind == ValueKind.Integer ? ref t.GetIntRef(key.I) : ref t.GetRef(key));
                            if (!v.IsNil || t.Metatable == null)
                            {
                                Value.Copy(ref stack[@base + i.A], v);
                                continue;
                            }
                        }
                        else if (o.Kind == ValueKind.Userdata
                            && ArrayGet(in o, in (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]), ref stack[@base + i.A])) continue;
                        th.SavedPC = pc;
                        th.Frames[th.FrameCount - 1].PC = pc;
                        if (OpIndexSlow(th, @base, k, in i)) continue frames;
                        stack = th.Stack;
                        continue;
                    }
                case OpCode.GetField:
                    {
                        ref readonly Value o = ref stack[@base + i.B];
                        if (o.Kind == ValueKind.Table)
                        {
                            Table t = Unsafe.As<Table>(o.O!);
                            // an object of a class with hidden members, used by code of another class (or none)
                            if (t.Guarded && (t.GuardTag == null || !ReferenceEquals(t.GuardTag, p.ClassTag))) { th.SavedPC = pc; GuardCheck(t, k[i.C], p.ClassTag); }
                            ref readonly Value v = ref t.GetStrChainRef(Unsafe.As<string>(k[i.C].O!), p.KHash[i.C], out bool slow);
                            if (!slow)
                            {
                                Value.Copy(ref stack[@base + i.A], v);
                                continue;
                            }
                        }
                        th.SavedPC = pc;
                        th.Frames[th.FrameCount - 1].PC = pc;
                        if (OpIndexSlow(th, @base, k, in i)) continue frames;
                        stack = th.Stack;
                        continue;
                    }
                case OpCode.Self:
                    {
                        ref readonly Value o = ref stack[@base + i.B];
                        if (o.Kind == ValueKind.Table)
                        {
                            Table t = Unsafe.As<Table>(o.O!);
                            // an object of a class with hidden members, used by code of another class (or none)
                            if (t.Guarded && (t.GuardTag == null || !ReferenceEquals(t.GuardTag, p.ClassTag))) { th.SavedPC = pc; GuardCheck(t, k[i.C], p.ClassTag); }
                            ref readonly Value v = ref t.GetStrChainRef(Unsafe.As<string>(k[i.C].O!), p.KHash[i.C], out bool slow);
                            if (!slow)
                            {
                                // A+1 first: B may equal A
                                stack[@base + i.A + 1] = o;
                                Value.Copy(ref stack[@base + i.A], v);
                                continue;
                            }
                        }
                        th.SavedPC = pc;
                        th.Frames[th.FrameCount - 1].PC = pc;
                        if (OpIndexSlow(th, @base, k, in i)) continue frames;
                        stack = th.Stack;
                        continue;
                    }
                case OpCode.SetTable:
                    {
                        ref readonly Value o = ref stack[@base + i.A];
                        if (o.Kind == ValueKind.Table)
                        {
                            Table t = Unsafe.As<Table>(o.O!);
                            if (t.Metatable == null)
                            {
                                ref readonly Value key = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                                ref readonly Value val = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                                th.SavedPC = pc;
                                if (key.Kind == ValueKind.Integer) t.SetInt(key.I, val); else t.Set(key, val);
                                continue;
                            }
                        }
                        else if (o.Kind == ValueKind.Userdata
                            && ArraySet(in o, in (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]), in (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]))) continue;
                        th.SavedPC = pc;
                        th.Frames[th.FrameCount - 1].PC = pc;
                        if (OpSetIndexSlow(th, @base, k, in i)) continue frames;
                        stack = th.Stack;
                        continue;
                    }
                case OpCode.SetField:
                    {
                        ref readonly Value o = ref stack[@base + i.A];
                        if (o.Kind == ValueKind.Table)
                        {
                            Table t = Unsafe.As<Table>(o.O!);
                            if (t.Metatable == null)
                            {
                                t.SetStr(Unsafe.As<string>(k[i.B].O!), p.KHash[i.B], in (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]));
                                continue;
                            }
                        }
                        th.SavedPC = pc;
                        th.Frames[th.FrameCount - 1].PC = pc;
                        if (OpSetIndexSlow(th, @base, k, in i)) continue frames;
                        stack = th.Stack;
                        continue;
                    }
                case OpCode.NewTable:
                    stack[@base + i.A] = Value.FromTable(new Table(i.B, i.C));
                    continue;

                case OpCode.Add:
                    {
                        ref readonly Value b = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                        ref readonly Value c = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                        if (b.Kind == ValueKind.Integer && c.Kind == ValueKind.Integer)
                        {
                            Value.SetInt(ref stack[@base + i.A], unchecked(b.I + c.I));
                            continue;
                        }
                        if (b.Kind == ValueKind.Float && c.Kind == ValueKind.Float)
                        {
                            Value.SetFloat(ref stack[@base + i.A], b.N + c.N);
                            continue;
                        }
                        goto arithSlow;
                    }
                case OpCode.Sub:
                    {
                        ref readonly Value b = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                        ref readonly Value c = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                        if (b.Kind == ValueKind.Integer && c.Kind == ValueKind.Integer)
                        {
                            Value.SetInt(ref stack[@base + i.A], unchecked(b.I - c.I));
                            continue;
                        }
                        if (b.Kind == ValueKind.Float && c.Kind == ValueKind.Float)
                        {
                            Value.SetFloat(ref stack[@base + i.A], b.N - c.N);
                            continue;
                        }
                        goto arithSlow;
                    }
                case OpCode.Mul:
                    {
                        ref readonly Value b = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                        ref readonly Value c = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                        if (b.Kind == ValueKind.Integer && c.Kind == ValueKind.Integer)
                        {
                            Value.SetInt(ref stack[@base + i.A], unchecked(b.I * c.I));
                            continue;
                        }
                        if (b.Kind == ValueKind.Float && c.Kind == ValueKind.Float)
                        {
                            Value.SetFloat(ref stack[@base + i.A], b.N * c.N);
                            continue;
                        }
                        goto arithSlow;
                    }
                case OpCode.Div:
                    {
                        ref readonly Value b = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                        ref readonly Value c = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                        if (b.Kind == ValueKind.Float && c.Kind == ValueKind.Float)
                        {
                            Value.SetFloat(ref stack[@base + i.A], b.N / c.N);
                            continue;
                        }
                        goto arithSlow;
                    }
                case OpCode.Mod:
                    {
                        ref readonly Value b = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                        ref readonly Value c = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                        if (b.Kind == ValueKind.Integer && c.Kind == ValueKind.Integer && c.I > 0)
                        {
                            long m = b.I % c.I;
                            if (m < 0) m += c.I;
                            Value.SetInt(ref stack[@base + i.A], m);
                            continue;
                        }
                        goto arithSlow;
                    }
                case OpCode.Pow:
                case OpCode.IDiv:
                case OpCode.BAnd:
                case OpCode.BOr:
                case OpCode.BXor:
                case OpCode.Shl:
                case OpCode.Shr:
                case OpCode.Unm:
                case OpCode.Len:
                case OpCode.BNot:
                case OpCode.Concat:
                arithSlow:
                    th.SavedPC = pc;
                    th.Frames[th.FrameCount - 1].PC = pc;
                    if (OpArithSlow(th, @base, k, in i)) continue frames;
                    stack = th.Stack;
                    continue;
                case OpCode.Not:
                    Value.SetBool(ref stack[@base + i.A], !stack[@base + i.B].IsTruthy);
                    continue;

                // Backward jumps (loops) and calls are where a host abort or step budget is noticed.
                case OpCode.Jmp:
                    pc += i.B;
                    if (i.B < 0 && PollStep()) ThrowAbort();
                    continue;
                case OpCode.JmpIf:
                    if (stack[@base + i.A].IsTruthy)
                    {
                        pc += i.B;
                        if (i.B < 0 && PollStep()) ThrowAbort();
                    }
                    continue;
                case OpCode.JmpIfNot:
                    if (!stack[@base + i.A].IsTruthy)
                    {
                        pc += i.B;
                        if (i.B < 0 && PollStep()) ThrowAbort();
                    }
                    continue;
                case OpCode.Eq:
                    {
                        ref readonly Value b = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                        ref readonly Value c = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                        bool r;
                        if (b.Kind == ValueKind.Integer && c.Kind == ValueKind.Integer) r = b.I == c.I;
                        else if (b.Kind != c.Kind && !(b.IsNumber && c.IsNumber)) r = false;
                        else
                        {
                            th.SavedPC = pc;
                            th.Frames[th.FrameCount - 1].PC = pc;
                            int cr = OpCompareSlow(th, @base, k, in i);
                            if (cr < 0) continue frames; // a Lua metamethod was called; it finishes the comparison
                            r = cr != 0;
                            stack = th.Stack;
                        }
                        if (r != (i.A != 0)) pc++;
                        continue;
                    }
                case OpCode.Lt:
                    {
                        ref readonly Value b = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                        ref readonly Value c = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                        bool r;
                        if (b.Kind == ValueKind.Integer && c.Kind == ValueKind.Integer) r = b.I < c.I;
                        else if (b.Kind == ValueKind.Float && c.Kind == ValueKind.Float) r = b.N < c.N;
                        else
                        {
                            th.SavedPC = pc;
                            th.Frames[th.FrameCount - 1].PC = pc;
                            int cr = OpCompareSlow(th, @base, k, in i);
                            if (cr < 0) continue frames; // a Lua metamethod was called; it finishes the comparison
                            r = cr != 0;
                            stack = th.Stack;
                        }
                        if (r != (i.A != 0)) pc++;
                        continue;
                    }
                case OpCode.Le:
                    {
                        ref readonly Value b = ref (i.B >= 0 ? ref stack[@base + i.B] : ref k[~i.B]);
                        ref readonly Value c = ref (i.C >= 0 ? ref stack[@base + i.C] : ref k[~i.C]);
                        bool r;
                        if (b.Kind == ValueKind.Integer && c.Kind == ValueKind.Integer) r = b.I <= c.I;
                        else if (b.Kind == ValueKind.Float && c.Kind == ValueKind.Float) r = b.N <= c.N;
                        else
                        {
                            th.SavedPC = pc;
                            th.Frames[th.FrameCount - 1].PC = pc;
                            int cr = OpCompareSlow(th, @base, k, in i);
                            if (cr < 0) continue frames; // a Lua metamethod was called; it finishes the comparison
                            r = cr != 0;
                            stack = th.Stack;
                        }
                        if (r != (i.A != 0)) pc++;
                        continue;
                    }

                case OpCode.PrepareDotCall:
                    th.Top = DotCallPreparation.PrepareRegisters(stack, @base + i.A,
                        i.B == 0 ? th.Top : @base + i.A + i.B);
                    continue;

                case OpCode.Call:
                    callRa = @base + i.A;
                    callNargs = i.B != 0 ? i.B - 1 : th.Top - callRa - 1;
                    callNres = i.C - 1;
                    break;

                case OpCode.TailCall:
                    {
                        int ra = @base + i.A;
                        int nargs = i.B != 0 ? i.B - 1 : th.Top - ra - 1;
                        if (stack[ra].O is Closure tcl)
                        {
                            th.SavedPC = pc;
                            if (PollStep()) ThrowAbort(); // an endless tail call is a loop too
                            OpTailCall(th, tcl, ra, @base, nargs);
                            continue frames;
                        }
                        // Not a Lua closure: ordinary call keeping all results; the Return that follows finishes.
                        callRa = ra;
                        callNargs = nargs;
                        callNres = -1;
                        break;
                    }

                case OpCode.Return:
                    {
                        int ra = @base + i.A;
                        int n = i.B != 0 ? i.B - 1 : th.Top - ra;
                        ref Frame fr = ref th.Frames[th.FrameCount - 1];
                        if (fr.Finish != 0)
                        {
                            FinishCompare(th, n > 0 && stack[ra].IsTruthy);
                            continue frames;
                        }
                        int dest = fr.RetDest;
                        int want = fr.NResults;
                        bool prot = fr.Protected;
                        fr.Cl = null!;
                        if (fr.Varargs.Length != 0) th.VarargSlots -= fr.Varargs.Length;
                        fr.Varargs = null!;
                        th.FrameCount--;
                        if (prot && want != 0)
                        {
                            stack[dest++] = Value.True;
                            if (want > 0) want--;
                        }
                        if (want == 1)
                        {
                            if (n > 0) Value.Copy(ref stack[dest], stack[ra]); else stack[dest] = default;
                        }
                        else if (want < 0)
                        {
                            if (n == 1) Value.Copy(ref stack[dest], stack[ra]);
                            else if (n > 0) Array.Copy(stack, ra, stack, dest, n);
                            th.Top = dest + n;
                        }
                        else
                        {
                            int m = n < want ? n : want;
                            for (int j = 0; j < m; j++) stack[dest + j] = stack[ra + j];
                            for (int j = m; j < want; j++) stack[dest + j] = default;
                        }
                        if (th.FrameCount == baseDepth) return ExecStatus.Done;
                        continue frames;
                    }

                case OpCode.ForPrep:
                    th.SavedPC = pc;
                    if (OpForPrep(stack, @base + i.A)) pc += i.B;
                    continue;
                // Verified: verification/ForLoop.dfy, method RunLoop (the integer branch below)
                case OpCode.ForLoop:
                    {
                        int ra = @base + i.A;
                        ref Value idx = ref stack[ra];
                        if (idx.Kind == ValueKind.Integer)
                        {
                            ulong count = (ulong)stack[ra + 1].I;
                            if (count > 0)
                            {
                                long nv = unchecked(idx.I + stack[ra + 2].I);
                                Value.SetIntPayload(ref stack[ra + 1], unchecked((long)(count - 1)));
                                Value.SetIntPayload(ref idx, nv);
                                Value.SetInt(ref stack[ra + 3], nv);
                                pc += i.B;
                                if (PollStep()) ThrowAbort();
                            }
                        }
                        else
                        {
                            double st = stack[ra + 2].N;
                            double v = idx.N + st;
                            double lim = stack[ra + 1].N;
                            if (st > 0 ? v <= lim : lim <= v)
                            {
                                Value.SetFloat(ref idx, v);
                                Value.SetFloat(ref stack[ra + 3], v);
                                pc += i.B;
                                if (PollStep()) ThrowAbort();
                            }
                        }
                        continue;
                    }
                case OpCode.TForCall:
                    {
                        int ra = @base + i.A;
                        stack[ra + 3] = stack[ra];
                        stack[ra + 4] = stack[ra + 1];
                        stack[ra + 5] = stack[ra + 2];
                        callRa = ra + 3;
                        callNargs = 2;
                        callNres = i.C;
                        break;
                    }
                case OpCode.TForLoop:
                    {
                        int ra = @base + i.A;
                        if (!stack[ra + 3].IsNil)
                        {
                            Value.Copy(ref stack[ra + 2], stack[ra + 3]);
                            pc += i.B;
                        }
                        continue;
                    }
                case OpCode.SetList:
                    {
                        int ra = @base + i.A;
                        int n = i.B != 0 ? i.B : th.Top - ra - 1;
                        Unsafe.As<Table>(stack[ra].O!).SetRange(i.C, stack.AsSpan(ra + 1, n));
                        continue;
                    }
                case OpCode.Closure:
                    OpClosure(stack, @base, cl, p.Protos[i.B], i.A);
                    continue;
                case OpCode.VarArg:
                    OpVarArg(th, @base + i.A, i.C - 1);
                    stack = th.Stack;
                    continue;
                case OpCode.IsKind:
                    Value.SetBool(ref stack[@base + i.A], IsKind(stack[@base + i.B], (TypeKindTest)i.C));
                    continue;
                case OpCode.MathFn:
                    {
                        ref readonly Value b = ref stack[@base + i.B];
                        double x;
                        if (b.Kind == ValueKind.Float) x = b.N;
                        else if (b.Kind == ValueKind.Integer) x = b.I;
                        else
                        {
                            th.SavedPC = pc;
                            x = MathArgSlow(b, (MathFn)i.C);
                        }
                        Value.SetFloat(ref stack[@base + i.A], MathBuiltins.Apply((MathFn)i.C, x));
                        continue;
                    }
                case OpCode.Tbc:
                    th.SavedPC = pc;
                    MarkTbc(th, @base + i.A, p, pc, i.A);
                    continue;
                case OpCode.Close:
                    th.SavedPC = pc;
                    th.Frames[th.FrameCount - 1].PC = pc;
                    OpClose(th, @base + i.A, i.B != 0, @base + p.MaxStack);
                    stack = th.Stack;
                    continue;
                default:
                    th.SavedPC = pc;
                    throw new LuaException("invalid opcode " + i.Op);
            }

            // ---- call (reached by `break` from Call / TailCall / TForCall) ----
            th.SavedPC = pc;
            if (PollStep()) ThrowAbort();
            object? fo = stack[callRa].O;
            if (fo is Closure ncl)
            {
                if (AutoTierCompiler != null && TryInvokeTiered(th, ncl, pc, callRa, callNargs, callNres, @base + p.MaxStack))
                {
                    stack = th.Stack;
                    continue;
                }
                th.Frames[th.FrameCount - 1].PC = pc;
                PushLuaFrame(th, ncl, callRa + 1, callNargs, callRa, callNres, false);
                continue frames;
            }
            th.Frames[th.FrameCount - 1].PC = pc;
            int status = CallNonLua(th, callRa, callNargs, callNres, @base + p.MaxStack);
            stack = th.Stack;
            if (status == CallPushedFrame) continue frames;
            if (status == CallYielded) return ExecStatus.Yield;
        }
        }
    }

    private const int CallDone = 0, CallPushedFrame = 1, CallYielded = 2;

    // Hot-function automatic IL tiering: only fires on the non-yielding main thread with no
    // debugger attached, once a Proto's call count reaches JitTierThreshold.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryInvokeTiered(LuaThread th, Closure ncl, int pc, int callRa, int nargs, int nres, int frameTop)
    {
        if (JitTierThreshold <= 0 || Debugger != null || !th.IsMain) return false;
        NativeFunction? nf = ncl.TieredFunction;
        if (nf == null)
        {
            Proto p = ncl.Proto;
            if (p.TierAttempted && p.TierBody == null) return false;
            if (p.TierBody == null && ++p.CallCount < JitTierThreshold) return false;
            nf = AutoTierCompiler!(ncl);
            if (nf == null) return false;
            ncl.TieredFunction = nf;
        }
        th.Frames[th.FrameCount - 1].PC = pc;
        th.CallTop = callRa + 1 + nargs;
        if (th.CallTop + 8 > th.Stack.Length) th.EnsureStack(th.CallTop + 8);
        var ctx = new CallContext(this, th, nf, callRa + 1, nargs);
        DirectNativeCall = true;
        int n = nf.Fn(ref ctx);
        Value[] stack = th.Stack;
        th.CallTop = frameTop;
        if (nres == 1)
        {
            if (n > 0) stack[callRa] = stack[callRa + 1]; else stack[callRa] = default;
        }
        else if (nres < 0)
        {
            if (n > 0) Array.Copy(stack, callRa + 1, stack, callRa, n);
            th.Top = callRa + n;
        }
        else
        {
            int m = n < nres ? n : nres;
            for (int j = 0; j < m; j++) stack[callRa + j] = stack[callRa + 1 + j];
            for (int j = m; j < nres; j++) stack[callRa + j] = default;
        }
        return true;
    }

    // Calls a value that is not a plain Lua closure: a native function (including the inline
    // pcall protocol) or an object with a __call metamethod.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CallNonLua(LuaThread th, int callRa, int nargs, int nres, int frameTop)
    {
        for (int callChain = 0; ; callChain++)
        {
            if (callChain > MaxCallChain) throw new LuaException("'__call' chain too long; possible loop");
            Value[] stack = th.Stack;
            object? fo = stack[callRa].O;
            if (fo is NativeFunction nf)
            {
                if (nf.Special == SpecialPcall && nargs >= 1 && stack[callRa + 1].O is Closure pcl)
                {
                    PushLuaFrame(th, pcl, callRa + 2, nargs - 1, callRa, nres, true);
                    return CallPushedFrame;
                }
                if (nf.Special == SpecialXpcall && nargs >= 2 && stack[callRa + 1].O is Closure xcl)
                {
                    // xpcall(f, handler, ...): keep the handler at callRa+1 and run f as a protected frame
                    stack[callRa + 1] = stack[callRa + 2];
                    stack[callRa + 2] = Value.FromFunction(xcl);
                    PushLuaFrame(th, xcl, callRa + 3, nargs - 2, callRa, nres, true);
                    th.Frames[th.FrameCount - 1].HasHandler = true;
                    return CallPushedFrame;
                }
                th.CallTop = callRa + 1 + nargs;
                if (th.CallTop + 8 > stack.Length) th.EnsureStack(th.CallTop + 8);
                var ctx = new CallContext(this, th, nf, callRa + 1, nargs);
                DirectNativeCall = true;
                int n = nf.Fn(ref ctx);
                stack = th.Stack;
                th.CallTop = frameTop;
                if (n < 0)
                {
                    if (th.IsMain) throw new LuaException("attempt to yield from outside a coroutine");
                    if (th.ExecDepth != 1) throw new LuaException("attempt to yield across a native-call boundary");
                    th.ResultBase = callRa + 1;
                    th.ResultCount = ~n;
                    return CallYielded;
                }
                if (nres == 1)
                {
                    if (n > 0) stack[callRa] = stack[callRa + 1]; else stack[callRa] = default;
                }
                else if (nres < 0)
                {
                    if (n > 0) Array.Copy(stack, callRa + 1, stack, callRa, n);
                    th.Top = callRa + n;
                }
                else
                {
                    int m = n < nres ? n : nres;
                    for (int j = 0; j < m; j++) stack[callRa + j] = stack[callRa + 1 + j];
                    for (int j = m; j < nres; j++) stack[callRa + j] = default;
                }
                return CallDone;
            }
            if (fo is Closure cl)
            {
                // reached after a __call metamethod resolved to a Lua function
                PushLuaFrame(th, cl, callRa + 1, nargs, callRa, nres, false);
                return CallPushedFrame;
            }
            Value fv = stack[callRa];
            Value h = GetMetamethod(fv, Meta.Call);
            if (h.IsNil) throw new LuaException($"attempt to call a {TypeNameOf(fv)} value") { Category = ErrorCategory.Call };
            th.EnsureStack(callRa + nargs + 10);
            stack = th.Stack;
            Array.Copy(stack, callRa, stack, callRa + 1, nargs + 1);
            stack[callRa] = h;
            nargs++;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpClose(LuaThread th, int fromIndex, bool openResults, int frameTop)
    {
        int keep = th.Frames[th.FrameCount - 1].TbcBase;
        if (th.TbcCount <= keep) return;
        // __close handlers are called above everything that is live, including a pending
        // open result list (return f() inside the scope of a <close> variable).
        int savedTop = th.Top;
        if (openResults && savedTop > th.CallTop) th.CallTop = savedTop;
        try
        {
            CloseTbc(th, keep, fromIndex, default);
        }
        finally
        {
            th.CallTop = frameTop;
            th.Top = savedTop;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpTailCall(LuaThread th, Closure tcl, int ra, int @base, int nargs)
    {
        Proto tp = tcl.Proto;
        Value[] stack = th.Stack;
        for (int j = 0; j <= nargs; j++) Value.Copy(ref stack[@base - 1 + j], stack[ra + j]);
        th.EnsureStack(@base + tp.MaxStack + 8);
        stack = th.Stack;
        Value[] va = Array.Empty<Value>();
        if (nargs > tp.NumParams)
        {
            if (tp.IsVarArg)
            {
                va = new Value[nargs - tp.NumParams];
                Array.Copy(stack, @base + tp.NumParams, va, 0, va.Length);
            }
        }
        else
        {
            for (int j = nargs; j < tp.NumParams; j++) stack[@base + j] = default;
        }
        ref Frame fr = ref th.Frames[th.FrameCount - 1];
        th.VarargSlots += va.Length - fr.Varargs.Length;
        fr.Cl = tcl;
        fr.PC = 0;
        fr.Varargs = va;
    }

    // ---- metamethods as frames ------------------------------------------------------------
    // When a metamethod is a Lua function it is run as an ordinary frame of the current
    // interpreter activation instead of a nested one. That keeps the native stack flat and,
    // more importantly, lets a coroutine yield from inside a metamethod.

    // Pushes a frame calling Lua closure h with the given arguments; its results (nres of them)
    // go to stack[retDest..].
    private void PushMetaFrame(LuaThread th, Value h, int nargs, in Value a, in Value b, in Value c, int retDest, int nres, byte finish)
    {
        int top = th.CallTop;
        th.EnsureStack(top + 5);
        Value[] stack = th.Stack;
        stack[top] = h;
        stack[top + 1] = a;
        if (nargs > 1) stack[top + 2] = b;
        if (nargs > 2) stack[top + 3] = c;
        PushLuaFrame(th, Unsafe.As<Closure>(h.O!), top + 1, nargs, retDest, nres, false);
        th.Frames[th.FrameCount - 1].Finish = finish;
    }

    // A comparison metamethod returned: complete the Eq/Lt/Le instruction of the caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FinishCompare(LuaThread th, bool result)
    {
        ref Frame fr = ref th.Frames[th.FrameCount - 1];
        fr.Cl = null!;
        if (fr.Varargs.Length != 0) th.VarargSlots -= fr.Varargs.Length;
        fr.Varargs = null!;
        th.FrameCount--;
        ref Frame caller = ref th.Frames[th.FrameCount - 1];
        Instruction ins = caller.Cl.Proto.At(caller.PC - 1);
        if (result != (ins.A != 0)) caller.PC++;
    }

    // An error reached an xpcall frame: run the message handler and deliver false + its results.
    private void FinishXpcall(LuaThread th, int dest, int want, LuaException le)
    {
        Value handler = th.Stack[dest + 1];
        Value[] hr;
        string? savedTb = PendingTraceback;
        PendingTraceback = le.Traceback;
        th.CallTop = dest + 2;
        try
        {
            hr = Call(handler, le.Value);
            if (hr.Length > 1) hr = new[] { hr[0] }; // a message handler yields a single value
            else if (hr.Length == 0) hr = new[] { Value.Nil };
        }
        catch (LuaException e)
        {
            hr = new[] { e.Value }; // error in the message handler
        }
        finally
        {
            PendingTraceback = savedTb;
        }
        th.EnsureStack(dest + hr.Length + 2);
        var s = th.Stack;
        if (want != 0) s[dest] = Value.False;
        if (want < 0)
        {
            hr.CopyTo(s, dest + 1);
            th.Top = dest + 1 + hr.Length;
        }
        else
        {
            for (int j = 1; j < want; j++) s[dest + j] = j - 1 < hr.Length ? hr[j - 1] : default;
        }
    }

    // GetTable / GetField / Self when the fast path does not apply (metatables, non-tables).
    // Returns true if a frame for a Lua __index function was pushed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DebugTrap(LuaThread th, Proto p, int pc) => Debugger?.OnLine(this, th, p, pc);

    // a[i] on a numeric array (FloatArray), kept out of the interpreter loop. False means
    // "not that case": the general path then handles it (methods, out-of-range writes, errors).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ArrayGet(in Value o, in Value key, ref Value dest)
    {
        if (key.Kind != ValueKind.Integer || o.O is not Stdlib.FloatArray fa) return false;
        double[] data = fa.Data;
        dest = (ulong)(key.I - 1) < (ulong)data.Length ? Value.FromFloat(data[key.I - 1]) : default;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ArraySet(in Value o, in Value key, in Value val)
    {
        if (key.Kind != ValueKind.Integer || o.O is not Stdlib.FloatArray fa || fa.Frozen) return false;
        double[] data = fa.Data;
        if ((ulong)(key.I - 1) >= (ulong)data.Length) return false;
        if (val.Kind == ValueKind.Float) data[key.I - 1] = val.N;
        else if (val.Kind == ValueKind.Integer) data[key.I - 1] = val.I;
        else return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool OpIndexSlow(LuaThread th, int @base, Value[] k, in Instruction i)
    {
        Value[] stack = th.Stack;
        Value orig = stack[@base + i.B];
        Value key = i.Op == OpCode.GetTable ? (i.C >= 0 ? stack[@base + i.C] : k[~i.C]) : k[i.C];
        // method and field names are constants whose hash the compiler precomputed
        bool hashed = i.Op != OpCode.GetTable;
        int keyHash = hashed ? th.Frames[th.FrameCount - 1].Cl.Proto.KHash[i.C] : 0;
        Value o = orig;
        Value r = default;
        for (int loop = 0; ; loop++)
        {
            if (loop >= 100) throw new LuaException("'__index' chain too long; possible loop");
            Value h;
            if (o.Kind == ValueKind.Table)
            {
                Table t = (Table)o.O!;
                r = hashed ? t.GetStr(Unsafe.As<string>(key.O!), keyHash) : t.Get(key);
                if (!r.IsNil) break;
                Table? mt = t.Metatable;
                if (mt == null) break;
                // a property of a class: its getter is called like an __index function
                // (it takes the object; the key passed after it is ignored)
                if (mt.Getters != null && mt.Getters.Get(key) is { IsNil: false } getter) h = getter;
                else
                {
                    h = mt.GetStr(Meta.Index.Name, Meta.Index.Hash);
                    if (h.IsNil) break;
                }
            }
            else
            {
                h = GetMetamethod(o, Meta.Index);
                if (h.IsNil) throw IndexError(o, key);
            }
            if (h.Kind == ValueKind.Function)
            {
                if (h.O is Closure)
                {
                    if (i.Op == OpCode.Self) stack[@base + i.A + 1] = orig;
                    PushMetaFrame(th, h, 2, o, key, default, @base + i.A, 1, 0);
                    return true;
                }
                r = Call1(h, o, key);
                break;
            }
            o = h;
        }
        stack = th.Stack;
        if (i.Op == OpCode.Self) stack[@base + i.A + 1] = orig;
        stack[@base + i.A] = r;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool OpSetIndexSlow(LuaThread th, int @base, Value[] k, in Instruction i)
    {
        Value[] stack = th.Stack;
        Value o = stack[@base + i.A];
        Value key = i.Op == OpCode.SetTable ? (i.B >= 0 ? stack[@base + i.B] : k[~i.B]) : k[i.B];
        Value val = i.C >= 0 ? stack[@base + i.C] : k[~i.C];
        if (o.O is Table { Guarded: true } guarded)
        {
            ClassTag? caller = th.Frames[th.FrameCount - 1].Cl.Proto.ClassTag;
            if (guarded.GuardTag == null || !ReferenceEquals(guarded.GuardTag, caller)) GuardCheck(guarded, key, caller);
        }
        for (int loop = 0; ; loop++)
        {
            if (loop >= 100) throw new LuaException("'__newindex' chain too long; possible loop");
            Value h;
            if (o.Kind == ValueKind.Table)
            {
                Table t = (Table)o.O!;
                Table? mt = t.Metatable;
                if (mt == null) { t.Set(key, val); return false; }
                if (mt.Setters != null && mt.Setters.Get(key) is { IsNil: false } setter)
                {
                    // a property of a class: its setter takes the object and the value
                    if (setter.O is Closure)
                    {
                        PushMetaFrame(th, setter, 2, o, val, default, 0, 0, 0);
                        return true;
                    }
                    Call1(setter, o, val);
                    return false;
                }
                h = mt.GetStr(Meta.Newindex.Name, Meta.Newindex.Hash);
                if (h.IsNil || !t.Get(key).IsNil) { t.Set(key, val); return false; }
            }
            else
            {
                h = GetMetamethod(o, Meta.Newindex);
                if (h.IsNil) throw IndexError(o, key);
            }
            if (h.Kind == ValueKind.Function)
            {
                if (h.O is Closure)
                {
                    PushMetaFrame(th, h, 3, o, key, val, 0, 0, 0);
                    return true;
                }
                Call1(h, o, key, val);
                return false;
            }
            o = h;
        }
    }

    // Arithmetic, unary and concat instructions outside the inline integer/float fast paths.
    // Returns true if a frame for a Lua metamethod was pushed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool OpArithSlow(LuaThread th, int @base, Value[] k, in Instruction i)
    {
        Value[] stack = th.Stack;
        Value r;
        switch (i.Op)
        {
            case OpCode.Unm:
            case OpCode.BNot:
            case OpCode.Len:
                {
                    Value a = stack[@base + i.B];
                    bool primitive = i.Op == OpCode.Len
                        ? a.IsString || (a.IsTable && (a.AsTable.Metatable == null || a.AsTable.Metatable.GetStr(Meta.Len).IsNil))
                        : a.IsNumber || a.IsString;
                    if (!primitive)
                    {
                        Value h = GetMetamethod(a, i.Op == OpCode.Unm ? Meta.Unm : i.Op == OpCode.BNot ? Meta.Bnot : Meta.Len);
                        if (h.O is Closure)
                        {
                            PushMetaFrame(th, h, i.Op == OpCode.Len ? 1 : 2, a, a, default, @base + i.A, 1, 0);
                            return true;
                        }
                    }
                    r = i.Op == OpCode.Unm ? Unm(a) : i.Op == OpCode.BNot ? BNot(a) : Len(a);
                    break;
                }
            case OpCode.Concat: r = Concat(th, @base + i.B, @base + i.C); break;
            default:
                {
                    Value b = i.B >= 0 ? stack[@base + i.B] : k[~i.B];
                    Value c = i.C >= 0 ? stack[@base + i.C] : k[~i.C];
                    if (!(b.IsNumber || b.IsString) || !(c.IsNumber || c.IsString))
                    {
                        MetaKey ev = Meta.Arith[i.Op - OpCode.Add];
                        Value h = GetMetamethod(b, ev);
                        if (h.IsNil) h = GetMetamethod(c, ev);
                        if (h.O is Closure)
                        {
                            PushMetaFrame(th, h, 2, b, c, default, @base + i.A, 1, 0);
                            return true;
                        }
                    }
                    r = Arith(i.Op, b, c);
                    break;
                }
        }
        th.Stack[@base + i.A] = r;
        return false;
    }

    // Eq/Lt/Le outside the fast paths. Returns 0/1, or -1 if a Lua metamethod frame was pushed
    // (FinishCompare then completes the instruction when it returns).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int OpCompareSlow(LuaThread th, int @base, Value[] k, in Instruction i)
    {
        Value[] stack = th.Stack;
        Value b = i.B >= 0 ? stack[@base + i.B] : k[~i.B];
        Value c = i.C >= 0 ? stack[@base + i.C] : k[~i.C];
        if (i.Op == OpCode.Eq)
        {
            if (b.RawEquals(c)) return 1;
            if (b.Kind != c.Kind || (b.Kind != ValueKind.Table && b.Kind != ValueKind.Userdata)) return 0;
            Value h = GetMetamethod(b, Meta.Eq);
            if (h.IsNil) h = GetMetamethod(c, Meta.Eq);
            if (h.O is Closure)
            {
                PushMetaFrame(th, h, 2, b, c, default, 0, -1, 1);
                return -1;
            }
            return Equals(b, c) ? 1 : 0;
        }
        bool primitive = (b.IsNumber && c.IsNumber) || (b.IsString && c.IsString);
        if (!primitive)
        {
            MetaKey ev = i.Op == OpCode.Lt ? Meta.Lt : Meta.Le;
            Value h = GetMetamethod(b, ev);
            if (h.IsNil) h = GetMetamethod(c, ev);
            if (h.O is Closure)
            {
                PushMetaFrame(th, h, 2, b, c, default, 0, -1, 1);
                return -1;
            }
        }
        return (i.Op == OpCode.Lt ? LessThan(b, c) : LessEqual(b, c)) ? 1 : 0;
    }

    /// <summary>Prepares a numeric for loop at stack[ra..ra+3]; returns true if the loop must be skipped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    // Verified: verification/ForLoop.dfy, methods ForPrep and RunLoop (trip count == iterations of the unbounded loop, no overflow)
    internal static bool OpForPrep(Value[] stack, int ra)
    {
        ref Value init = ref stack[ra];
        ref Value limit = ref stack[ra + 1];
        ref Value step = ref stack[ra + 2];
        if (init.Kind == ValueKind.Integer && step.Kind == ValueKind.Integer)
        {
            long st = step.I;
            if (st == 0) throw new LuaException("'for' step is zero");
            long start = init.I;
            if (!ForLimit(limit, st, start, out long lim)) return true;
            ulong count = st > 0
                ? ((ulong)lim - (ulong)start) / (ulong)st
                : ((ulong)start - (ulong)lim) / ((ulong)(-(st + 1)) + 1UL);
            limit = Value.FromInt(unchecked((long)count));
            stack[ra + 3] = init;
            return false;
        }
        if (!init.IsNumber && !(init.IsString && init.TryToDouble(out _))) throw ForError("initial value", init);
        if (!limit.TryToDouble(out double flim)) throw ForError("limit", limit);
        if (!step.TryToDouble(out double fstep)) throw ForError("step", step);
        init.TryToDouble(out double finit);
        if (fstep == 0) throw new LuaException("'for' step is zero");
        if (fstep > 0 ? flim < finit : finit < flim) return true;
        init = Value.FromFloat(finit);
        limit = Value.FromFloat(flim);
        step = Value.FromFloat(fstep);
        stack[ra + 3] = init;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpClosure(Value[] stack, int @base, Closure cl, Proto np, int a)
    {
        var descs = np.Upvals;
        Box[] ups = descs.Length == 0 ? Array.Empty<Box>() : new Box[descs.Length];
        for (int j = 0; j < ups.Length; j++)
            ups[j] = descs[j].FromParentRegister ? Unsafe.As<Box>(stack[@base + descs[j].Index].O!) : cl.Upvals[descs[j].Index];
        stack[@base + a] = Value.FromFunction(new Closure(np, ups));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpVarArg(LuaThread th, int ra, int n)
    {
        Value[] va = th.Frames[th.FrameCount - 1].Varargs;
        if (n < 0)
        {
            n = va.Length;
            th.EnsureStack(ra + n + 8);
            Array.Copy(va, 0, th.Stack, ra, n);
            th.Top = ra + n;
            return;
        }
        Value[] stack = th.Stack;
        int m = n < va.Length ? n : va.Length;
        for (int j = 0; j < m; j++) stack[ra + j] = va[j];
        for (int j = m; j < n; j++) stack[ra + j] = default;
    }

    // Same argument rules and message as the native function the instruction stands in for.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double MathArgSlow(in Value v, MathFn fn)
    {
        if (v.TryToDouble(out double d)) return d;
        throw new LuaException($"bad argument #1 to '{MathBuiltins.Names[(int)fn]}' (number expected, got {(v.IsNil ? "nil" : TypeNameOf(v))})");
    }

    internal static bool IsKind(in Value b, TypeKindTest test) => test switch
    {
        TypeKindTest.Nil => b.Kind == ValueKind.Nil,
        TypeKindTest.Boolean => b.Kind == ValueKind.Boolean,
        TypeKindTest.Number => b.Kind == ValueKind.Integer || b.Kind == ValueKind.Float,
        TypeKindTest.Integer => b.Kind == ValueKind.Integer,
        TypeKindTest.Float => b.Kind == ValueKind.Float,
        TypeKindTest.String => b.Kind == ValueKind.String,
        TypeKindTest.Table => b.Kind == ValueKind.Table,
        TypeKindTest.Function => b.Kind == ValueKind.Function,
        TypeKindTest.Thread => b.Kind == ValueKind.Thread,
        TypeKindTest.Userdata => b.Kind == ValueKind.Userdata,
        _ => false,
    };

    // ------------------------------------------------------------------ error operand description

    // Describes the operand that caused an index/call/arithmetic error at instruction `pc`.
    private static string OperandInfo(Proto p, int pc, ErrorCategory cat, Value[] stack, int @base)
    {
        if ((uint)pc >= (uint)p.Code.Length) return "";
        Instruction ins = p.At(pc);
        int reg;
        switch (cat)
        {
            case ErrorCategory.Index:
                switch (ins.Op)
                {
                    case OpCode.GetTable: case OpCode.GetField: case OpCode.Self: reg = ins.B; break;
                    case OpCode.SetTable: case OpCode.SetField: reg = ins.A; break;
                    default: return "";
                }
                break;
            case ErrorCategory.Call:
                if (ins.Op is not (OpCode.Call or OpCode.TailCall or OpCode.TForCall)) return "";
                reg = ins.A;
                break;
            case ErrorCategory.Arith:
                {
                    if (ins.Op >= OpCode.Add && ins.Op <= OpCode.Shr)
                    {
                        // pick the operand that is not a number
                        bool bBad = ins.B >= 0 && !IsNumeric(stack[@base + ins.B]);
                        reg = bBad ? ins.B : ins.C;
                    }
                    else if (ins.Op == OpCode.Unm) reg = ins.B;
                    else return "";
                    if (reg < 0) return "";
                    break;
                }
            default: return "";
        }
        string d = DescribeRegister(p, pc, reg, 0);
        return d.Length == 0 ? "" : " (" + d + ")";
    }

    private static bool IsNumeric(in Value v) => v.IsNumber || (v.IsString && LuaNumber.TryParse(v.AsString, out _));

    private static string? LocalName(Proto p, int pc, int reg)
    {
        foreach (LocVar lv in p.LocVars)
            if (lv.Reg == reg && lv.StartPc <= pc && pc < lv.EndPc) return lv.Name;
        return null;
    }

    private static string DescribeRegister(Proto p, int pc, int reg, int depth)
    {
        string? local = LocalName(p, pc, reg);
        if (local != null) return $"local '{local}'";
        if (depth > 3) return "";
        // Find the instruction that loaded the register (straight-line approximation).
        for (int i = pc - 1; i >= 0; i--)
        {
            Instruction ins = p.At(i);
            switch (ins.Op)
            {
                case OpCode.Jmp: case OpCode.JmpIf: case OpCode.JmpIfNot: case OpCode.Eq: case OpCode.Lt: case OpCode.Le:
                case OpCode.SetUpval: case OpCode.SetBox: case OpCode.SetGlobal: case OpCode.SetTable: case OpCode.SetField:
                case OpCode.Return: case OpCode.SetList: case OpCode.ForPrep: case OpCode.ForLoop: case OpCode.TForLoop:
                    continue;
                case OpCode.PrepareDotCall:
                    if (reg > ins.A && (ins.B == 0 || reg < ins.A + ins.B)) return "";
                    continue;
                case OpCode.Call: case OpCode.TailCall: case OpCode.VarArg:
                    if (reg >= ins.A) return "";
                    continue;
                case OpCode.TForCall:
                    if (reg >= ins.A + 3) return "";
                    continue;
                case OpCode.LoadNil:
                    if (reg >= ins.A && reg <= ins.A + ins.B) return "";
                    continue;
                case OpCode.Self:
                    if (reg == ins.A) return $"method '{p.K[ins.C]}'";
                    if (reg == ins.A + 1) return "";
                    continue;
            }
            if (ins.A != reg) continue;
            switch (ins.Op)
            {
                case OpCode.GetGlobal: return $"global '{p.K[ins.B]}'";
                case OpCode.GetField: return $"field '{p.K[ins.C]}'";
                case OpCode.GetTable: return ins.C < 0 && p.K[~ins.C].IsString ? $"field '{p.K[~ins.C]}'" : "";
                case OpCode.GetUpval: return $"upvalue '{p.Upvals[ins.B].Name}'";
                case OpCode.GetBox: return LocalName(p, i, ins.B) is { } n ? $"local '{n}'" : "";
                case OpCode.Move: return DescribeRegister(p, i, ins.B, depth + 1);
                default: return "";
            }
        }
        return "";
    }

    private static void PopFrames(LuaThread th, int newCount)
    {
        for (int fi = th.FrameCount - 1; fi >= newCount; fi--)
        {
            th.Frames[fi].Cl = null!;
            if (th.Frames[fi].Varargs is { Length: > 0 } va) th.VarargSlots -= va.Length;
            th.Frames[fi].Varargs = null!;
        }
        th.FrameCount = newCount;
    }

    private static string DescribeHostException(Exception e) => e switch
    {
        InvalidCastException or NullReferenceException => "internal error: unexpected value type (" + e.Message + ")",
        StackOverflowException or InsufficientExecutionStackException => "stack overflow",
        OutOfMemoryException => "not enough memory",
        _ => e.Message,
    };

    private static LuaException ForError(string what, in Value v) =>
        new($"bad 'for' {what} (number expected, got {TypeNameOf(v)})");

    /// <summary>Computes the integer loop limit; returns false if the loop must not run.</summary>
    // Verified: verification/ForLoop.dfy, method ForLimitFloat (clipping a float limit)
    private static bool ForLimit(in Value limit, long step, long start, out long lim)
    {
        if (limit.Kind == ValueKind.Integer) lim = limit.I;
        else
        {
            if (!limit.TryToDouble(out double f)) throw ForError("limit", limit);
            if (double.IsNaN(f)) { lim = 0; return false; }
            if (step > 0)
            {
                f = Math.Floor(f);
                if (f >= 9223372036854775808.0) lim = long.MaxValue;
                else if (f < -9223372036854775808.0) { lim = 0; return false; }
                else lim = (long)f;
            }
            else
            {
                f = Math.Ceiling(f);
                if (f < -9223372036854775808.0) lim = long.MinValue;
                else if (f >= 9223372036854775808.0) { lim = 0; return false; }
                else lim = (long)f;
            }
        }
        return step > 0 ? start <= lim : start >= lim;
    }

    // ------------------------------------------------------------------ coroutines

    public LuaThread NewThread(Value fn) => new LuaThread(false) { StartFunction = fn, Limits = Limits };

    /// <summary>Resumes a suspended coroutine by raising <paramref name="error"/> at the point where it yielded.</summary>
    public bool ResumeWithError(LuaThread co, Value error, out Value[] results)
    {
        co.HasPendingThrow = true;
        co.PendingThrow = error;
        return Resume(co, ReadOnlySpan<Value>.Empty, out results);
    }

    /// <summary>
    /// Resumes a coroutine. Returns true and the yielded/returned values, or false and the error value.
    /// </summary>
    public bool Resume(LuaThread co, ReadOnlySpan<Value> args, out Value[] results)
    {
        if (co.Status != CoStatus.Suspended)
        {
            results = new[] { Value.FromString(co.Status == CoStatus.Dead ? "cannot resume dead coroutine" : "cannot resume non-suspended coroutine") };
            return false;
        }
        LuaThread prev = CurrentThread;
        prev.Status = CoStatus.Normal;
        co.Status = CoStatus.Running;
        CurrentThread = co;
        try
        {
            ExecStatus st;
            int resBase, resCount;
            if (!co.Started)
            {
                co.Started = true;
                co.EnsureStack(args.Length + 16);
                co.Stack[0] = co.StartFunction;
                args.CopyTo(co.Stack.AsSpan(1));
                co.CallTop = 0;
                if (co.StartFunction.O is Closure cl)
                {
                    PushLuaFrame(co, cl, 1, args.Length, 0, -1, false);
                    st = Execute(co, 0);
                    if (st == ExecStatus.Done) { resBase = 0; resCount = co.Top; }
                    else { resBase = co.ResultBase; resCount = co.ResultCount; }
                }
                else
                {
                    resCount = CallAt(co, 0, args.Length);
                    resBase = 0;
                    st = ExecStatus.Done;
                }
            }
            else
            {
                // Deliver the resume arguments as the results of the pending yield call.
                ref Frame f = ref co.Frames[co.FrameCount - 1];
                Instruction ins = f.Cl.Proto.At(f.PC - 1);
                int ra = f.Base + ins.A;
                int nres;
                switch (ins.Op)
                {
                    case OpCode.Call: nres = ins.C - 1; break;
                    case OpCode.TForCall: ra += 3; nres = ins.C; break;
                    default: nres = -1; break;
                }
                co.EnsureStack(ra + args.Length + 8);
                var s = co.Stack;
                if (nres < 0)
                {
                    args.CopyTo(s.AsSpan(ra));
                    co.Top = ra + args.Length;
                }
                else
                {
                    int m = Math.Min(nres, args.Length);
                    for (int j = 0; j < m; j++) s[ra + j] = args[j];
                    for (int j = m; j < nres; j++) s[ra + j] = default;
                }
                st = Execute(co, 0);
                if (st == ExecStatus.Done) { resBase = 0; resCount = co.Top; }
                else { resBase = co.ResultBase; resCount = co.ResultCount; }
            }
            results = resCount == 0 ? Array.Empty<Value>() : co.Stack.AsSpan(resBase, resCount).ToArray();
            co.Status = st == ExecStatus.Yield ? CoStatus.Suspended : CoStatus.Dead;
            if (co.Status == CoStatus.Dead) co.Stack = Array.Empty<Value>();
            return true;
        }
        catch (LuaAbortException)
        {
            co.Status = CoStatus.Dead;
            co.FrameCount = 0;
            throw;
        }
        catch (LuaException e)
        {
            co.Status = CoStatus.Dead;
            co.FrameCount = 0;
            co.DeathError = e.Value;     // coroutine.close reports it
            co.DiedWithError = true;
            results = new[] { e.Value };
            return false;
        }
        finally
        {
            CurrentThread = prev;
            prev.Status = CoStatus.Running;
        }
    }
}
