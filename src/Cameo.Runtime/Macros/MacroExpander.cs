using Cameo.Runtime.Compiler;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Macros;

/// <summary>
/// Compile-time macros. Macro bodies are Teal code: they are compiled with the ordinary
/// bytecode compiler and run on a private, sandboxed VM instance while the program is being
/// compiled. Their arguments and results are syntax, exchanged as "blocks" (see Blocks.cs).
/// Expansion happens right after parsing, so the type checker and the code generator only
/// ever see the expanded tree.
/// </summary>
internal sealed class MacroExpander
{
    private const int MaxDepth = 200;

    private sealed class Macro
    {
        public MacroDeclStat Decl = null!;
        public Value Function;
    }

    private readonly string _file;
    private readonly List<MacroQuote> _quotes;
    private readonly Dictionary<string, Macro> _macros = new();
    private readonly List<string> _errors = new();
    private LuaState? _vm;
    private int _whereLine = 1, _whereCol = 1;
    private int _depth;

    private MacroExpander(string file, List<MacroQuote> quotes)
    {
        _file = file;
        _quotes = quotes;
    }

    public static void Expand(Chunk chunk, List<MacroQuote> quotes)
    {
        var x = new MacroExpander(chunk.FileName, quotes);
        x.Run(chunk);
    }

    private CameoSyntaxException Error(Node at, string message) => new(new Diagnostic(_file, at.Line, at.Col, message));

    private void Run(Chunk chunk)
    {
        // Macro declarations are file-level; they are compiled and removed from the program.
        var stats = chunk.Body.Stats;
        for (int i = 0; i < stats.Count;)
        {
            if (stats[i] is MacroDeclStat decl)
            {
                Compile(decl);
                stats.RemoveAt(i);
            }
            else i++;
        }
        ExpandBlock(chunk.Body);
    }

    // ------------------------------------------------------------------ the macro VM

    private LuaState Vm
    {
        get
        {
            if (_vm != null) return _vm;
            var L = new LuaState();
            // Sandbox: no file, process, network, database or module access. The libraries that
            // reach outside the VM (aio, fs, db, futures, sandbox, cli) are not opened at all.
            StdlibRegistry.OpenAll(L, CameoLibraries.Extended);
            foreach (string name in new[] { "io", "debug", "package", "require", "load", "loadstring", "loadfile", "dofile", "collectgarbage", "arg" })
                L.Globals.SetStr(name, Value.Nil);
            Value os = L.Globals.GetStr("os");
            var safeOs = new Table();
            if (os.IsTable)
                foreach (string name in new[] { "clock", "date", "difftime", "time" }) safeOs.SetStr(name, os.AsTable.GetStr(name));
            L.Globals.SetStr("os", safeOs);
            Value tbl = L.Globals.GetStr("table");
            if (tbl.IsTable) L.Globals.SetStr("unpack", tbl.AsTable.GetStr("unpack"));

            var indexes = new Table();
            foreach (var (group, fields) in BlockInfo.Indexes)
            {
                var g = new Table();
                foreach (var (name, index) in fields) g.SetStr(name, index);
                indexes.SetStr(group, g);
            }
            L.Globals.SetStr("BLOCK_INDEXES", indexes);
            L.Register(L.Globals, "block", NewBlock);
            L.Register(L.Globals, "expect", Expect);
            L.Register(L.Globals, "clone", Clone);
            L.Register(L.Globals, "__macro_quote", Quote);
            _vm = L;
            return L;
        }
    }

    private int NewBlock(ref CallContext c)
    {
        string kind = c[0].IsString ? c[0].AsString : c[0].ToString();
        if (Array.IndexOf(BlockInfo.Kinds, kind) < 0) _errors.Add("unknown block kind: " + kind);
        var t = new Table(0, 8);
        t.SetStr("kind", kind);
        t.SetStr("tk", "");
        t.SetStr("f", _file);
        t.SetStr("y", _whereLine);
        t.SetStr("x", _whereCol);
        t.SetStr("yend", _whereLine);
        t.SetStr("xend", _whereCol);
        return c.Return(Value.FromTable(t));
    }

    private int Expect(ref CallContext c)
    {
        Value b = c[0];
        string kind = c.CheckString(1);
        if (BlockInfo.IsBlock(b) && BlockInfo.KindOf(b) == kind) return c.Return(b);
        _errors.Add($"expected {kind}, got {(BlockInfo.IsBlock(b) ? BlockInfo.KindOf(b) : "nil")}");
        return c.Return(Value.Nil);
    }

    private static int Clone(ref CallContext c) => c.Return(CloneValue(c[0], 0));

    private static Value CloneValue(Value v, int depth)
    {
        if (!v.IsTable) return v;
        if (depth > 2000) throw new LuaException("clone: block nesting too deep (cyclic block?)");
        Table src = v.AsTable;
        var dst = new Table();
        foreach (var kv in src.Pairs())
        {
            // named fields (kind, tk, positions and hidden syntax references) are copied, children cloned
            if (kv.Key.IsString) dst.Set(kv.Key, kv.Value);
            else dst.Set(kv.Key, CloneValue(kv.Value, depth + 1));
        }
        return Value.FromTable(dst);
    }

    // __macro_quote(index, vars): instantiate a quote template with the macro's variables.
    private int Quote(ref CallContext c)
    {
        long index = c.CheckInt(0);
        Table vars = c.CheckTable(1);
        if (index < 0 || index >= _quotes.Count) throw new LuaException("invalid macro quote");
        MacroQuote q = _quotes[(int)index];
        var builder = new BlockBuilder(_file, vars);
        try
        {
            if (q.IsStatements) return c.Return(Value.FromTable(builder.Statements(q.Stats!)));
            return c.Return(builder.Expr(q.Expr!));
        }
        catch (MacroException e)
        {
            throw new LuaException(Value.FromString(e.Message), true);
        }
    }

    // ------------------------------------------------------------------ compiling macros

    private void Compile(MacroDeclStat decl)
    {
        // Macro bodies may not expand other macros.
        new InvocationFinder(this).Check(decl.Body.Body);

        var fn = new LocalFunctionStat
        {
            Line = decl.Line, Col = decl.Col,
            Name = new AttribName { Name = decl.Name, Line = decl.Line, Col = decl.Col },
            Body = decl.Body,
        };
        var ret = new ReturnStat { Line = decl.Line, Col = decl.Col };
        ret.Values.Add(new NameExpr { Name = decl.Name, Line = decl.Line, Col = decl.Col });
        var block = new Block { Line = decl.Line, Col = decl.Col };
        block.Stats.Add(fn);
        block.Stats.Add(ret);
        var main = new FunctionBody { Name = "main chunk", IsVarArg = true, Body = block, Line = decl.Line, Col = decl.Col, EndLine = decl.Body.EndLine };
        var chunk = new Chunk { FileName = _file, Body = block, Main = main, Line = decl.Line, Col = decl.Col };
        Proto proto = BytecodeCompiler.Compile(chunk);
        LuaState L = Vm;
        try
        {
            Value[] r = L.Call(L.MakeFunction(proto));
            if (r.Length == 0 || !r[0].IsFunction) throw Error(decl, $"macro '{decl.Name}' did not compile to a function");
            _macros[decl.Name] = new Macro { Decl = decl, Function = r[0] };
        }
        catch (LuaException e)
        {
            throw Error(decl, e.Message);
        }
    }

    // Walks a macro body looking for things that are not allowed there.
    private sealed class InvocationFinder
    {
        private readonly MacroExpander _x;
        public InvocationFinder(MacroExpander x) { _x = x; }

        public void Check(Block b)
        {
            foreach (Stat s in b.Stats)
            {
                if (s is MacroCallStat mc) throw _x.Error(mc, "macros cannot be invoked inside a macro body");
                if (s is MacroDeclStat md) throw _x.Error(md, "macros can only be declared at the top level of a file");
                _x.VisitStat(s, Check, CheckExpr);
            }
        }

        private Expr CheckExpr(Expr e)
        {
            if (e is MacroCallExpr mc) throw _x.Error(mc, "macros cannot be invoked inside a macro body");
            _x.VisitExprChildren(e, Check, CheckExpr);
            return e;
        }
    }

    // ------------------------------------------------------------------ generic traversal

    private static void MapList(List<Expr> list, Func<Expr, Expr> f)
    {
        for (int i = 0; i < list.Count; i++) list[i] = f(list[i]);
    }

    /// <summary>Applies <paramref name="block"/> to nested blocks and <paramref name="expr"/> to every
    /// expression slot of a statement (replacing the expression with the returned one).</summary>
    private void VisitStat(Stat s, Action<Block> block, Func<Expr, Expr> expr)
    {
        switch (s)
        {
            case LocalStat l: MapList(l.Values, expr); break;
            case GlobalStat g: MapList(g.Values, expr); break;
            case AssignStat a: MapList(a.Targets, expr); MapList(a.Values, expr); break;
            case CallStat c: c.Call = expr(c.Call); break;
            case DoStat d: block(d.Body); break;
            case WhileStat w: w.Cond = expr(w.Cond); block(w.Body); break;
            case RepeatStat r: block(r.Body); r.Cond = expr(r.Cond); break;
            case IfStat i:
                for (int k = 0; k < i.Clauses.Count; k++)
                {
                    var (cond, body) = i.Clauses[k];
                    i.Clauses[k] = (expr(cond), body);
                    block(body);
                }
                if (i.Else != null) block(i.Else);
                break;
            case ForNumStat f:
                f.Start = expr(f.Start);
                f.Limit = expr(f.Limit);
                if (f.Step != null) f.Step = expr(f.Step);
                block(f.Body);
                break;
            case ForInStat f: MapList(f.Exprs, expr); block(f.Body); break;
            case FunctionStat f: block(f.Body.Body); break;
            case LocalFunctionStat lf: block(lf.Body.Body); break;
            case GlobalFunctionStat gf: block(gf.Body.Body); break;
            case ReturnStat r: MapList(r.Values, expr); break;
            case MatchStat ms: VisitExprChildren(ms.Match, block, expr); break;
        }
    }

    private void VisitExprChildren(Expr e, Action<Block> block, Func<Expr, Expr> expr)
    {
        switch (e)
        {
            case IndexExpr i: i.Object = expr(i.Object); i.Key = expr(i.Key); break;
            case CallExpr c: c.Callee = expr(c.Callee); MapList(c.Args, expr); break;
            case MethodCallExpr m: m.Object = expr(m.Object); MapList(m.Args, expr); break;
            case FunctionExpr f: block(f.Body.Body); break;
            case TableExpr t:
                foreach (TableItem item in t.Items)
                {
                    if (item.Key != null) item.Key = expr(item.Key);
                    item.Value = expr(item.Value);
                }
                break;
            case CameoListExpr l:
                MapList(l.Items, expr);
                break;
            case CameoListCompExpr c:
                c.Iterable = expr(c.Iterable);
                if (c.Filter != null) c.Filter = expr(c.Filter);
                c.Body = expr(c.Body);
                break;
            case CameoMatchExpr m:
                m.Scrutinee = expr(m.Scrutinee);
                foreach (MatchArm arm in m.Arms)
                {
                    foreach (MatchPattern p in arm.Patterns)
                        if (p.Literal != null) p.Literal = expr(p.Literal);
                    if (arm.BlockBody != null) block(arm.BlockBody);
                    if (arm.ExprBody != null) arm.ExprBody = expr(arm.ExprBody);
                }
                break;
            case BinaryExpr b: b.Left = expr(b.Left); b.Right = expr(b.Right); break;
            case UnaryExpr u: u.Operand = expr(u.Operand); break;
            case ParenExpr p: p.Inner = expr(p.Inner); break;
            case CastExpr c: c.Inner = expr(c.Inner); break;
            case IsExpr i: i.Inner = expr(i.Inner); break;
        }
    }

    // ------------------------------------------------------------------ expansion

    private void ExpandBlock(Block b)
    {
        var result = new List<Stat>(b.Stats.Count);
        foreach (Stat s in b.Stats)
        {
            if (s is MacroCallStat mc)
            {
                ExpandStatement(mc.Call, result);
                continue;
            }
            if (s is MacroDeclStat md) throw Error(md, "macros can only be declared at the top level of a file");
            VisitStat(s, ExpandBlock, ExpandExpr);
            if (s is CallStat cs && cs.Call is not (CallExpr or MethodCallExpr))
                throw Error(s, "macro expansion produced an expression where a statement is required");
            result.Add(s);
        }
        b.Stats.Clear();
        b.Stats.AddRange(result);
    }

    private Expr ExpandExpr(Expr e)
    {
        if (e is MacroCallExpr mc)
        {
            Value res = Invoke(mc);
            if (BlockInfo.KindOf(res) == "statements")
            {
                Table t = res.AsTable;
                if (t.Length == 1 && !BlockInfo.IsStatementKind(BlockInfo.KindOf(t.GetInt(1)))) res = t.GetInt(1);
                else throw Error(mc, $"macro '{mc.Name}' returned statements in an expression context");
            }
            Expr produced;
            try
            {
                produced = new BlockReader(mc.Line, mc.Col).Expr(res);
            }
            catch (MacroException ex)
            {
                throw Error(mc, $"in expansion of macro '{mc.Name}': {ex.Message}");
            }
            return Nested(mc, () => ExpandExpr(produced));
        }
        VisitExprChildren(e, ExpandBlock, ExpandExpr);
        return e;
    }

    private void ExpandStatement(MacroCallExpr mc, List<Stat> into)
    {
        Value res = Invoke(mc);
        var block = new Block { Line = mc.Line, Col = mc.Col };
        try
        {
            new BlockReader(mc.Line, mc.Col).Stats(res, block.Stats);
        }
        catch (MacroException ex)
        {
            throw Error(mc, $"in expansion of macro '{mc.Name}': {ex.Message}");
        }
        Nested(mc, () => { ExpandBlock(block); return 0; });
        into.AddRange(block.Stats);
    }

    private T Nested<T>(MacroCallExpr mc, Func<T> f)
    {
        if (++_depth > MaxDepth) throw Error(mc, $"macro expansion too deep (macro '{mc.Name}' may be expanding itself endlessly)");
        try
        {
            return f();
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>Runs a macro and returns the block it produced.</summary>
    private Value Invoke(MacroCallExpr mc)
    {
        if (!_macros.TryGetValue(mc.Name, out Macro? macro)) throw Error(mc, $"unknown macro '{mc.Name}'");
        MacroDeclStat decl = macro.Decl;
        int required = decl.ParamIsStatement.Count;
        int provided = mc.Args.Count;
        if (provided < required || (decl.VarargIsStatement == null && provided > required))
            throw Error(mc, $"macro '{mc.Name}' expects {required} argument{(required == 1 ? "" : "s")}, got {provided}");

        var builder = new BlockBuilder(_file, null);
        var args = new Value[provided];
        for (int i = 0; i < provided; i++)
        {
            bool wantStmt = i < required ? decl.ParamIsStatement[i] : decl.VarargIsStatement == true;
            MacroArg a = mc.Args[i];
            try
            {
                if (wantStmt)
                {
                    if (a.Stats == null) throw Error(a.Expr!, $"macro '{mc.Name}' argument {i + 1} must be a Statement");
                    args[i] = Value.FromTable(builder.Statements(a.Stats));
                }
                else
                {
                    if (a.Expr == null) throw Error(mc, $"macro '{mc.Name}' argument {i + 1} must be an Expression");
                    args[i] = builder.Expr(a.Expr);
                }
            }
            catch (MacroException ex)
            {
                throw Error(mc, ex.Message);
            }
        }

        LuaState L = Vm;
        int savedLine = _whereLine, savedCol = _whereCol;
        _whereLine = mc.Line;
        _whereCol = mc.Col;
        _errors.Clear();
        Value[] results;
        try
        {
            results = L.Call(macro.Function, args);
        }
        catch (LuaException e)
        {
            throw Error(mc, _errors.Count > 0 ? _errors[0] : e.Message);
        }
        finally
        {
            _whereLine = savedLine;
            _whereCol = savedCol;
        }
        if (_errors.Count > 0) throw Error(mc, _errors[0]);
        if (results.Length == 0 || !BlockInfo.IsBlock(results[0]))
            throw Error(mc, $"macro '{mc.Name}' did not return a Block");
        return results[0];
    }
}
