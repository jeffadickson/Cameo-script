using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Compiler;

/// <summary>
/// Compiles a (type-checked) Teal syntax tree straight to VM bytecode.
/// There is no intermediate Lua source: Teal-only constructs (records, enums, `is`, `as`,
/// type declarations) are lowered here.
/// </summary>
public sealed class BytecodeCompiler
{
    private sealed class BlockScope
    {
        public BlockScope? Parent;
        public int SavedFreeReg;
        public bool IsLoop;
        public List<int> Breaks = new();
        /// <summary>This block is the body of a loop: `continue` leaves it and starts the next iteration.</summary>
        public bool IsLoopBody;
        /// <summary>For a loop written right after <c>::name::</c>: that name, for <c>break name</c> / <c>continue name</c>.</summary>
        public string? LoopLabel;
        public List<int> Continues = new();
        /// <summary>Number of locals declared when the first `continue` of this body was compiled (-1: none).</summary>
        public int LocVarsAtContinue = -1;
        public Dictionary<string, int> Labels = new();
        /// <summary>Forward gotos waiting for their label. ClosePc is a placeholder Close instruction
        /// emitted before the jump (or -1), patched if the goto turns out to leave a &lt;close&gt; scope.</summary>
        public List<(GotoStat Stat, int Pc, int ClosePc)> PendingGotos = new();
        public int FirstLocVar;
        /// <summary>Lowest register of a to-be-closed variable declared in this block, or -1.</summary>
        public int TbcReg = -1;
    }

    private sealed class FuncState
    {
        public FuncState? Parent;
        public FuncInfo Info = null!;
        public Proto Proto = new();
        public List<Instruction> Code = new();
        public List<int> Lines = new();
        public List<Value> K = new();
        public Dictionary<string, int> KStr = new();
        public Dictionary<long, int> KInt = new();
        public Dictionary<long, int> KFloat = new();
        public int KNil = -1, KTrue = -1, KFalse = -1;
        public List<Proto> Protos = new();
        public int FreeReg;
        public int MaxReg;
        /// <summary>Registers below this hold active local variables.</summary>
        public int ActiveTop;
        public BlockScope? Block;
        public int Line;
        public List<(string Name, int Reg, int Start, int End)> LocVars = new();
    }

    private readonly string _file;
    private readonly string _chunkName;
    private FuncState _fs = null!;
    private bool _intrinsics;

    // f(x) where f is a global math built-in called with exactly one single-valued argument.
    private bool IsIntrinsicCall(Expr e, out MathFn fn)
    {
        fn = default;
        return _intrinsics && e is CallExpr { Macro: null, Callee: NameExpr { Symbol: null } callee, Args.Count: 1 } call
            && MathBuiltins.TryGet(callee.Name, out fn) && !IsMulti(call.Args[0]);
    }

    private BytecodeCompiler(string file, string chunkName) { _file = file; _chunkName = chunkName; }

    /// <summary>Compiles a chunk to the prototype of its main function.</summary>
    /// <param name="intrinsics">Compile calls to the global math built-ins (sin, log2, ...) to the
    /// MathFn instruction. Only sound when the program was type-checked: the checker guarantees
    /// those globals are constants that still name the built-ins.</param>
    public static Proto Compile(Chunk chunk, string? chunkName = null, bool intrinsics = false)
    {
        try
        {
            Resolver.Resolve(chunk);
            var c = new BytecodeCompiler(chunk.FileName, chunkName ?? chunk.FileName) { _intrinsics = intrinsics, _classTags = chunk.ClassTags };
            return c.Function(chunk.Main, "main chunk");
        }
        catch (InsufficientExecutionStackException)
        {
            // e.g. an operator chain with tens of thousands of terms: the tree is too deep to walk
            throw new CameoSyntaxException(new Diagnostic(chunk.FileName, 1, 1, "program is too deeply nested for the available stack"));
        }
    }

    private CameoSyntaxException Error(Node n, string msg) => new(new Diagnostic(_file, n.Line, n.Col, msg));

    // ================================================================== emit helpers

    private int Emit(OpCode op, int a = 0, int b = 0, int c = 0)
    {
        _fs.Code.Add(new Instruction(op, a, b, c));
        _fs.Lines.Add(_fs.Line);
        return _fs.Code.Count - 1;
    }

    private int Pc => _fs.Code.Count;

    private void SetLine(Node n)
    {
        if (n.Line > 0) _fs.Line = n.Line;
    }

    private int EmitJump(OpCode op = OpCode.Jmp, int a = 0) => Emit(op, a, 0, 0);

    private void PatchJump(int at, int target)
    {
        Instruction i = _fs.Code[at];
        _fs.Code[at] = new Instruction(i.Op, i.A, target - (at + 1), i.C);
    }

    private void PatchHere(List<int> jumps)
    {
        foreach (int j in jumps) PatchJump(j, Pc);
    }

    private int Alloc(int n = 1)
    {
        int r = _fs.FreeReg;
        _fs.FreeReg += n;
        Touch(_fs.FreeReg);
        return r;
    }

    private void Touch(int top)
    {
        if (top > _fs.MaxReg) _fs.MaxReg = top;
        if (top > 60000) throw new CameoSyntaxException(new Diagnostic(_file, _fs.Line, 1, "function or expression too complex (too many registers)"));
    }

    private bool IsTopTemp(int reg) => reg == _fs.FreeReg - 1 && reg >= _fs.ActiveTop;

    private int KString(string s)
    {
        if (!_fs.KStr.TryGetValue(s, out int idx))
        {
            idx = _fs.K.Count;
            // Interned, so that equal names in different functions (and the runtime's own
            // metamethod names) are the same object and table lookups hit on reference equality.
            _fs.K.Add(Value.FromString(string.Intern(s)));
            _fs.KStr[s] = idx;
        }
        return idx;
    }

    private int KInt(long v)
    {
        if (!_fs.KInt.TryGetValue(v, out int idx))
        {
            idx = _fs.K.Count;
            _fs.K.Add(Value.FromInt(v));
            _fs.KInt[v] = idx;
        }
        return idx;
    }

    private int KFloat(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
        if (!_fs.KFloat.TryGetValue(bits, out int idx))
        {
            idx = _fs.K.Count;
            _fs.K.Add(Value.FromFloat(v));
            _fs.KFloat[bits] = idx;
        }
        return idx;
    }

    private int KNil()
    {
        if (_fs.KNil < 0) { _fs.KNil = _fs.K.Count; _fs.K.Add(Value.Nil); }
        return _fs.KNil;
    }

    private int KBool(bool b)
    {
        if (b)
        {
            if (_fs.KTrue < 0) { _fs.KTrue = _fs.K.Count; _fs.K.Add(Value.True); }
            return _fs.KTrue;
        }
        if (_fs.KFalse < 0) { _fs.KFalse = _fs.K.Count; _fs.K.Add(Value.False); }
        return _fs.KFalse;
    }

    // ================================================================== functions

    private List<((int StartLine, int StartCol, int EndLine, int EndCol) Range, ClassTag Tag)>? _classTags;

    // The class declaration (the innermost) that contains this place in the source.
    private ClassTag? ClassTagAt(int line, int col)
    {
        if (_classTags == null) return null;
        ClassTag? best = null;
        (int, int) bestStart = (-1, -1);
        foreach (var (r, tag) in _classTags)
        {
            bool inside = (line > r.StartLine || (line == r.StartLine && col >= r.StartCol)) && (line < r.EndLine || (line == r.EndLine && col <= r.EndCol));
            if (inside && (r.StartLine, r.StartCol).CompareTo(bestStart) > 0) { best = tag; bestStart = (r.StartLine, r.StartCol); }
        }
        return best;
    }

    private Proto Function(FunctionBody body, string name)
    {
        var info = (FuncInfo)body.ScopeInfo!;
        var fs = new FuncState { Parent = _fs, Info = info, Line = body.Line };
        FuncState? saved = _fs;
        _fs = fs;
        Proto p = fs.Proto;
        p.Name = name;
        p.ChunkName = _chunkName;
        p.LineDefined = body.Line;
        p.NumParams = body.Params.Count;
        p.NumericParameters = _intrinsics ? body.Params.Select(parameter => parameter.NumericHint).ToArray() : [];
        p.IsVarArg = body.IsVarArg;
        p.ClassTag = ClassTagAt(body.Line, body.Col);
        p.IsCameoMethod = body.IsCameoMethod;

        EnterBlock(false);
        foreach (Param prm in body.Params)
        {
            var sym = (LocalSym)prm.Symbol!;
            sym.Reg = Alloc();
        }
        fs.ActiveTop = fs.FreeReg;
        foreach (Param prm in body.Params)
        {
            var sym = (LocalSym)prm.Symbol!;
            if (sym.Captured) Emit(OpCode.NewBox, sym.Reg);
            fs.LocVars.Add((sym.Name, sym.Reg, 0, -1));
        }
        BlockStats(body.Body);
        fs.Line = body.EndLine > 0 ? body.EndLine : fs.Line;
        if (fs.Block!.TbcReg >= 0) Emit(OpCode.Close, 0);
        Emit(OpCode.Return, 0, 1);
        BlockScope top = fs.Block!;
        if (top.PendingGotos.Count > 0)
        {
            GotoStat g = top.PendingGotos[0].Stat;
            throw Error(g, $"no visible label '{g.Label}' for goto");
        }
        CloseLocVars(top);
        fs.Block = null;

        p.LocVars = fs.LocVars.Select(lv => new LocVar(lv.Name, lv.Reg, lv.Start, lv.End)).ToArray();
        p.Code = fs.Code.ToArray();
        p.Lines = fs.Lines.ToArray();
        p.K = fs.K.ToArray();
        p.Protos = fs.Protos.ToArray();
        p.MaxStack = fs.MaxReg + 2;
        p.FinishConstants();
        var ups = new UpvalDesc[info.Upvalues.Count];
        for (int i = 0; i < ups.Length; i++)
        {
            LocalSym sym = info.Upvalues[i];
            if (sym.Owner == info.Parent) ups[i] = new UpvalDesc(true, sym.Reg, sym.Name);
            else ups[i] = new UpvalDesc(false, info.Parent!.UpvalueIndex[sym], sym.Name);
        }
        p.Upvals = ups;
        _fs = saved!;
        return p;
    }

    /// <summary>Compiles a nested function and emits the instruction that creates its closure in <paramref name="dst"/>.</summary>
    private void ClosureTo(FunctionBody body, string name, int dst, Node at)
    {
        Proto p = Function(body, name);
        _fs.Protos.Add(p);
        SetLine(at);
        Emit(OpCode.Closure, dst, _fs.Protos.Count - 1);
    }

    private static string FuncName(string name, FunctionBody body) =>
        name == "anonymous" ? $"function <line {body.Line}>" : $"function '{name}'";

    // ================================================================== blocks

    private void EnterBlock(bool isLoop)
    {
        _fs.Block = new BlockScope { Parent = _fs.Block, SavedFreeReg = _fs.FreeReg, IsLoop = isLoop, FirstLocVar = _fs.LocVars.Count };
    }

    // Name of the label statement directly before the statement being compiled, if any.
    private string? _loopLabel;

    private void LeaveBlock()
    {
        BlockScope b = _fs.Block!;
        if (b.TbcReg >= 0)
        {
            // gotos that jump out of this block must close its <close> variables first
            foreach (var g in b.PendingGotos)
            {
                if (g.ClosePc < 0) continue;
                Instruction c = _fs.Code[g.ClosePc];
                if (b.TbcReg < c.A) _fs.Code[g.ClosePc] = new Instruction(OpCode.Close, b.TbcReg);
            }
            Emit(OpCode.Close, b.TbcReg);
        }
        CloseLocVars(b);
        _fs.Block = b.Parent;
        _fs.FreeReg = b.SavedFreeReg;
        _fs.ActiveTop = b.SavedFreeReg;
        // Unresolved gotos may still find their label in an enclosing block.
        if (b.PendingGotos.Count > 0)
        {
            if (b.Parent == null) return;
            b.Parent.PendingGotos.AddRange(b.PendingGotos);
        }
    }

    private void CloseLocVars(BlockScope b)
    {
        for (int i = b.FirstLocVar; i < _fs.LocVars.Count; i++)
        {
            var lv = _fs.LocVars[i];
            if (lv.End < 0) _fs.LocVars[i] = (lv.Name, lv.Reg, lv.Start, Pc);
        }
    }

    private void Block(Block b, bool isLoop = false)
    {
        EnterBlock(isLoop);
        BlockStats(b);
        LeaveBlock();
    }

    private void BlockStats(Block b)
    {
        _pendingLabel = null; // a label names a loop only within its own block
        foreach (Stat s in b.Stats)
        {
            SetLine(s);
            Statement(s);
            // temporaries die at the end of each statement
            _fs.FreeReg = _fs.ActiveTop;
        }
        _pendingLabel = null;
    }

    private void DeclareLocal(LocalSym sym, int reg)
    {
        sym.Reg = reg;
        if (sym.Captured) Emit(OpCode.NewBox, reg);
        _fs.LocVars.Add((sym.Name, reg, Pc, -1));
        if (reg + 1 > _fs.ActiveTop) _fs.ActiveTop = reg + 1;
    }

    // ================================================================== statements

    private void Statement(Stat s)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        // `::name::` directly before a loop names that loop
        string? label = _pendingLabel;
        _pendingLabel = (s as LabelStat)?.Name;
        _loopLabel = s is WhileStat or RepeatStat or ForNumStat or ForInStat ? label : null;
        switch (s)
        {
            case LocalStat l: LocalStatement(l); break;
            case GlobalStat g: GlobalStatement(g); break;
            case AssignStat a: Assign(a); break;
            case CallStat c:
                if (c.Call.Macro != null || IsIntrinsicCall(c.Call, out _)) ExprTo(c.Call, Alloc());
                else Call(c.Call, -1, 0);
                break;
            case DoStat d: Block(d.Body); break;
            case WhileStat w: While(w); break;
            case RepeatStat r: Repeat(r); break;
            case IfStat i: If(i); break;
            case ForNumStat f: ForNum(f); break;
            case ForInStat f: ForIn(f); break;
            case FunctionStat f: FunctionStatement(f); break;
            case LocalFunctionStat lf:
                {
                    var sym = (LocalSym)lf.Name.Symbol!;
                    int reg = Alloc();
                    if (sym.Captured)
                    {
                        // the function may refer to itself: create the box first
                        Emit(OpCode.LoadNil, reg, 0);
                        DeclareLocal(sym, reg);
                        int tmp = Alloc();
                        ClosureTo(lf.Body, FuncName(lf.Name.Name, lf.Body), tmp, lf);
                        Emit(OpCode.SetBox, reg, tmp);
                    }
                    else
                    {
                        sym.Reg = reg;
                        ClosureTo(lf.Body, FuncName(lf.Name.Name, lf.Body), reg, lf);
                        _fs.LocVars.Add((sym.Name, reg, Pc, -1));
                        _fs.ActiveTop = reg + 1;
                    }
                    break;
                }
            case GlobalFunctionStat gf:
                {
                    int tmp = Alloc();
                    ClosureTo(gf.Body, FuncName(gf.Name, gf.Body), tmp, gf);
                    Emit(OpCode.SetGlobal, tmp, KString(gf.Name));
                    break;
                }
            case ReturnStat r: Return(r); break;
            case MatchStat ms: CompileCameoMatch(ms.Match, -1); break;
            case CameoImportStat imp: CompileCameoImport(imp); break;
            case BreakStat br:
                {
                    BlockScope? b = _fs.Block;
                    while (b != null && !(b.IsLoop && (br.Label == null || b.LoopLabel == br.Label))) b = b.Parent;
                    if (b == null) throw Error(s, br.Label == null ? "break outside a loop" : $"no enclosing loop is labelled '{br.Label}'");
                    int tbc = PendingTbcReg(b);
                    if (tbc >= 0) Emit(OpCode.Close, tbc);
                    b.Breaks.Add(EmitJump());
                    break;
                }
            case ContinueStat ct:
                {
                    BlockScope? b = _fs.Block;
                    while (b != null && !(b.IsLoopBody && (ct.Label == null || b.LoopLabel == ct.Label))) b = b.Parent;
                    if (b == null) throw Error(s, ct.Label == null ? "continue outside a loop" : $"no enclosing loop is labelled '{ct.Label}'");
                    // leaving the body early closes its <close> variables, as reaching its end does
                    int tbc = PendingTbcReg(b);
                    if (tbc >= 0) Emit(OpCode.Close, tbc);
                    if (b.LocVarsAtContinue < 0) b.LocVarsAtContinue = _fs.LocVars.Count;
                    b.Continues.Add(EmitJump());
                    break;
                }
            case GotoStat g: Goto(g); break;
            case LabelStat l: Label(l); break;
            case TypeDeclStat t: TypeDecl(t); break;
            case MacroExpStat: break;
            default: throw Error(s, "internal error: unknown statement " + s.GetType().Name);
        }
    }

    private void LocalStatement(LocalStat l)
    {
        int n = l.Names.Count;
        int @base = _fs.FreeReg;
        ExprListTo(l.Values, n);
        for (int i = 0; i < n; i++) DeclareLocal((LocalSym)l.Names[i].Symbol!, @base + i);
        for (int i = 0; i < n; i++)
        {
            if (l.Names[i].Attrib != "close") continue;
            if (n > 1) throw Error(l.Names[i], "a <close> variable must be declared on its own");
            SetLine(l);
            Emit(OpCode.Tbc, @base + i);
            if (_fs.Block!.TbcReg < 0) _fs.Block.TbcReg = @base + i;
        }
    }

    // Lowest to-be-closed register among the blocks from the current one up to (and including) `upTo`
    // (the whole function when null), or -1.
    private int PendingTbcReg(BlockScope? upTo)
    {
        int reg = -1;
        for (BlockScope? b = _fs.Block; b != null; b = b.Parent)
        {
            if (b.TbcReg >= 0) reg = b.TbcReg;
            if (b == upTo) break;
        }
        return reg;
    }

    private void GlobalStatement(GlobalStat g)
    {
        if (g.Values.Count == 0) return; // declaration only
        int n = g.Names.Count;
        int @base = _fs.FreeReg;
        ExprListTo(g.Values, n);
        for (int i = 0; i < n; i++) Emit(OpCode.SetGlobal, @base + i, KString(g.Names[i].Name));
    }

    /// <summary>
    /// Evaluates an expression list into <paramref name="want"/> consecutive registers starting at FreeReg
    /// (adjusting with nils / dropping extras like Lua). On return FreeReg is past them.
    /// </summary>
    private void ExprListTo(List<Expr> values, int want)
    {
        int @base = _fs.FreeReg;
        int count = values.Count;
        for (int i = 0; i < count; i++)
        {
            Expr e = values[i];
            bool last = i == count - 1;
            if (i < want)
            {
                if (last && IsMulti(e) && want - i > 1)
                {
                    Multi(e, want - i);
                }
                else
                {
                    int r = Alloc();
                    ExprTo(e, r);
                }
            }
            else
            {
                // extra value: evaluate for side effects only
                int mark = _fs.FreeReg;
                if ((e is CallExpr || e is MethodCallExpr) && e.Macro == null && !IsIntrinsicCall(e, out _)) Call(e, -1, 0);
                else if (!IsPure(e)) ExprTo(e, Alloc());
                _fs.FreeReg = mark;
            }
        }
        int have = _fs.FreeReg - @base;
        if (have < want)
        {
            int r = Alloc(want - have);
            Emit(OpCode.LoadNil, r, want - have - 1);
        }
        _fs.FreeReg = @base + want;
    }

    private static bool IsPure(Expr e) => e is NilExpr or BoolExpr or NumberExpr or StringExpr or NameExpr or FunctionExpr or VarargExpr;

    private static Expr StripCasts(Expr e)
    {
        while (e is CastExpr c) e = c.Inner;
        return e;
    }

    /// <summary>Expressions that can yield several values (calls and `...`, possibly behind `as`).</summary>
    private bool IsMulti(Expr e)
    {
        e = StripCasts(e);
        return e.Macro == null && (e is CallExpr or MethodCallExpr or VarargExpr) && !IsIntrinsicCall(e, out _);
    }

    /// <summary>Evaluates a multi-value expression leaving <paramref name="want"/> values (or all, if -1)
    /// in registers starting at the current FreeReg. Returns the first register.</summary>
    private int Multi(Expr e, int want)
    {
        e = StripCasts(e);
        if (e is VarargExpr)
        {
            SetLine(e);
            int r = _fs.FreeReg;
            if (want >= 0) Alloc(want); else Touch(r + 1);
            Emit(OpCode.VarArg, r, 0, want + 1);
            return r;
        }
        return Call(e, -1, want);
    }

    private void Assign(AssignStat a)
    {
        if (a.Targets.Count == 1 && a.Values.Count == 1)
        {
            Expr target = a.Targets[0];
            Expr value = a.Values[0];
            if (target is NameExpr n)
            {
                if (n.Symbol is LocalSym sym && sym.Owner == _fs.Info && !sym.Captured)
                {
                    if (WritesDestinationEarly(value))
                    {
                        int tmp = Alloc();
                        ExprTo(value, tmp);
                        Emit(OpCode.Move, sym.Reg, tmp);
                    }
                    else ExprTo(value, sym.Reg);
                }
                else StoreName(n, ExprToRK(value));
                return;
            }
            var ix = (IndexExpr)target;
            int obj = ExprToAnyReg(ix.Object);
            if (ix.Key is StringExpr sk)
            {
                int v = ExprToRK(value);
                SetLine(ix);
                Emit(OpCode.SetField, obj, KString(sk.Value), v);
            }
            else
            {
                int k = ExprToRK(ix.Key);
                int v = ExprToRK(value);
                SetLine(ix);
                Emit(OpCode.SetTable, obj, k, v);
            }
            return;
        }

        // General case: evaluate all target operands and all values first, then assign.
        int nt = a.Targets.Count;
        var objs = new int[nt];
        var keys = new int[nt];
        for (int i = 0; i < nt; i++)
        {
            if (a.Targets[i] is IndexExpr ix)
            {
                objs[i] = Alloc();
                ExprTo(ix.Object, objs[i]);
                if (ix.Key is StringExpr or NumberExpr) keys[i] = ExprToRK(ix.Key);
                else
                {
                    keys[i] = Alloc();
                    ExprTo(ix.Key, keys[i]);
                }
            }
        }
        int vbase = _fs.FreeReg;
        ExprListTo(a.Values, nt);
        for (int i = nt - 1; i >= 0; i--)
        {
            Expr target = a.Targets[i];
            SetLine(target);
            if (target is NameExpr n)
            {
                if (n.Symbol is LocalSym sym && sym.Owner == _fs.Info && !sym.Captured) Emit(OpCode.Move, sym.Reg, vbase + i);
                else StoreName(n, vbase + i);
            }
            else Emit(OpCode.SetTable, objs[i], keys[i], vbase + i);
        }
    }

    // True if compiling `e` directly into a variable's register could overwrite the variable
    // before `e` has finished reading it (x = x or y, x = {x}).
    private static bool WritesDestinationEarly(Expr e)
    {
        while (true)
        {
            if (e is ParenExpr p) e = p.Inner;
            else if (e is CastExpr c) e = c.Inner;
            else break;
        }
        return e.Macro != null || e is TableExpr or IsExpr || e is BinaryExpr { Op: BinaryOp.And or BinaryOp.Or };
    }

    /// <summary>Stores RK operand <paramref name="rk"/> into a variable (captured local, upvalue or global).</summary>
    private void StoreName(NameExpr n, int rk)
    {
        SetLine(n);
        if (n.Symbol is LocalSym sym)
        {
            if (sym.Owner == _fs.Info)
            {
                if (sym.Captured) Emit(OpCode.SetBox, sym.Reg, rk);
                else if (rk >= 0) Emit(OpCode.Move, sym.Reg, rk);
                else Emit(OpCode.LoadK, sym.Reg, ~rk);
            }
            else Emit(OpCode.SetUpval, rk, _fs.Info.UpvalueIndex[sym]);
        }
        else Emit(OpCode.SetGlobal, rk, KString(n.Name));
    }

    private void While(WhileStat w)
    {
        string? label = _loopLabel; // read now: compiling the loop's own expressions may reset it
        int top = Pc;
        var exits = new List<int>();
        CondJump(w.Cond, false, exits);
        _fs.FreeReg = _fs.ActiveTop;
        EnterBlock(true);
        BlockScope scope = _fs.Block!;
        scope.LoopLabel = label;
        scope.IsLoopBody = true;
        BlockStats(w.Body);
        LeaveBlock();
        foreach (int j in scope.Continues) PatchJump(j, top);
        PatchJump(EmitJump(), top);
        PatchHere(exits);
        PatchHere(scope.Breaks);
    }

    private void Repeat(RepeatStat r)
    {
        _repeatStack.Push(r);
        try { RepeatCore(r); }
        finally { _repeatStack.Pop(); }
    }

    private void RepeatCore(RepeatStat r)
    {
        string? label = _loopLabel; // read now: compiling the loop's own expressions may reset it
        int top = Pc;
        EnterBlock(true);
        BlockScope scope = _fs.Block!;
        scope.LoopLabel = label;
        scope.IsLoopBody = true;
        BlockStats(r.Body);
        if (scope.Continues.Count > 0)
        {
            // `continue` jumps to the condition, which can see the body's locals: one declared
            // after the continue would be read without ever having been set.
            if (SkippedLocalUsed(r.Cond, scope) is { } skipped)
                throw Error(r.Cond, $"'continue' skips the declaration of local '{skipped}', which the 'until' condition uses");
            PatchHere(scope.Continues);
        }
        SetLine(r.Cond);
        var again = new List<int>();
        CondJump(r.Cond, false, again);
        if (scope.TbcReg >= 0)
        {
            // The body's <close> variables must also be closed when looping back.
            int reg = scope.TbcReg;
            LeaveBlock();               // exit path: closes, then skips the loop-back code
            int skip = EmitJump();
            PatchHere(again);
            Emit(OpCode.Close, reg);
            PatchJump(EmitJump(), top);
            PatchJump(skip, Pc);
        }
        else
        {
            LeaveBlock();
            foreach (int j in again) PatchJump(j, top);
        }
        PatchHere(scope.Breaks);
    }

    // The name of a local of the repeat body that was declared after the body's first
    // `continue` and is used by the loop condition, or null.
    private string? SkippedLocalUsed(Expr cond, BlockScope body)
    {
        var skipped = new HashSet<LocalSym>();
        foreach (Stat s in ((RepeatStat)_repeatStack.Peek()).Body.Stats)
        {
            foreach (AttribName n in s switch
            {
                LocalStat l => l.Names,
                LocalFunctionStat lf => new List<AttribName> { lf.Name },
                _ => new List<AttribName>(),
            })
            {
                if (n.Symbol is LocalSym sym && _fs.LocVars.FindLastIndex(v => v.Name == sym.Name && v.Reg == sym.Reg) >= body.LocVarsAtContinue)
                    skipped.Add(sym);
            }
        }
        return skipped.Count == 0 ? null : FindUse(cond, skipped);
    }

    private static string? FindUse(Expr? e, HashSet<LocalSym> syms)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (e)
        {
            case null: return null;
            case NameExpr n: return n.Symbol is LocalSym s && syms.Contains(s) ? s.Name : null;
            case IndexExpr i: return FindUse(i.Object, syms) ?? FindUse(i.Key, syms);
            case CallExpr c: return FindUse(c.Callee, syms) ?? c.Args.Select(a => FindUse(a, syms)).FirstOrDefault(x => x != null);
            case MethodCallExpr m: return FindUse(m.Object, syms) ?? m.Args.Select(a => FindUse(a, syms)).FirstOrDefault(x => x != null);
            case TableExpr t: return t.Items.Select(it => FindUse(it.Key, syms) ?? FindUse(it.Value, syms)).FirstOrDefault(x => x != null);
            case CameoListExpr l: return l.Items.Select(it => FindUse(it, syms)).FirstOrDefault(x => x != null);
            case CameoListCompExpr c: return FindUse(c.Iterable, syms) ?? FindUse(c.Filter, syms) ?? FindUse(c.Body, syms);
            case CameoMatchExpr m:
                return FindUse(m.Scrutinee, syms) ?? m.Arms.Select(a => FindUse(a.ExprBody, syms)).FirstOrDefault(x => x != null);
            case BinaryExpr b: return FindUse(b.Left, syms) ?? FindUse(b.Right, syms);
            case UnaryExpr u: return FindUse(u.Operand, syms);
            case ParenExpr p: return FindUse(p.Inner, syms);
            case CastExpr c: return FindUse(c.Inner, syms);
            case IsExpr i: return FindUse(i.Inner, syms);
            default: return null; // literals; a function literal captures the variable, it does not read it now
        }
    }

    private readonly Stack<Stat> _repeatStack = new();
    private string? _pendingLabel;

    private void If(IfStat s)
    {
        var toEnd = new List<int>();
        for (int i = 0; i < s.Clauses.Count; i++)
        {
            var (cond, body) = s.Clauses[i];
            SetLine(cond);
            var skip = new List<int>();
            CondJump(cond, false, skip);
            _fs.FreeReg = _fs.ActiveTop;
            Block(body);
            bool more = i < s.Clauses.Count - 1 || s.Else != null;
            if (more) toEnd.Add(EmitJump());
            PatchHere(skip);
        }
        if (s.Else != null) Block(s.Else);
        PatchHere(toEnd);
    }

    private void ForNum(ForNumStat f)
    {
        string? label = _loopLabel; // read now: compiling the loop's own expressions may reset it
        EnterBlock(true);
        BlockScope scope = _fs.Block!;
        scope.LoopLabel = label;
        int @base = Alloc(4);
        ExprTo(f.Start, @base);
        ExprTo(f.Limit, @base + 1);
        if (f.Step != null) ExprTo(f.Step, @base + 2);
        else Emit(OpCode.LoadInt, @base + 2, 1);
        _fs.FreeReg = @base + 4;
        _fs.ActiveTop = @base + 4;
        SetLine(f);
        int prep = Emit(OpCode.ForPrep, @base);
        int bodyStart = Pc;
        EnterBlock(false);
        BlockScope body = _fs.Block!;
        body.IsLoopBody = true;
        body.LoopLabel = scope.LoopLabel;
        DeclareLocal((LocalSym)f.Var.Symbol!, @base + 3);
        BlockStats(f.Body);
        LeaveBlock();
        PatchHere(body.Continues);
        _fs.ActiveTop = @base + 4;
        SetLine(f);
        int loop = Emit(OpCode.ForLoop, @base);
        PatchJump(loop, bodyStart);
        PatchJump(prep, Pc);
        PatchHere(scope.Breaks);
        LeaveBlock();
    }

    private void ForIn(ForInStat f)
    {
        string? label = _loopLabel; // read now: compiling the loop's own expressions may reset it
        EnterBlock(true);
        BlockScope scope = _fs.Block!;
        scope.LoopLabel = label;
        int @base = _fs.FreeReg;
        bool useCameoIter = f.CameoIterCount > 0 || (f.CameoIterCount == 0 && f.CameoForIn && f.Exprs.Count == 1);
        if (useCameoIter)
        {
            int arity = f.CameoIterCount > 0 ? f.CameoIterCount : (f.Names.Count == 1 ? 1 : 2);
            int fnReg = Alloc(); // == @base
            Emit(OpCode.GetGlobal, fnReg, KString(arity == 1 ? "__cameo_iter1" : "__cameo_iter2"));
            if (f.CameoIterCount == 0 && IsMulti(f.Exprs[0]))
            {
                Multi(f.Exprs[0], -1);
                Emit(OpCode.Call, @base, 0, 4); // variable args -> 3 iterator results
            }
            else
            {
                int argReg = Alloc(); // == @base + 1
                ExprTo(f.Exprs[0], argReg);
                Emit(OpCode.Call, @base, 2, 4); // 1 arg -> 3 iterator results
            }
            _fs.FreeReg = @base + 3;
            Touch(@base + 3);
        }
        else
        {
            ExprListTo(f.Exprs, 3);
        }
        int nvars = f.Names.Count;
        Alloc(nvars);
        Touch(@base + 3 + Math.Max(nvars, 3) + 2);
        _fs.ActiveTop = _fs.FreeReg;
        SetLine(f);
        int toCall = EmitJump();
        int bodyStart = Pc;
        EnterBlock(false);
        BlockScope body = _fs.Block!;
        body.IsLoopBody = true;
        body.LoopLabel = scope.LoopLabel;
        for (int i = 0; i < nvars; i++) DeclareLocal((LocalSym)f.Names[i].Symbol!, @base + 3 + i);
        BlockStats(f.Body);
        LeaveBlock();
        PatchHere(body.Continues);
        _fs.ActiveTop = @base + 3 + nvars;
        PatchJump(toCall, Pc);
        SetLine(f);
        Emit(OpCode.TForCall, @base, 0, nvars);
        int loop = Emit(OpCode.TForLoop, @base);
        PatchJump(loop, bodyStart);
        PatchHere(scope.Breaks);
        LeaveBlock();
    }

    private void FunctionStatement(FunctionStat f)
    {
        string name = f.Root.Name + (f.Path.Count > 0 ? (f.IsMethod
            ? (f.Path.Count > 1 ? "." + string.Join(".", f.Path.Take(f.Path.Count - 1)) : "") + ":" + f.Path[^1]
            : "." + string.Join(".", f.Path)) : "");
        if (f.Path.Count == 0)
        {
            if (f.Root.Symbol is LocalSym sym && sym.Owner == _fs.Info && !sym.Captured)
            {
                ClosureTo(f.Body, FuncName(name, f.Body), sym.Reg, f);
            }
            else
            {
                int tmp = Alloc();
                ClosureTo(f.Body, FuncName(name, f.Body), tmp, f);
                StoreName(f.Root, tmp);
            }
            return;
        }
        int obj = ExprToAnyReg(f.Root);
        for (int i = 0; i < f.Path.Count - 1; i++)
        {
            int next = Alloc();
            Emit(OpCode.GetField, next, obj, KString(f.Path[i]));
            obj = next;
        }
        int fn = Alloc();
        ClosureTo(f.Body, FuncName(name, f.Body), fn, f);
        Emit(OpCode.SetField, obj, KString(f.Path[^1]), fn);
    }

    private void Return(ReturnStat r)
    {
        int n = r.Values.Count;
        // Inside the scope of <close> variables they are closed after the return values have
        // been computed, and the call in `return f()` cannot be a tail call.
        bool closing = PendingTbcReg(null) >= 0;
        if (n == 0)
        {
            if (closing) Emit(OpCode.Close, 0);
            Emit(OpCode.Return, 0, 1);
            return;
        }
        if (n == 1)
        {
            Expr v = r.Values[0];
            Expr sv = StripCasts(v);
            if (sv is CallExpr or MethodCallExpr && sv.Macro == null && !IsIntrinsicCall(sv, out _))
            {
                int cb = Call(sv, -1, -1, tail: !closing);
                if (closing) Emit(OpCode.Close, 0, 1);
                Emit(OpCode.Return, cb, 0);
                return;
            }
            if (sv is NameExpr { Symbol: LocalSym sym } && sym.Owner == _fs.Info && !sym.Captured)
            {
                SetLine(r);
                if (closing) Emit(OpCode.Close, 0);
                Emit(OpCode.Return, sym.Reg, 2);
                return;
            }
            if (sv is VarargExpr)
            {
                int vb = Multi(sv, -1);
                if (closing) Emit(OpCode.Close, 0, 1);
                Emit(OpCode.Return, vb, 0);
                return;
            }
            int reg = Alloc();
            ExprTo(v, reg);
            SetLine(r);
            if (closing) Emit(OpCode.Close, 0);
            Emit(OpCode.Return, reg, 2);
            return;
        }
        int @base = _fs.FreeReg;
        bool open = false;
        for (int i = 0; i < n; i++)
        {
            Expr v = r.Values[i];
            if (i == n - 1 && IsMulti(v))
            {
                Multi(v, -1);
                open = true;
            }
            else ExprTo(v, Alloc());
        }
        SetLine(r);
        if (closing) Emit(OpCode.Close, 0, open ? 1 : 0);
        Emit(OpCode.Return, @base, open ? 0 : n + 1);
    }

    private void Goto(GotoStat g)
    {
        int closeReg = -1; // lowest <close> register among the blocks this goto leaves
        for (BlockScope? b = _fs.Block; b != null; b = b.Parent)
        {
            if (b.Labels.TryGetValue(g.Label, out int target))
            {
                if (closeReg >= 0) Emit(OpCode.Close, closeReg);
                PatchJump(EmitJump(), target);
                return;
            }
            if (b.TbcReg >= 0) closeReg = b.TbcReg;
        }
        // Forward goto: the label is not known yet. If a <close> variable is in scope, reserve a
        // Close instruction that closes nothing until the goto is found to leave that scope.
        int closePc = PendingTbcReg(null) >= 0 ? Emit(OpCode.Close, NoRegister) : -1;
        _fs.Block!.PendingGotos.Add((g, EmitJump(), closePc));
    }

    private const int NoRegister = 1 << 24;

    private void Label(LabelStat l)
    {
        BlockScope b = _fs.Block!;
        for (BlockScope? s = b; s != null; s = s.Parent)
            if (s.Labels.ContainsKey(l.Name)) throw Error(l, $"label '{l.Name}' already defined");
        b.Labels[l.Name] = Pc;
        for (int i = b.PendingGotos.Count - 1; i >= 0; i--)
        {
            if (b.PendingGotos[i].Stat.Label == l.Name)
            {
                PatchJump(b.PendingGotos[i].Pc, Pc);
                b.PendingGotos.RemoveAt(i);
            }
        }
    }

    private void TypeDecl(TypeDeclStat t)
    {
        if (!t.DeclaresValue) return;
        int reg = Alloc();
        if (t.AliasValueExpr != null) ExprTo(t.AliasValueExpr, reg);
        else RecordTable((RecordTypeNode)t.Def!, reg);
        if (t.IsGlobal) Emit(OpCode.SetGlobal, reg, KString(t.Name.Name));
        else DeclareLocal((LocalSym)t.Name.Symbol!, reg);
    }

    // The run-time value of a record declaration: a table whose fields are its nested records.
    private void RecordTable(RecordTypeNode r, int dst)
    {
        Emit(OpCode.NewTable, dst, 0, 0);
        foreach (NestedTypeDecl nt in r.NestedTypes)
        {
            if (nt.Def is RecordTypeNode { IsInterface: false } nested)
            {
                int tmp = Alloc();
                RecordTable(nested, tmp);
                Emit(OpCode.SetField, dst, KString(nt.Name), tmp);
                _fs.FreeReg = tmp;
            }
        }
    }

    // ================================================================== expressions

    /// <summary>Returns a register holding the value of <paramref name="e"/> (a variable's own register when possible).</summary>
    private int ExprToAnyReg(Expr e)
    {
        Expr s = e;
        while (true)
        {
            if (s is CastExpr c) s = c.Inner;
            else if (s is ParenExpr p && !IsMulti(p.Inner)) s = p.Inner;
            else break;
        }
        if (s is NameExpr { Symbol: LocalSym sym } && sym.Owner == _fs.Info && !sym.Captured) return sym.Reg;
        int r = Alloc();
        ExprTo(e, r);
        return r;
    }

    /// <summary>Returns an RK operand: a constant (negative) or a register.</summary>
    private int ExprToRK(Expr e)
    {
        Expr s = e;
        while (true)
        {
            if (s is CastExpr c) s = c.Inner;
            else if (s is ParenExpr p && !IsMulti(p.Inner)) s = p.Inner;
            else break;
        }
        switch (s)
        {
            case NumberExpr n: return ~(n.IsInteger ? KInt(n.IntValue) : KFloat(n.FloatValue));
            case StringExpr str: return ~KString(str.Value);
            case NilExpr: return ~KNil();
            case BoolExpr b: return ~KBool(b.Value);
        }
        return ExprToAnyReg(e);
    }

    /// <summary>Compiles <paramref name="e"/> so that its (single) value ends up in register <paramref name="dst"/>.</summary>
    private void ExprTo(Expr e, int dst)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        int mark = _fs.FreeReg;
        SetLine(e);
        if (e.Macro != null)
        {
            MacroTo(e.Macro, dst);
            _fs.FreeReg = mark;
            return;
        }
        switch (e)
        {
            case NilExpr:
                Emit(OpCode.LoadNil, dst, 0);
                break;
            case BoolExpr b:
                Emit(OpCode.LoadBool, dst, b.Value ? 1 : 0);
                break;
            case NumberExpr n:
                if (n.IsInteger && n.IntValue >= int.MinValue && n.IntValue <= int.MaxValue) Emit(OpCode.LoadInt, dst, (int)n.IntValue);
                else Emit(OpCode.LoadK, dst, n.IsInteger ? KInt(n.IntValue) : KFloat(n.FloatValue));
                break;
            case StringExpr s:
                Emit(OpCode.LoadK, dst, KString(s.Value));
                break;
            case VarargExpr:
                Emit(OpCode.VarArg, dst, 0, 2);
                break;
            case FunctionExpr f:
                ClosureTo(f.Body, FuncName(f.Body.Name, f.Body), dst, f);
                break;
            case NameExpr n:
                LoadName(n, dst);
                break;
            case IndexExpr ix:
                {
                    int obj = ExprToAnyReg(ix.Object);
                    if (ix.IsOptional)
                    {
                        SetLine(ix);
                        Emit(OpCode.LoadNil, dst, 0);
                        Emit(OpCode.Eq, 1, obj, ~KNil());
                        int skip = EmitJump();
                        if (ix.Key is StringExpr osk)
                            Emit(OpCode.GetField, dst, obj, KString(osk.Value));
                        else
                        {
                            int ok = ExprToRK(ix.Key);
                            Emit(OpCode.GetTable, dst, obj, ok);
                        }
                        PatchJump(skip, Pc);
                    }
                    else if (ix.Key is StringExpr sk)
                    {
                        SetLine(ix);
                        Emit(OpCode.GetField, dst, obj, KString(sk.Value));
                    }
                    else
                    {
                        int k = ExprToRK(ix.Key);
                        SetLine(ix);
                        Emit(OpCode.GetTable, dst, obj, k);
                    }
                    break;
                }
            case CallExpr when IsIntrinsicCall(e, out MathFn mathFn):
                {
                    int arg = ExprToAnyReg(((CallExpr)e).Args[0]);
                    SetLine(e);
                    Emit(OpCode.MathFn, dst, arg, (int)mathFn);
                    break;
                }
            case CallExpr:
            case MethodCallExpr:
                Call(e, dst, 1);
                break;
            case TableExpr t:
                Table(t, dst);
                break;
            case CameoListExpr l:
                CompileCameoList(l, dst);
                break;
            case CameoListCompExpr c:
                CompileCameoListComp(c, dst);
                break;
            case CameoMatchExpr m:
                CompileCameoMatch(m, dst);
                break;
            case BinaryExpr b:
                Binary(b, dst);
                break;
            case UnaryExpr u:
                {
                    int r = ExprToAnyReg(u.Operand);
                    SetLine(u);
                    Emit(u.Op switch
                    {
                        UnaryOp.Neg => OpCode.Unm,
                        UnaryOp.Not => OpCode.Not,
                        UnaryOp.Len => OpCode.Len,
                        _ => OpCode.BNot,
                    }, dst, r);
                    break;
                }
            case ParenExpr p:
                ExprTo(p.Inner, dst);
                break;
            case CastExpr c:
                ExprTo(c.Inner, dst);
                break;
            case IsExpr i:
                {
                    int r = ExprToAnyReg(i.Inner);
                    TypeTest test = i.Test ?? SyntacticTest(i.Type);
                    SetLine(i);
                    if (r == dst)
                    {
                        // the tests below write dst before they are done reading the value
                        int copy = Alloc();
                        Emit(OpCode.Move, copy, r);
                        r = copy;
                    }
                    EmitTypeTest(test, r, dst);
                    break;
                }
            default:
                throw Error(e, "internal error: unknown expression " + e.GetType().Name);
        }
        _fs.FreeReg = mark;
    }

    // Inline expansion of a macroexp: bind each parameter to its argument (evaluated once,
    // left to right) and compile the macro's return expression in place.
    private void MacroTo(MacroExpansion m, int dst)
    {
        int savedActive = _fs.ActiveTop;
        int n = m.ParamNames.Count;
        int @base = _fs.FreeReg;
        for (int i = 0; i < n; i++)
        {
            int r = Alloc();
            if (i < m.Args.Count) ExprTo(m.Args[i], r);
            else Emit(OpCode.LoadNil, r, 0);
        }
        for (int i = n; i < m.Args.Count; i++)
        {
            // surplus arguments are still evaluated
            int mark = _fs.FreeReg;
            ExprTo(m.Args[i], Alloc());
            _fs.FreeReg = mark;
        }
        int firstLocVar = _fs.LocVars.Count;
        for (int i = 0; i < n; i++) DeclareLocal((LocalSym)m.ParamSymbols[i], @base + i);
        int tmp = Alloc();
        ExprTo(m.Body, tmp);
        if (m.Negate) Emit(OpCode.Not, dst, tmp);
        else Emit(OpCode.Move, dst, tmp);
        for (int i = firstLocVar; i < _fs.LocVars.Count; i++)
        {
            var lv = _fs.LocVars[i];
            if (lv.End < 0) _fs.LocVars[i] = (lv.Name, lv.Reg, lv.Start, Pc);
        }
        _fs.ActiveTop = savedActive;
    }

    private void LoadName(NameExpr n, int dst)
    {
        if (n.Symbol is LocalSym sym)
        {
            if (sym.Owner == _fs.Info)
            {
                if (sym.Captured) Emit(OpCode.GetBox, dst, sym.Reg);
                else if (sym.Reg != dst) Emit(OpCode.Move, dst, sym.Reg);
            }
            else Emit(OpCode.GetUpval, dst, _fs.Info.UpvalueIndex[sym]);
        }
        else Emit(OpCode.GetGlobal, dst, KString(n.Name));
    }

    private void CompileCameoList(CameoListExpr l, int dst)
    {
        if (l.Items.Count == 0)
        {
            int fnReg = Alloc();
            Emit(OpCode.GetGlobal, fnReg, KString("List"));
            Emit(OpCode.GetField, fnReg, fnReg, KString("new"));
            Emit(OpCode.Call, fnReg, 1, 2);
            if (fnReg != dst) Emit(OpCode.Move, dst, fnReg);
            return;
        }
        if (l.Items.Count <= 50)
        {
            int fnReg = Alloc();
            Emit(OpCode.GetGlobal, fnReg, KString("List"));
            Emit(OpCode.GetField, fnReg, fnReg, KString("of"));
            bool open = false;
            int nargs = 0;
            for (int i = 0; i < l.Items.Count; i++)
            {
                Expr item = l.Items[i];
                if (i == l.Items.Count - 1 && IsMulti(item))
                {
                    Multi(item, -1);
                    open = true;
                }
                else
                {
                    ExprTo(item, Alloc());
                    nargs++;
                }
            }
            SetLine(l);
            Emit(OpCode.Call, fnReg, open ? 0 : nargs + 1, 2);
            if (fnReg != dst) Emit(OpCode.Move, dst, fnReg);
            return;
        }
        // Large list literal: construct List.new() and push each item to keep register pressure O(1).
        int resReg = Alloc();
        Emit(OpCode.GetGlobal, resReg, KString("List"));
        Emit(OpCode.GetField, resReg, resReg, KString("new"));
        Emit(OpCode.Call, resReg, 1, 2);
        foreach (Expr item in l.Items)
        {
            int callBase = _fs.FreeReg;
            Alloc(3);
            Emit(OpCode.Self, callBase, resReg, KString("push"));
            ExprTo(item, callBase + 2);
            Emit(OpCode.Call, callBase, 3, 1);
            _fs.FreeReg = callBase;
        }
        if (resReg != dst) Emit(OpCode.Move, dst, resReg);
    }

    private void CompileCameoListComp(CameoListCompExpr c, int dst)
    {
        int savedActive = _fs.ActiveTop;
        int resReg = Alloc();
        Emit(OpCode.GetGlobal, resReg, KString("List"));
        Emit(OpCode.GetField, resReg, resReg, KString("new"));
        Emit(OpCode.Call, resReg, 1, 2);

        int @base = _fs.FreeReg;
        int arity = c.IterCount > 0 ? c.IterCount : (c.Vars.Count == 1 ? 1 : 2);
        int fnReg = Alloc(); // == @base
        Emit(OpCode.GetGlobal, fnReg, KString(arity == 1 ? "__cameo_iter1" : "__cameo_iter2"));
        if (IsMulti(c.Iterable))
        {
            Multi(c.Iterable, -1);
            Emit(OpCode.Call, @base, 0, 4);
        }
        else
        {
            int argReg = Alloc(); // == @base + 1
            ExprTo(c.Iterable, argReg);
            Emit(OpCode.Call, @base, 2, 4);
        }
        _fs.FreeReg = @base + 3;
        int nvars = c.Vars.Count;
        Alloc(nvars);
        Touch(@base + 3 + Math.Max(nvars, 3) + 2);
        _fs.ActiveTop = _fs.FreeReg;

        SetLine(c);
        int toCall = EmitJump();
        int bodyStart = Pc;
        EnterBlock(false);
        for (int i = 0; i < nvars; i++)
            DeclareLocal((LocalSym)c.Vars[i].Symbol!, @base + 3 + i);

        var skip = new List<int>();
        if (c.Filter != null)
        {
            SetLine(c.Filter);
            CondJump(c.Filter, false, skip);
        }

        int callBase = _fs.FreeReg;
        Alloc(3);
        Emit(OpCode.Self, callBase, resReg, KString("push"));
        ExprTo(c.Body, callBase + 2);
        SetLine(c.Body);
        Emit(OpCode.Call, callBase, 3, 1);
        _fs.FreeReg = callBase;

        PatchHere(skip);
        LeaveBlock();
        _fs.ActiveTop = @base + 3 + nvars;
        PatchJump(toCall, Pc);
        SetLine(c);
        Emit(OpCode.TForCall, @base, 0, nvars);
        int loop = Emit(OpCode.TForLoop, @base);
        PatchJump(loop, bodyStart);

        _fs.ActiveTop = savedActive;
        if (resReg != dst) Emit(OpCode.Move, dst, resReg);
    }

    private void CompileCameoMatch(CameoMatchExpr m, int dst)
    {
        int savedActive = _fs.ActiveTop;
        int resReg = dst >= 0 ? Alloc() : -1;
        if (resReg >= 0) Emit(OpCode.LoadNil, resReg, 0);

        int scrutReg = Alloc();
        ExprTo(m.Scrutinee, scrutReg);
        if (m.ScrutineeSymbol is LocalSym ssym)
            DeclareLocal(ssym, scrutReg);
        else
            _fs.ActiveTop = Math.Max(_fs.ActiveTop, scrutReg + 1);

        var toEnd = new List<int>();
        foreach (MatchArm arm in m.Arms)
        {
            bool isWildcard = arm.Patterns.Any(p => p.IsWildcard);
            int? skipArm = null;
            if (!isWildcard)
            {
                var matched = new List<int>();
                foreach (MatchPattern p in arm.Patterns)
                {
                    if (p.Literal != null)
                    {
                        int markP = _fs.FreeReg;
                        int rk = ExprToRK(p.Literal);
                        SetLine(p);
                        Emit(OpCode.Eq, 1, scrutReg, rk);
                        matched.Add(EmitJump());
                        _fs.FreeReg = markP;
                    }
                    else if (p.TypeTest != null)
                    {
                        int tmp = Alloc();
                        SetLine(p);
                        EmitTypeTest(p.CompiledTest ?? SyntacticTest(p.TypeTest), scrutReg, tmp);
                        matched.Add(EmitJump(OpCode.JmpIf, tmp));
                        _fs.FreeReg = tmp;
                    }
                }
                skipArm = EmitJump();
                PatchHere(matched);
            }

            EnterBlock(false);
            foreach (MatchPattern p in arm.Patterns)
            {
                if (p.Binding?.Symbol is LocalSym bsym)
                {
                    int bReg = Alloc();
                    Emit(OpCode.Move, bReg, scrutReg);
                    DeclareLocal(bsym, bReg);
                }
            }
            if (arm.BlockBody != null) BlockStats(arm.BlockBody);
            if (arm.ExprBody != null && resReg >= 0) ExprTo(arm.ExprBody, resReg);
            LeaveBlock();
            toEnd.Add(EmitJump());

            if (skipArm != null) PatchJump(skipArm.Value, Pc);
        }

        PatchHere(toEnd);
        _fs.ActiveTop = savedActive;
        if (resReg >= 0 && resReg != dst) Emit(OpCode.Move, dst, resReg);
    }

    private void CompileCameoImport(CameoImportStat imp)
    {
        SetLine(imp);
        int callBase = Alloc();
        Emit(OpCode.GetGlobal, callBase, KString("require"));
        int argReg = Alloc();
        Emit(OpCode.LoadK, argReg, KString(imp.Module));
        Emit(OpCode.Call, callBase, 2, 2); // 1 arg, 1 result in callBase
        _fs.FreeReg = callBase + 1;

        if (imp.DefaultName != null)
        {
            DeclareLocal((LocalSym)imp.DefaultName.Symbol!, callBase);
            return;
        }
        if (imp.Named != null)
        {
            foreach (CameoImportItem item in imp.Named)
            {
                if (!item.HasRuntimeValue || item.LocalName.Symbol is not LocalSym sym) continue;
                int reg = Alloc();
                Emit(OpCode.GetField, reg, callBase, KString(item.ExportName));
                DeclareLocal(sym, reg);
            }
        }
    }

    private void Binary(BinaryExpr b, int dst)
    {
        switch (b.Op)
        {
            case BinaryOp.Coalesce:
                {
                    ExprTo(b.Left, dst);
                    Emit(OpCode.Eq, 0, dst, ~KNil()); // if dst ~= nil, skip right
                    int done = EmitJump();
                    ExprTo(b.Right, dst);
                    PatchJump(done, Pc);
                    return;
                }
            case BinaryOp.And:
            case BinaryOp.Or:
                {
                    ExprTo(b.Left, dst);
                    int j = EmitJump(b.Op == BinaryOp.And ? OpCode.JmpIfNot : OpCode.JmpIf, dst);
                    ExprTo(b.Right, dst);
                    PatchJump(j, Pc);
                    return;
                }
            case BinaryOp.Eq:
            case BinaryOp.Ne:
            case BinaryOp.Lt:
            case BinaryOp.Le:
            case BinaryOp.Gt:
            case BinaryOp.Ge:
                {
                    var whenFalse = new List<int>();
                    CondJump(b, false, whenFalse);
                    Emit(OpCode.LoadBool, dst, 1, 1);
                    PatchHere(whenFalse);
                    Emit(OpCode.LoadBool, dst, 0, 0);
                    return;
                }
            case BinaryOp.Concat:
                {
                    // flatten a .. b .. c into one instruction over consecutive registers
                    var parts = new List<Expr>();
                    Expr cur = b;
                    while (cur is BinaryExpr { Op: BinaryOp.Concat } cb)
                    {
                        parts.Add(cb.Left);
                        cur = cb.Right;
                    }
                    parts.Add(cur);
                    int first = _fs.FreeReg;
                    foreach (Expr part in parts) ExprTo(part, Alloc());
                    SetLine(b);
                    Emit(OpCode.Concat, dst, first, first + parts.Count - 1);
                    return;
                }
        }
        int l = ExprToRK(b.Left);
        int r = ExprToRK(b.Right);
        SetLine(b);
        Emit(b.Op switch
        {
            BinaryOp.Add => OpCode.Add,
            BinaryOp.Sub => OpCode.Sub,
            BinaryOp.Mul => OpCode.Mul,
            BinaryOp.Div => OpCode.Div,
            BinaryOp.IDiv => OpCode.IDiv,
            BinaryOp.Mod => OpCode.Mod,
            BinaryOp.Pow => OpCode.Pow,
            BinaryOp.BAnd => OpCode.BAnd,
            BinaryOp.BOr => OpCode.BOr,
            BinaryOp.BXor => OpCode.BXor,
            BinaryOp.Shl => OpCode.Shl,
            BinaryOp.Shr => OpCode.Shr,
            _ => throw Error(b, "internal error: unknown operator"),
        }, dst, l, r);
    }

    /// <summary>
    /// Emits code that jumps when the truthiness of <paramref name="e"/> equals <paramref name="jumpWhen"/>
    /// and falls through otherwise. The emitted jumps are appended to <paramref name="jumps"/> for patching.
    /// </summary>
    private void CondJump(Expr e, bool jumpWhen, List<int> jumps)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        while (true)
        {
            if (e is ParenExpr p) e = p.Inner;
            else if (e is CastExpr c) e = c.Inner;
            else break;
        }
        int mark = _fs.FreeReg;
        if (e.Macro != null)
        {
            int mreg = ExprToAnyReg(e);
            jumps.Add(EmitJump(jumpWhen ? OpCode.JmpIf : OpCode.JmpIfNot, mreg));
            _fs.FreeReg = mark;
            return;
        }
        switch (e)
        {
            case UnaryExpr { Op: UnaryOp.Not } u:
                CondJump(u.Operand, !jumpWhen, jumps);
                return;
            case BinaryExpr { Op: BinaryOp.And } b:
                if (!jumpWhen)
                {
                    CondJump(b.Left, false, jumps);
                    CondJump(b.Right, false, jumps);
                }
                else
                {
                    var skip = new List<int>();
                    CondJump(b.Left, false, skip);
                    CondJump(b.Right, true, jumps);
                    PatchHere(skip);
                }
                return;
            case BinaryExpr { Op: BinaryOp.Or } b:
                if (jumpWhen)
                {
                    CondJump(b.Left, true, jumps);
                    CondJump(b.Right, true, jumps);
                }
                else
                {
                    var skip = new List<int>();
                    CondJump(b.Left, true, skip);
                    CondJump(b.Right, false, jumps);
                    PatchHere(skip);
                }
                return;
            case BinaryExpr { Op: BinaryOp.Eq or BinaryOp.Ne or BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge } b:
                {
                    int l = ExprToRK(b.Left);
                    int r = ExprToRK(b.Right);
                    SetLine(b);
                    bool expect = jumpWhen;
                    OpCode op;
                    switch (b.Op)
                    {
                        case BinaryOp.Eq: op = OpCode.Eq; break;
                        case BinaryOp.Ne: op = OpCode.Eq; expect = !expect; break;
                        case BinaryOp.Lt: op = OpCode.Lt; break;
                        case BinaryOp.Le: op = OpCode.Le; break;
                        case BinaryOp.Gt: op = OpCode.Lt; (l, r) = (r, l); break;
                        default: op = OpCode.Le; (l, r) = (r, l); break;
                    }
                    Emit(op, expect ? 1 : 0, l, r);
                    jumps.Add(EmitJump());
                    _fs.FreeReg = mark;
                    return;
                }
            case BoolExpr { Value: var v }:
                if (v == jumpWhen) jumps.Add(EmitJump());
                return;
            case NilExpr:
                if (!jumpWhen) jumps.Add(EmitJump());
                return;
        }
        int reg = ExprToAnyReg(e);
        jumps.Add(EmitJump(jumpWhen ? OpCode.JmpIf : OpCode.JmpIfNot, reg));
        _fs.FreeReg = mark;
    }

    /// <summary>
    /// Compiles a call. Results: want == -1 keeps all (open), otherwise exactly `want` values are left
    /// starting at the returned base register. When want == 1 and dst >= 0 the value is moved to dst.
    /// </summary>
    private int Call(Expr e, int dst, int want, bool tail = false)
    {
        int mark = _fs.FreeReg;
        int @base = dst >= 0 && IsTopTemp(dst) ? dst : Alloc();
        int nargs = 0;
        List<Expr> args;
        int? optionalSkip = null;
        CameoDotDispatch dotDispatch = e is CallExpr { CameoDotCall: true, Callee: IndexExpr { IsDot: true } } ce
            ? ce.DotDispatch
            : CameoDotDispatch.Function;
        bool useSelf = e is CallExpr { CameoDotCall: true, Callee: IndexExpr { IsDot: true } } && dotDispatch != CameoDotDispatch.Function;
        bool prepareDot = useSelf && dotDispatch == CameoDotDispatch.Dynamic;
        if (e is MethodCallExpr m)
        {
            int self = Alloc();
            ExprTo(m.Object, self);
            SetLine(m);
            Emit(OpCode.Self, @base, self, KString(m.Name));
            nargs = 1;
            args = m.Args;
        }
        else
        {
            var c = (CallExpr)e;
            if (useSelf)
            {
                var dot = (IndexExpr)c.Callee;
                int self = Alloc();
                ExprTo(dot.Object, self);
                if (c.IsOptional || dot.IsOptional)
                {
                    tail = false;
                    Emit(OpCode.LoadNil, @base, 0);
                    Emit(OpCode.Eq, 1, self, ~KNil());
                    optionalSkip = EmitJump();
                }
                SetLine(c);
                Emit(OpCode.Self, @base, self, KString(((StringExpr)dot.Key).Value));
                nargs = 1;
            }
            else if (c.IsOptional)
            {
                tail = false;
                ExprTo(c.Callee, @base);
                Emit(OpCode.Eq, 1, @base, ~KNil());
                optionalSkip = EmitJump();
            }
            else ExprTo(c.Callee, @base);
            args = c.Args;
        }
        bool open = false;
        for (int i = 0; i < args.Count; i++)
        {
            Expr a = args[i];
            if (i == args.Count - 1 && IsMulti(a))
            {
                Multi(a, -1);
                open = true;
            }
            else
            {
                ExprTo(a, Alloc());
                nargs++;
            }
        }
        SetLine(e);
        if (prepareDot)
        {
            Emit(OpCode.PrepareDotCall, @base, open ? 0 : nargs + 1);
            open = true;
        }
        if (tail) Emit(OpCode.TailCall, @base, open ? 0 : nargs + 1, 0);
        else Emit(OpCode.Call, @base, open ? 0 : nargs + 1, want + 1);
        if (optionalSkip is { } skipJmp)
        {
            int afterCall = EmitJump();
            PatchJump(skipJmp, Pc);
            if (want > 1) Emit(OpCode.LoadNil, @base, want - 1);
            else Emit(OpCode.LoadNil, @base, 0);
            PatchJump(afterCall, Pc);
        }
        if (want == 1 && dst >= 0)
        {
            _fs.FreeReg = mark;
            if (dst != @base) Emit(OpCode.Move, dst, @base);
            return dst;
        }
        _fs.FreeReg = @base + Math.Max(want, 0);
        Touch(_fs.FreeReg + 1);
        return @base;
    }

    private void Table(TableExpr t, int dst)
    {
        int reg = IsTopTemp(dst) ? dst : Alloc();
        int narr = 0, nhash = 0;
        foreach (TableItem item in t.Items)
        {
            if (item.Kind == TableItemKind.Positional) narr++; else nhash++;
        }
        SetLine(t);
        Emit(OpCode.NewTable, reg, Math.Min(narr, 1 << 20), Math.Min(nhash, 1 << 20));
        int pending = 0;   // positional values waiting in reg+1..reg+pending
        int stored = 0;    // positional values already stored
        for (int i = 0; i < t.Items.Count; i++)
        {
            TableItem item = t.Items[i];
            SetLine(item);
            switch (item.Kind)
            {
                case TableItemKind.Positional:
                    if (i == t.Items.Count - 1 && IsMulti(item.Value))
                    {
                        Multi(item.Value, -1);
                        Emit(OpCode.SetList, reg, 0, stored);
                        pending = 0;
                        _fs.FreeReg = reg + 1;
                        continue;
                    }
                    ExprTo(item.Value, Alloc());
                    pending++;
                    if (pending == 50)
                    {
                        Emit(OpCode.SetList, reg, pending, stored);
                        stored += pending;
                        pending = 0;
                        _fs.FreeReg = reg + 1;
                    }
                    break;
                case TableItemKind.Named:
                    {
                        int mark = _fs.FreeReg;
                        int v = ExprToRK(item.Value);
                        Emit(OpCode.SetField, reg, KString(((StringExpr)item.Key!).Value), v);
                        _fs.FreeReg = mark;
                        break;
                    }
                default:
                    {
                        int mark = _fs.FreeReg;
                        int k = ExprToRK(item.Key!);
                        int v = ExprToRK(item.Value);
                        Emit(OpCode.SetTable, reg, k, v);
                        _fs.FreeReg = mark;
                        break;
                    }
            }
        }
        if (pending > 0) Emit(OpCode.SetList, reg, pending, stored);
        _fs.FreeReg = reg + 1;
        if (t.CameoMetatableSym != null || t.CameoRecordId != null)
        {
            int callBase = Alloc();
            Emit(OpCode.GetGlobal, callBase, KString("__cameo_bless"));
            int a0 = Alloc();
            Emit(OpCode.Move, a0, reg);
            int a1 = Alloc();
            if (t.CameoMetatableSym is LocalSym mtSym)
            {
                if (mtSym.Owner == _fs.Info)
                {
                    if (mtSym.Captured) Emit(OpCode.GetBox, a1, mtSym.Reg);
                    else Emit(OpCode.Move, a1, mtSym.Reg);
                }
                else Emit(OpCode.GetUpval, a1, _fs.Info.UpvalueIndex[mtSym]);
            }
            else Emit(OpCode.LoadK, a1, KString(t.CameoRecordId!));
            Emit(OpCode.Call, callBase, 3, 1);
            _fs.FreeReg = reg + 1;
        }
        if (reg != dst) Emit(OpCode.Move, dst, reg);
    }

    // ================================================================== `is`

    // Fallback used when the type checker did not run: decide from the type's syntax alone.
    private static TypeTest SyntacticTest(TypeNode t)
    {
        switch (t)
        {
            case NameTypeNode { Names.Count: 1, TypeArgs: null } n:
                switch (n.Names[0])
                {
                    case "nil": return new KindTest(TypeKindTest.Nil);
                    case "boolean": return new KindTest(TypeKindTest.Boolean);
                    case "number": return new KindTest(TypeKindTest.Number);
                    case "integer": return new KindTest(TypeKindTest.Integer);
                    case "string": return new KindTest(TypeKindTest.String);
                    case "thread": return new KindTest(TypeKindTest.Thread);
                    case "userdata": return new KindTest(TypeKindTest.Userdata);
                }
                return new KindTest(TypeKindTest.Table);
            case FunctionTypeNode: return new KindTest(TypeKindTest.Function);
            case EnumTypeNode: return new KindTest(TypeKindTest.String);
            case UnionTypeNode u:
                {
                    var any = new AnyOfTest();
                    foreach (TypeNode m in u.Members) any.Options.Add(SyntacticTest(m));
                    return any;
                }
            default: return new KindTest(TypeKindTest.Table);
        }
    }

    // dst = (value in `reg` passes `test`); reg != dst.
    private void EmitTypeTest(TypeTest test, int reg, int dst)
    {
        switch (test)
        {
            case KindTest k:
                Emit(OpCode.IsKind, dst, reg, (int)k.Kind);
                break;
            case AnyOfTest any:
                {
                    if (any.Options.Count == 0)
                    {
                        Emit(OpCode.LoadBool, dst, 0);
                        break;
                    }
                    var done = new List<int>();
                    for (int i = 0; i < any.Options.Count; i++)
                    {
                        EmitTypeTest(any.Options[i], reg, dst);
                        if (i < any.Options.Count - 1) done.Add(EmitJump(OpCode.JmpIf, dst));
                    }
                    PatchHere(done);
                    break;
                }
            case WhereTest w:
                {
                    // Bind `self` to the tested value and evaluate the where clause.
                    int mark = _fs.FreeReg;
                    int savedActive = _fs.ActiveTop;
                    var sym = (LocalSym?)w.SelfSymbol ?? throw new InvalidOperationException("where test was not resolved");
                    int self = Alloc();
                    Emit(OpCode.Move, self, reg);
                    DeclareLocal(sym, self);
                    int tmp = Alloc();
                    ExprTo(w.Condition, tmp);
                    // normalise to a boolean
                    Emit(OpCode.Not, tmp, tmp);
                    Emit(OpCode.Not, dst, tmp);
                    _fs.FreeReg = mark;
                    _fs.ActiveTop = savedActive;
                    break;
                }
            default:
                throw new InvalidOperationException("unknown type test");
        }
    }
}
