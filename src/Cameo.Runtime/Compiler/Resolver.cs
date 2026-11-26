using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Compiler;

/// <summary>A local variable (or parameter) as seen by the compiler.</summary>
internal sealed class LocalSym
{
    public readonly string Name;
    public readonly FuncInfo Owner;
    /// <summary>Referenced from a nested function: lives in a heap box instead of directly in a register.</summary>
    public bool Captured;
    public int Reg = -1;
    public string? Attrib;
    /// <summary>Cameo let is enforced independently of optional static checking.</summary>
    public bool IsCameoLet;
    public LocalSym(string name, FuncInfo owner) { Name = name; Owner = owner; }
}

/// <summary>Per-function scope information computed by the resolver.</summary>
internal sealed class FuncInfo
{
    public readonly FuncInfo? Parent;
    public readonly FunctionBody Body;
    /// <summary>Variables of enclosing functions used by this function, in upvalue-index order.</summary>
    public readonly List<LocalSym> Upvalues = new();
    public readonly Dictionary<LocalSym, int> UpvalueIndex = new();
    public FuncInfo(FuncInfo? parent, FunctionBody body) { Parent = parent; Body = body; }

    public int UpvalueFor(LocalSym sym)
    {
        if (!UpvalueIndex.TryGetValue(sym, out int idx))
        {
            idx = Upvalues.Count;
            Upvalues.Add(sym);
            UpvalueIndex[sym] = idx;
        }
        return idx;
    }
}

/// <summary>
/// Binds every name to a local variable or a global, and works out which locals are
/// captured by closures. Runs after type checking and before code generation.
/// </summary>
internal sealed class Resolver
{
    private sealed class Scope
    {
        public readonly Scope? Parent;
        public readonly FuncInfo Func;
        public readonly Dictionary<string, LocalSym> Vars = new();
        public Scope(Scope? parent, FuncInfo func) { Parent = parent; Func = func; }
    }

    private readonly string _file;
    private Scope _scope = null!;
    private FuncInfo _func = null!;

    private Resolver(string file) { _file = file; }

    public static void Resolve(Chunk chunk)
    {
        var r = new Resolver(chunk.FileName);
        r.Function(chunk.Main);
    }

    private CameoSyntaxException Error(Node n, string msg) => new(new Diagnostic(_file, n.Line, n.Col, msg));

    private LocalSym Declare(string name, string? attrib = null)
    {
        var sym = new LocalSym(name, _func) { Attrib = attrib };
        _scope.Vars[name] = sym;
        return sym;
    }

    private LocalSym? Lookup(string name)
    {
        for (Scope? s = _scope; s != null; s = s.Parent)
        {
            if (s.Vars.TryGetValue(name, out LocalSym? sym))
            {
                if (sym.Owner != _func)
                {
                    sym.Captured = true;
                    // every function between the owner and the user needs the upvalue
                    for (FuncInfo? f = _func; f != null && f != sym.Owner; f = f.Parent) f.UpvalueFor(sym);
                }
                return sym;
            }
        }
        return null;
    }

    private void Push() => _scope = new Scope(_scope, _func);
    private void Pop() => _scope = _scope.Parent!;

    private void Function(FunctionBody body)
    {
        FuncInfo savedFunc = _func;
        Scope savedScope = _scope;
        _func = new FuncInfo(savedFunc, body);
        body.ScopeInfo = _func;
        _scope = new Scope(savedScope, _func);
        if (body.PrependIgnoredSelf && (body.Params.Count == 0 || body.Params[0].Name != "\0self"))
            body.Params.Insert(0, new Param { Name = "\0self", Line = body.Line, Col = body.Col });
        foreach (Param p in body.Params) p.Symbol = Declare(p.Name);
        BlockNoScope(body.Body);
        _func = savedFunc;
        _scope = savedScope;
    }

    private void Block(Block b)
    {
        Push();
        BlockNoScope(b);
        Pop();
    }

    private void BlockNoScope(Block b)
    {
        foreach (Stat s in b.Stats) Statement(s);
    }

    private void Statement(Stat s)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        switch (s)
        {
            case LocalStat l:
                foreach (Expr e in l.Values) Expression(e);
                foreach (AttribName n in l.Names)
                {
                    var symbol = Declare(n.Name, n.Attrib);
                    symbol.IsCameoLet = n.IsCameoLet;
                    n.Symbol = symbol;
                }
                break;
            case GlobalStat g:
                foreach (Expr e in g.Values) Expression(e);
                break;
            case AssignStat a:
                foreach (Expr e in a.Targets)
                {
                    Expression(e);
                    RejectLetAssignment(e);
                }
                foreach (Expr e in a.Values) Expression(e);
                break;
            case CallStat c:
                Expression(c.Call);
                break;
            case DoStat d:
                Block(d.Body);
                break;
            case WhileStat w:
                Expression(w.Cond);
                Block(w.Body);
                break;
            case RepeatStat r:
                Push();
                BlockNoScope(r.Body);
                Expression(r.Cond); // the condition sees the body's locals
                Pop();
                break;
            case IfStat i:
                foreach (var (cond, body) in i.Clauses)
                {
                    Expression(cond);
                    Block(body);
                }
                if (i.Else != null) Block(i.Else);
                break;
            case ForNumStat f:
                Expression(f.Start);
                Expression(f.Limit);
                if (f.Step != null) Expression(f.Step);
                Push();
                f.Var.Symbol = Declare(f.Var.Name);
                BlockNoScope(f.Body);
                Pop();
                break;
            case ForInStat f:
                foreach (Expr e in f.Exprs) Expression(e);
                Push();
                foreach (AttribName n in f.Names) n.Symbol = Declare(n.Name);
                BlockNoScope(f.Body);
                Pop();
                break;
            case FunctionStat f:
                Expression(f.Root);
                if (f.Path.Count == 0) RejectLetAssignment(f.Root);
                Function(f.Body);
                break;
            case LocalFunctionStat lf:
                lf.Name.Symbol = Declare(lf.Name.Name);
                Function(lf.Body);
                break;
            case GlobalFunctionStat gf:
                Function(gf.Body);
                break;
            case ReturnStat r:
                foreach (Expr e in r.Values) Expression(e);
                break;
            case TypeDeclStat t:
                TypeDecl(t);
                break;
            case MatchStat ms:
                Expression(ms.Match);
                break;
            case CameoImportStat imp:
                if (imp.DefaultName != null)
                {
                    LocalSym sym = Declare(imp.DefaultName.Name, "const");
                    sym.IsCameoLet = true;
                    imp.DefaultName.Symbol = sym;
                }
                if (imp.Named != null)
                {
                    foreach (CameoImportItem item in imp.Named)
                    {
                        if (!item.HasRuntimeValue) continue;
                        LocalSym sym = Declare(item.LocalName.Name, "const");
                        sym.IsCameoLet = true;
                        item.LocalName.Symbol = sym;
                    }
                }
                break;
            case BreakStat:
            case ContinueStat:
            case GotoStat:
            case LabelStat:
            case MacroExpStat:
                break;
            default:
                throw Error(s, "internal error: unknown statement " + s.GetType().Name);
        }
    }

    private void RejectLetAssignment(Expr target)
    {
        if (target is NameExpr { Symbol: LocalSym { IsCameoLet: true } symbol } name)
        {
            string keyword = symbol.Attrib == "close" ? "using" : "let";
            throw Error(target, $"cannot assign to {keyword} binding '{name.Name}'");
        }
    }

    private void TypeDecl(TypeDeclStat t)
    {
        if (t.Def is RecordTypeNode { IsInterface: false })
        {
            // A record is also a value: a table that holds its functions and nested records.
            t.DeclaresValue = true;
        }
        else if (t.Def is NameTypeNode nt && t.RuntimeValue != TypeDeclValue.None)
        {
            // Alias of another type. It has a value if the checker says so, or (without a
            // checker) if the aliased name is a local variable.
            var root = new NameExpr { Name = nt.Names[0], Line = nt.Line, Col = nt.Col };
            Expression(root);
            if (t.RuntimeValue == TypeDeclValue.AliasValue || root.Symbol != null)
            {
                Expr e = root;
                for (int i = 1; i < nt.Names.Count; i++)
                    e = new IndexExpr { Object = e, Key = new StringExpr { Value = nt.Names[i], Line = nt.Line, Col = nt.Col }, IsDot = true, Line = nt.Line, Col = nt.Col };
                t.AliasValueExpr = e;
                t.DeclaresValue = true;
            }
        }
        else if (t.Def is RequireTypeNode rq && t.RuntimeValue == TypeDeclValue.AliasValue)
        {
            var req = new NameExpr { Name = "require", Line = rq.Line, Col = rq.Col };
            Expression(req);
            Expr e = new CallExpr { Callee = req, Args = { new StringExpr { Value = rq.Module, Line = rq.Line, Col = rq.Col } }, Line = rq.Line, Col = rq.Col };
            foreach (string name in rq.Path)
                e = new IndexExpr { Object = e, Key = new StringExpr { Value = name, Line = rq.Line, Col = rq.Col }, IsDot = true, Line = rq.Line, Col = rq.Col };
            t.AliasValueExpr = e;
            t.DeclaresValue = true;
        }
        if (t.DeclaresValue && !t.IsGlobal) t.Name.Symbol = Declare(t.Name.Name);
    }

    private void Expression(Expr e)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        if (e.Macro is { } mx)
        {
            // macroexp use: the arguments are evaluated here, the body in a scope of its own
            // that sees only the parameters (plus whatever is visible at this point).
            foreach (Expr a in mx.Args) Expression(a);
            Push();
            mx.ParamSymbols.Clear();
            foreach (string name in mx.ParamNames) mx.ParamSymbols.Add(Declare(name));
            Expression(mx.Body);
            Pop();
            return;
        }
        switch (e)
        {
            case NilExpr:
            case BoolExpr:
            case NumberExpr:
            case StringExpr:
                break;
            case VarargExpr:
                if (!_func.Body.IsVarArg) throw Error(e, "cannot use '...' outside a vararg function");
                break;
            case NameExpr n:
                n.Symbol = Lookup(n.Name);
                break;
            case IndexExpr i:
                Expression(i.Object);
                Expression(i.Key);
                break;
            case CallExpr c:
                Expression(c.Callee);
                foreach (Expr a in c.Args) Expression(a);
                break;
            case MethodCallExpr m:
                Expression(m.Object);
                foreach (Expr a in m.Args) Expression(a);
                break;
            case FunctionExpr f:
                Function(f.Body);
                break;
            case TableExpr t:
                foreach (TableItem item in t.Items)
                {
                    if (item.Key != null) Expression(item.Key);
                    if (item.WrapDropSelf)
                    {
                        item.WrapDropSelf = false;
                        Expr inner = item.Value;
                        var wrapper = new FunctionExpr
                        {
                            Line = inner.Line, Col = inner.Col,
                            Body = new FunctionBody
                            {
                                Name = "method_adapter",
                                Line = inner.Line, Col = inner.Col, EndLine = inner.Line,
                                Params = [new Param { Name = "\0self", Line = inner.Line, Col = inner.Col }],
                                IsVarArg = true,
                                Body = new Block
                                {
                                    Line = inner.Line, Col = inner.Col,
                                    Stats =
                                    [
                                        new ReturnStat
                                        {
                                            Line = inner.Line, Col = inner.Col,
                                            Values =
                                            [
                                                new CallExpr
                                                {
                                                    Line = inner.Line, Col = inner.Col,
                                                    Callee = inner,
                                                    Args = [new VarargExpr { Line = inner.Line, Col = inner.Col }],
                                                }
                                            ]
                                        }
                                    ]
                                }
                            }
                        };
                        item.Value = wrapper;
                    }
                    Expression(item.Value);
                }
                if (t.CameoMetatableVar != null)
                    t.CameoMetatableSym = Lookup(t.CameoMetatableVar);
                break;
            case CameoListExpr l:
                foreach (Expr item in l.Items) Expression(item);
                break;
            case CameoListCompExpr c:
                Expression(c.Iterable);
                Push();
                foreach (AttribName n in c.Vars) n.Symbol = Declare(n.Name);
                if (c.Filter != null) Expression(c.Filter);
                Expression(c.Body);
                Pop();
                break;
            case CameoMatchExpr m:
                Expression(m.Scrutinee);
                Push();
                m.ScrutineeSymbol = Declare("\0match");
                foreach (MatchArm arm in m.Arms)
                {
                    Push();
                    foreach (MatchPattern p in arm.Patterns)
                    {
                        if (p.Literal != null) Expression(p.Literal);
                        if (p.CompiledTest != null) TypeTestExprs(p.CompiledTest);
                        if (p.Binding != null)
                        {
                            LocalSym bsym = Declare(p.Binding.Name, "const");
                            bsym.IsCameoLet = true;
                            p.Binding.Symbol = bsym;
                        }
                    }
                    if (arm.BlockBody != null) BlockNoScope(arm.BlockBody);
                    if (arm.ExprBody != null) Expression(arm.ExprBody);
                    Pop();
                }
                Pop();
                break;
            case BinaryExpr b:
                Expression(b.Left);
                Expression(b.Right);
                break;
            case UnaryExpr u:
                Expression(u.Operand);
                break;
            case ParenExpr p:
                Expression(p.Inner);
                break;
            case CastExpr c:
                Expression(c.Inner);
                break;
            case IsExpr i:
                Expression(i.Inner);
                if (i.Test != null) TypeTestExprs(i.Test);
                break;
            default:
                throw Error(e, "internal error: unknown expression " + e.GetType().Name);
        }
    }

    private void TypeTestExprs(TypeTest t)
    {
        switch (t)
        {
            case AnyOfTest any:
                foreach (TypeTest o in any.Options) TypeTestExprs(o);
                break;
            case WhereTest w:
                Push();
                w.SelfSymbol = Declare(w.SelfName);
                Expression(w.Condition);
                Pop();
                break;
        }
    }
}
