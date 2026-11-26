using System.Globalization;
using System.Text;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Macros;

/// <summary>Raised while converting or expanding; reported at the macro invocation.</summary>
internal sealed class MacroException : Exception
{
    public MacroException(string message) : base(message) { }
}

/// <summary>
/// The "block" view of syntax that macro code works with: VM tables shaped like the blocks of
/// the reference Teal implementation (<c>kind</c>, <c>tk</c>, position fields and numbered
/// children, see BLOCK_INDEXES). Parts that macros normally treat as opaque (types, generic
/// parameters) additionally carry the original syntax node in a hidden "__" field.
/// </summary>
internal static class BlockInfo
{
    public static readonly string[] Kinds =
    {
        "nil", "string", "number", "integer", "boolean", "literal_table", "literal_table_item", "function",
        "expression_list", "if", "if_block", "while", "fornum", "forin", "goto", "label", "repeat", "do", "break",
        "return", "newtype", "argument", "type_identifier", "variable", "variable_list", "statements", "assignment",
        "argument_list", "local_function", "global_function", "local_type", "global_type", "record_function",
        "local_declaration", "global_declaration", "identifier", "cast", "...", ":", ";", "comment", "hashbang",
        "paren", "macroexp", "local_macroexp", "local_macro", "macro_quote", "macro_var", "macro_invocation",
        "interface", "pragma", "error_block", "userdata",
        "op_not", "op_len", "op_unm", "op_bnot", "op_or", "op_and", "op_is", "op_lt", "op_gt", "op_le", "op_ge",
        "op_ne", "op_eq", "op_bor", "op_bxor", "op_band", "op_shl", "op_shr", "op_concat", "op_add", "op_sub",
        "op_mul", "op_div", "op_idiv", "op_mod", "op_pow", "op_as", "op_funcall", "op_index", "op_dot", "op_colon",
        "typeargs", "type_list", "generic_type", "typedecl", "tuple_type", "nominal_type", "map_type", "array_type",
        "union_type", "argument_type", "interface_list", "record_body", "record_field", "question",
    };

    public static readonly (string Group, (string Name, int Index)[] Fields)[] Indexes =
    {
        ("PRAGMA", new[] { ("KEY", 1), ("VALUE", 2) }),
        ("IF_BLOCK", new[] { ("COND", 1), ("BODY", 2) }),
        ("WHILE", new[] { ("COND", 1), ("BODY", 2) }),
        ("FORNUM", new[] { ("VAR", 1), ("FROM", 2), ("TO", 3), ("STEP", 4), ("BODY", 5) }),
        ("FORIN", new[] { ("VARS", 1), ("EXPS", 2), ("BODY", 3) }),
        ("REPEAT", new[] { ("BODY", 1), ("COND", 2) }),
        ("DO", new[] { ("BODY", 1) }),
        ("GOTO", new[] { ("LABEL", 1) }),
        ("LABEL", new[] { ("NAME", 1) }),
        ("RETURN", new[] { ("EXPS", 1) }),
        ("FUNCTION", new[] { ("TYPEARGS", 2), ("ARGS", 3), ("RETS", 4), ("BODY", 5) }),
        ("LOCAL_FUNCTION", new[] { ("NAME", 1), ("TYPEARGS", 2), ("ARGS", 3), ("RETS", 4), ("BODY", 5) }),
        ("GLOBAL_FUNCTION", new[] { ("NAME", 1), ("TYPEARGS", 2), ("ARGS", 3), ("RETS", 4), ("BODY", 5) }),
        ("RECORD_FUNCTION", new[] { ("OWNER", 1), ("NAME", 2), ("TYPEARGS", 3), ("ARGS", 4), ("RETS", 5), ("BODY", 6) }),
        ("LOCAL_MACRO", new[] { ("NAME", 1), ("TYPEARGS", 2), ("ARGS", 3), ("RETS", 4), ("BODY", 5) }),
        ("LOCAL_MACROEXP", new[] { ("NAME", 1), ("EXP", 2) }),
        ("LOCAL_DECLARATION", new[] { ("VARS", 1), ("DECL", 2), ("EXPS", 3) }),
        ("GLOBAL_DECLARATION", new[] { ("VARS", 1), ("DECL", 2), ("EXPS", 3) }),
        ("LOCAL_TYPE", new[] { ("VAR", 1), ("VALUE", 2) }),
        ("GLOBAL_TYPE", new[] { ("VAR", 1), ("VALUE", 2) }),
        ("ASSIGNMENT", new[] { ("VARS", 1), ("EXPS", 3) }),
        ("VARIABLE", new[] { ("ANNOTATION", 1) }),
        ("ARGUMENT", new[] { ("TYPE", 1) }),
        ("LITERAL_TABLE_ITEM", new[] { ("KEY", 1), ("VALUE", 2), ("TYPED_VALUE", 3) }),
        ("OP", new[] { ("E1", 1), ("E2", 2) }),
        ("PAREN", new[] { ("EXP", 1) }),
        ("MACRO_QUOTE", new[] { ("BLOCK", 1) }),
        ("MACRO_VAR", new[] { ("NAME", 1) }),
        ("MACRO_INVOCATION", new[] { ("MACRO", 1), ("ARGS", 2) }),
        ("CAST", new[] { ("TYPE", 1) }),
        ("NEWTYPE", new[] { ("TYPEDECL", 1) }),
        ("TYPEDECL", new[] { ("TYPE", 1) }),
        ("FUNCTION_TYPE", new[] { ("ARGS", 1), ("RETS", 2), ("MACROEXP", 4) }),
        ("MACROEXP", new[] { ("ARGS", 1), ("RETS", 2), ("EXP", 3) }),
        ("RECORD", new[] { ("ARRAY_TYPE", 1), ("INTERFACES", 2), ("FIELDS", 3), ("META_FIELDS", 4), ("WHERE_CLAUSE", 5) }),
        ("INTERFACE", new[] { ("ARRAY_TYPE", 1), ("INTERFACES", 2), ("FIELDS", 3) }),
        ("RECORD_BODY", new[] { ("FIELDS", 1), ("META_FIELDS", 2) }),
        ("RECORD_FIELD", new[] { ("NAME", 1), ("TYPE", 2), ("VAL", 3), ("METHOD", 4), ("DEFAULT_VAL", 5) }),
        ("ARGUMENT_TYPE", new[] { ("NAME", 1), ("TYPE", 2), ("VARARG", 3), ("OPTIONAL", 4) }),
        ("TYPEARG", new[] { ("NAME", 1), ("CONSTRAINT", 2) }),
        ("GENERIC_TYPE", new[] { ("TYPEARGS", 1), ("BASE", 2) }),
        ("NOMINAL_TYPE", new[] { ("NAME", 1) }),
        ("ARRAY_TYPE", new[] { ("ELEMENT", 1) }),
        ("MAP_TYPE", new[] { ("KEYS", 1), ("VALUES", 2) }),
    };

    public static readonly (BinaryOp Op, string Kind, string Tk)[] BinaryOps =
    {
        (BinaryOp.Or, "op_or", "or"), (BinaryOp.And, "op_and", "and"),
        (BinaryOp.Lt, "op_lt", "<"), (BinaryOp.Gt, "op_gt", ">"), (BinaryOp.Le, "op_le", "<="), (BinaryOp.Ge, "op_ge", ">="),
        (BinaryOp.Ne, "op_ne", "~="), (BinaryOp.Eq, "op_eq", "=="),
        (BinaryOp.BOr, "op_bor", "|"), (BinaryOp.BXor, "op_bxor", "~"), (BinaryOp.BAnd, "op_band", "&"),
        (BinaryOp.Shl, "op_shl", "<<"), (BinaryOp.Shr, "op_shr", ">>"), (BinaryOp.Concat, "op_concat", ".."),
        (BinaryOp.Add, "op_add", "+"), (BinaryOp.Sub, "op_sub", "-"), (BinaryOp.Mul, "op_mul", "*"),
        (BinaryOp.Div, "op_div", "/"), (BinaryOp.IDiv, "op_idiv", "//"), (BinaryOp.Mod, "op_mod", "%"),
        (BinaryOp.Pow, "op_pow", "^"),
    };

    public static readonly (UnaryOp Op, string Kind, string Tk)[] UnaryOps =
    {
        (UnaryOp.Not, "op_not", "not"), (UnaryOp.Len, "op_len", "#"), (UnaryOp.Neg, "op_unm", "-"), (UnaryOp.BNot, "op_bnot", "~"),
    };

    public static bool IsStatementKind(string k) => k is "assignment" or "local_declaration" or "global_declaration" or "return"
        or "if" or "while" or "fornum" or "forin" or "do" or "repeat" or "local_function" or "global_function"
        or "record_function" or "newtype" or "pragma" or "local_type" or "global_type" or "break" or "goto" or "label"
        or "local_macroexp";

    public static bool IsBlock(in Value v) => v.IsTable && v.AsTable.GetStr("kind").IsString;
    public static string KindOf(in Value v) => IsBlock(v) ? v.AsTable.GetStr("kind").AsString : v.TypeName;

    /// <summary>Lua source form of a string value (as it would appear as a token).</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32 || c >= 127) sb.Append('\\').Append(((int)c).ToString("000", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Value of a string token text (quoted or long-bracket form); raw text if it is not a literal.</summary>
    public static string Unquote(string tk)
    {
        try
        {
            List<Token> toks = Lexer.Tokenize(tk, "@macro");
            if (toks.Count == 2 && toks[0].Kind == TokenKind.String) return toks[0].Text;
        }
        catch (CameoSyntaxException)
        {
        }
        return tk;
    }
}

/// <summary>Syntax tree -> blocks. With <c>vars</c> set it instantiates a quote template,
/// replacing each <c>$name</c> with the block the macro supplied.</summary>
internal sealed class BlockBuilder
{
    private readonly string _file;
    private readonly Table? _vars;

    public BlockBuilder(string file, Table? vars)
    {
        _file = file;
        _vars = vars;
    }

    public Table New(string kind, Node? at, string tk = "")
    {
        var t = new Table(0, 8);
        t.SetStr("kind", kind);
        t.SetStr("tk", tk);
        t.SetStr("f", _file);
        int y = at?.Line ?? 1, x = at?.Col ?? 1;
        t.SetStr("y", y);
        t.SetStr("x", x);
        t.SetStr("yend", y);
        t.SetStr("xend", x);
        return t;
    }

    private Value Var(string name, Node at)
    {
        Value v = _vars == null ? Value.Nil : _vars.GetStr(name);
        if (!BlockInfo.IsBlock(v))
            throw new MacroException($"macro variable '${name}' is not a block (got {BlockInfo.KindOf(v)})");
        return v;
    }

    private Table Ident(string name, Node? at) => New("identifier", at, name);

    // ------------------------------------------------------------------ expressions

    public Value Expr(Expr e)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        switch (e)
        {
            case NilExpr: return New("nil", e, "nil");
            case BoolExpr b: return New("boolean", e, b.Value ? "true" : "false");
            case NumberExpr n:
                if (n.IsInteger) return New("integer", e, n.IntValue.ToString(CultureInfo.InvariantCulture));
                {
                    string s = n.FloatValue.ToString("R", CultureInfo.InvariantCulture);
                    if (double.IsInfinity(n.FloatValue)) s = n.FloatValue > 0 ? "1e999" : "-1e999";
                    else if (double.IsNaN(n.FloatValue)) s = "(0/0)";
                    else if (s.IndexOfAny(new[] { '.', 'E', 'e' }) < 0) s += ".0";
                    return New("number", e, s);
                }
            case StringExpr s: return New("string", e, BlockInfo.Quote(s.Value));
            case VarargExpr: return New("...", e, "...");
            case NameExpr n: return Ident(n.Name, e);
            case MacroVarExpr mv: return Var(mv.Name, e);
            case ParenExpr p:
                {
                    Table t = New("paren", e, "(");
                    t.SetInt(1, Expr(p.Inner));
                    return t;
                }
            case IndexExpr ix:
                {
                    Table t = New(ix.IsDot ? "op_dot" : "op_index", e, ix.IsDot ? "." : "[");
                    t.SetInt(1, Expr(ix.Object));
                    if (ix.IsDot)
                    {
                        if (ix.Key is MacroVarExpr kv)
                        {
                            Value k = Var(kv.Name, ix.Key);
                            if (BlockInfo.KindOf(k) != "identifier") throw new MacroException($"macro variable '${kv.Name}' used as a field name must be an identifier");
                            t.SetInt(2, k);
                        }
                        else t.SetInt(2, Ident(((StringExpr)ix.Key).Value, ix.Key));
                    }
                    else t.SetInt(2, Expr(ix.Key));
                    return t;
                }
            case CallExpr c:
                {
                    Table t = New("op_funcall", e, "(");
                    t.Set("cameo_dot_call", Value.FromBool(c.CameoDotCall));
                    t.SetInt(1, Expr(c.Callee));
                    t.SetInt(2, ExprList(c.Args, e));
                    return t;
                }
            case MethodCallExpr m:
                {
                    Table colon = New("op_colon", e, ":");
                    colon.SetInt(1, Expr(m.Object));
                    colon.SetInt(2, Ident(m.Name, e));
                    Table t = New("op_funcall", e, "(");
                    t.SetInt(1, colon);
                    t.SetInt(2, ExprList(m.Args, e));
                    return t;
                }
            case FunctionExpr f:
                {
                    Table t = New("function", e, "function");
                    FillFunction(t, f.Body, 2);
                    return t;
                }
            case TableExpr tb:
                {
                    Table t = New("literal_table", e, "{");
                    int pos = 1;
                    foreach (TableItem item in tb.Items)
                    {
                        Table it = New("literal_table_item", item);
                        switch (item.Kind)
                        {
                            case TableItemKind.Positional:
                                it.SetInt(1, New("integer", item, pos.ToString(CultureInfo.InvariantCulture)));
                                it.SetInt(2, Expr(item.Value));
                                it.SetStr("__positional", Value.True);
                                pos++;
                                break;
                            case TableItemKind.Named:
                                it.SetInt(1, New("string", item, BlockInfo.Quote(((StringExpr)item.Key!).Value)));
                                it.SetStr("__named", Value.True);
                                if (item.DeclaredType != null)
                                {
                                    it.SetInt(2, Type(item.DeclaredType));
                                    it.SetInt(3, Expr(item.Value));
                                }
                                else it.SetInt(2, Expr(item.Value));
                                break;
                            default:
                                it.SetInt(1, Expr(item.Key!));
                                it.SetInt(2, Expr(item.Value));
                                break;
                        }
                        t.Append(it);
                    }
                    return t;
                }
            case BinaryExpr b:
                foreach (var (op, kind, tk) in BlockInfo.BinaryOps)
                {
                    if (op != b.Op) continue;
                    Table t = New(kind, e, tk);
                    t.SetInt(1, Expr(b.Left));
                    t.SetInt(2, Expr(b.Right));
                    return t;
                }
                break;
            case UnaryExpr u:
                foreach (var (op, kind, tk) in BlockInfo.UnaryOps)
                {
                    if (op != u.Op) continue;
                    Table t = New(kind, e, tk);
                    t.SetInt(1, Expr(u.Operand));
                    return t;
                }
                break;
            case CastExpr c:
                {
                    Table t = New("op_as", e, "as");
                    t.SetInt(1, Expr(c.Inner));
                    if (c.IsTuple)
                    {
                        Table tl = New("type_list", e);
                        foreach (TypeNode ty in c.Types) tl.Append(Type(ty));
                        tl.SetStr("__tuple", Value.True);
                        t.SetInt(2, tl);
                    }
                    else t.SetInt(2, Type(c.Types[0]));
                    return t;
                }
            case IsExpr i:
                {
                    Table t = New("op_is", e, "is");
                    t.SetInt(1, Expr(i.Inner));
                    t.SetInt(2, Type(i.Type));
                    return t;
                }
            case MacroCallExpr mc: return MacroCall(mc);
        }
        throw new MacroException("cannot represent " + e.GetType().Name + " as a block");
    }

    private Table MacroCall(MacroCallExpr mc)
    {
        Table t = New("macro_invocation", mc, "!");
        t.SetInt(1, Ident(mc.Name, mc));
        Table args = New("expression_list", mc);
        foreach (MacroArg a in mc.Args) args.Append(a.Stats != null ? Value.FromTable(Statements(a.Stats)) : Expr(a.Expr!));
        t.SetInt(2, args);
        return t;
    }

    private Table ExprList(List<Expr> list, Node at)
    {
        Table t = New("expression_list", list.Count > 0 ? list[0] : at);
        foreach (Expr e in list) t.Append(Expr(e));
        return t;
    }

    // ------------------------------------------------------------------ functions

    // typeargs at index `first`, then args, rets, body
    private void FillFunction(Table t, FunctionBody fb, int first)
    {
        if (fb.TypeParams != null) t.SetStr("__typeparams", Value.FromUserdata(fb.TypeParams));
        Table args = New("argument_list", fb);
        foreach (Param p in fb.Params)
        {
            if (p.IsImplicitSelf) continue;
            Table a = New("argument", p, p.Name);
            if (p.Type != null) a.Append(Type(p.Type));
            if (p.Optional) a.Append(New("question", p, "?"));
            args.Append(a);
        }
        if (fb.IsVarArg)
        {
            Table a = New("argument", fb, "...");
            if (fb.VarArgType != null) a.Append(Type(fb.VarArgType));
            args.Append(a);
        }
        t.SetInt(first + 1, args);
        Table rets = New("type_list", fb);
        foreach (TypeNode r in fb.Returns) rets.Append(Type(r));
        if (fb.ReturnsVarArg) rets.SetStr("__vararg", Value.True);
        if (fb.HasReturnAnnotation) rets.SetStr("__annotated", Value.True);
        t.SetInt(first + 2, rets);
        t.SetInt(first + 3, Statements(fb.Body));
    }

    // ------------------------------------------------------------------ statements

    public Table Statements(Block b)
    {
        Table t = New("statements", b);
        foreach (Stat s in b.Stats) Stat(s, t);
        return t;
    }

    private Value VarName(AttribName n)
    {
        if (n.MacroVar != null)
        {
            Value v = Var(n.MacroVar, n);
            if (BlockInfo.KindOf(v) != "identifier") throw new MacroException($"macro variable '${n.MacroVar}' used as a name must be an identifier");
            // Macro arguments may be reused by other splices. Copy the identifier before
            // attaching declaration attributes so a let cannot mutate the caller's block.
            var copied = new Table();
            foreach (var entry in v.AsTable.Pairs()) copied.Set(entry.Key, entry.Value);
            if (n.IsCameoLet) copied.Set("cameo_let", Value.FromBool(true));
            if (n.Attrib != null) copied.SetInt(1, Ident(n.Attrib, n));
            return copied;
        }
        Table id = Ident(n.Name, n);
        id.Set("cameo_let", Value.FromBool(n.IsCameoLet));
        if (n.Attrib != null) id.Append(Ident(n.Attrib, n));
        return id;
    }

    private void Declaration(Table t, List<AttribName> names, List<TypeNode>? types, List<Expr> values, Node at)
    {
        Table vars = New("variable_list", at);
        foreach (AttribName n in names) vars.Append(VarName(n));
        t.SetInt(1, vars);
        if (types != null)
        {
            Table decl = New("type_list", at);
            foreach (TypeNode ty in types) decl.Append(Type(ty));
            t.SetInt(2, decl);
        }
        if (values.Count > 0) t.SetInt(3, ExprList(values, at));
    }

    private void Stat(Stat s, Table into)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        switch (s)
        {
            case MacroVarStat mv:
                into.Append(Var(mv.Name, s));
                return;
            case LocalStat l:
                {
                    Table t = New("local_declaration", s, "local");
                    Declaration(t, l.Names, l.Types, l.Values, s);
                    into.Append(t);
                    return;
                }
            case GlobalStat g:
                {
                    Table t = New("global_declaration", s, "global");
                    Declaration(t, g.Names, g.Types, g.Values, s);
                    into.Append(t);
                    return;
                }
            case AssignStat a:
                {
                    Table t = New("assignment", s);
                    Table vars = New("variable_list", s);
                    foreach (Expr e in a.Targets) vars.Append(Expr(e));
                    t.SetInt(1, vars);
                    t.SetInt(3, ExprList(a.Values, s));
                    into.Append(t);
                    return;
                }
            case CallStat c:
                into.Append(Expr(c.Call));
                return;
            case MacroCallStat mc:
                into.Append(MacroCall(mc.Call));
                return;
            case DoStat d:
                {
                    Table t = New("do", s, "do");
                    t.SetInt(1, Statements(d.Body));
                    into.Append(t);
                    return;
                }
            case WhileStat w:
                {
                    Table t = New("while", s, "while");
                    t.SetInt(1, Expr(w.Cond));
                    t.SetInt(2, Statements(w.Body));
                    into.Append(t);
                    return;
                }
            case RepeatStat r:
                {
                    Table t = New("repeat", s, "repeat");
                    t.SetInt(1, Statements(r.Body));
                    t.SetInt(2, Expr(r.Cond));
                    into.Append(t);
                    return;
                }
            case IfStat i:
                {
                    Table t = New("if", s, "if");
                    foreach (var (cond, body) in i.Clauses)
                    {
                        Table ib = New("if_block", cond);
                        ib.SetInt(1, Expr(cond));
                        ib.SetInt(2, Statements(body));
                        t.Append(ib);
                    }
                    if (i.Else != null)
                    {
                        Table ib = New("if_block", i.Else);
                        ib.SetInt(2, Statements(i.Else));
                        t.Append(ib);
                    }
                    into.Append(t);
                    return;
                }
            case ForNumStat f:
                {
                    Table t = New("fornum", s, "for");
                    t.SetInt(1, Ident(f.Var.Name, f.Var));
                    t.SetInt(2, Expr(f.Start));
                    t.SetInt(3, Expr(f.Limit));
                    if (f.Step != null) t.SetInt(4, Expr(f.Step));
                    t.SetInt(5, Statements(f.Body));
                    into.Append(t);
                    return;
                }
            case ForInStat f:
                {
                    Table t = New("forin", s, "for");
                    Table vars = New("variable_list", s);
                    foreach (AttribName n in f.Names) vars.Append(Ident(n.Name, n));
                    t.SetInt(1, vars);
                    t.SetInt(2, ExprList(f.Exprs, s));
                    t.SetInt(3, Statements(f.Body));
                    into.Append(t);
                    return;
                }
            case FunctionStat f:
                {
                    Table t = New("record_function", s, f.IsMethod ? ":" : "function");
                    // name pieces written as $var in a quote are replaced by the identifier block bound to var
                    Value Piece(string name)
                    {
                        if (!name.StartsWith('$')) return Ident(name, s);
                        Value v = Var(name.Substring(1), s);
                        if (BlockInfo.KindOf(v) != "identifier") throw new MacroException($"macro variable '{name}' used as a name must be an identifier");
                        return v;
                    }
                    Value owner = f.Root.Name.StartsWith('$') ? Piece(f.Root.Name) : Expr(f.Root);
                    for (int k = 0; k < f.Path.Count - 1; k++)
                    {
                        Table dot = New("op_dot", s, ".");
                        dot.SetInt(1, owner);
                        dot.SetInt(2, Piece(f.Path[k]));
                        owner = dot;
                    }
                    t.SetInt(1, owner);
                    if (f.Path.Count > 0) t.SetInt(2, Piece(f.Path[^1]));
                    FillFunction(t, f.Body, 3);
                    into.Append(t);
                    return;
                }
            case LocalFunctionStat lf:
                {
                    Table t = New("local_function", s, "local");
                    t.SetInt(1, VarName(lf.Name));
                    FillFunction(t, lf.Body, 2);
                    into.Append(t);
                    return;
                }
            case GlobalFunctionStat gf:
                {
                    Table t = New("global_function", s, "global");
                    t.SetInt(1, Ident(gf.Name, s));
                    FillFunction(t, gf.Body, 2);
                    into.Append(t);
                    return;
                }
            case ReturnStat r:
                {
                    Table t = New("return", s, "return");
                    t.SetInt(1, ExprList(r.Values, s));
                    into.Append(t);
                    return;
                }
            case BreakStat:
                {
                    Table t = New("break", s, "break");
                    if (((BreakStat)s).Label is { } label) t.SetInt(1, Ident(label, s));
                    into.Append(t);
                    return;
                }
            case ContinueStat:
                {
                    Table t = New("continue", s, "continue");
                    if (((ContinueStat)s).Label is { } label) t.SetInt(1, Ident(label, s));
                    into.Append(t);
                    return;
                }
            case GotoStat g:
                {
                    Table t = New("goto", s, "goto");
                    t.SetInt(1, Ident(g.Label, s));
                    into.Append(t);
                    return;
                }
            case LabelStat l:
                {
                    Table t = New("label", s, "::");
                    t.SetInt(1, Ident(l.Name, s));
                    into.Append(t);
                    return;
                }
            case TypeDeclStat td:
                {
                    Table t = New(td.IsGlobal ? "global_type" : "local_type", s, td.IsGlobal ? "global" : "local");
                    t.SetInt(1, VarName(td.Name));
                    if (td.Def != null)
                    {
                        Table typedecl = New("typedecl", td.Def);
                        typedecl.SetInt(1, Type(td.Def));
                        Table newtype = New("newtype", td.Def);
                        newtype.SetInt(1, typedecl);
                        t.SetInt(2, newtype);
                    }
                    if (td.TypeParams != null) t.SetStr("__typeparams", Value.FromUserdata(td.TypeParams));
                    into.Append(t);
                    return;
                }
            case MacroExpStat me:
                {
                    Table t = New("local_macroexp", s, "local");
                    t.SetStr("__stat", Value.FromUserdata(me));
                    into.Append(t);
                    return;
                }
            case MacroDeclStat:
                throw new MacroException("macros can only be declared at the top level of a file");
        }
        throw new MacroException("cannot represent " + s.GetType().Name + " as a block");
    }

    // ------------------------------------------------------------------ types

    private static bool HasMacroVars(TypeNode t)
    {
        switch (t)
        {
            case MacroVarTypeNode: return true;
            case NameTypeNode n:
                foreach (string s in n.Names) if (s.StartsWith('$')) return true;
                if (n.TypeArgs != null) foreach (TypeNode a in n.TypeArgs) if (HasMacroVars(a)) return true;
                return false;
            case ArrayTypeNode a: return HasMacroVars(a.Element);
            case MapTypeNode m: return HasMacroVars(m.Key) || HasMacroVars(m.Value);
            case TupleTypeNode tu: return tu.Elements.Exists(HasMacroVars);
            case UnionTypeNode u: return u.Members.Exists(HasMacroVars);
            case FunctionTypeNode f:
                return f.Params.Exists(p => HasMacroVars(p.Type)) || (f.VarArg != null && HasMacroVars(f.VarArg)) || f.Returns.Exists(HasMacroVars);
            default: return false;
        }
    }

    /// <summary>The type with every macro variable replaced by the type the macro supplied.</summary>
    private TypeNode Subst(TypeNode t)
    {
        if (!HasMacroVars(t)) return t;
        switch (t)
        {
            case MacroVarTypeNode mv:
                return BlockReader.Type(Var(mv.Name, t), t.Line, t.Col);
            case NameTypeNode n:
                {
                    var r = new NameTypeNode { Line = n.Line, Col = n.Col };
                    foreach (string s in n.Names)
                    {
                        if (!s.StartsWith('$')) { r.Names.Add(s); continue; }
                        Value v = Var(s.Substring(1), t);
                        TypeNode part = BlockReader.Type(v, t.Line, t.Col);
                        if (part is not NameTypeNode pn) throw new MacroException($"macro variable '{s}' must be a name here");
                        r.Names.AddRange(pn.Names);
                    }
                    if (n.TypeArgs != null) r.TypeArgs = n.TypeArgs.ConvertAll(Subst);
                    return r;
                }
            case ArrayTypeNode a: return new ArrayTypeNode { Line = t.Line, Col = t.Col, Element = Subst(a.Element) };
            case MapTypeNode m: return new MapTypeNode { Line = t.Line, Col = t.Col, Key = Subst(m.Key), Value = Subst(m.Value) };
            case TupleTypeNode tu: return new TupleTypeNode { Line = t.Line, Col = t.Col, Elements = tu.Elements.ConvertAll(Subst) };
            case UnionTypeNode u: return new UnionTypeNode { Line = t.Line, Col = t.Col, Members = u.Members.ConvertAll(Subst) };
            case FunctionTypeNode f:
                return new FunctionTypeNode
                {
                    Line = t.Line, Col = t.Col, TypeParams = f.TypeParams, IsBare = f.IsBare, ReturnsVarArg = f.ReturnsVarArg,
                    Params = f.Params.ConvertAll(p => new ParamTypeNode { Name = p.Name, Optional = p.Optional, Type = Subst(p.Type) }),
                    VarArg = f.VarArg == null ? null : Subst(f.VarArg),
                    Returns = f.Returns.ConvertAll(Subst),
                };
            default: return t;
        }
    }

    public Value Type(TypeNode type)
    {
        if (type is MacroVarTypeNode mv) return Var(mv.Name, type);
        TypeNode t = Subst(type);
        Table b;
        switch (t)
        {
            case NameTypeNode n:
                b = New("nominal_type", t, n.FullName);
                if (n.Names.Count == 1) b.SetInt(1, Ident(n.Names[0], t));
                break;
            case ArrayTypeNode: b = New("array_type", t, "{"); break;
            case MapTypeNode: b = New("map_type", t, "{"); break;
            case TupleTypeNode: b = New("tuple_type", t, "{"); break;
            case UnionTypeNode: b = New("union_type", t, "|"); break;
            case RecordTypeNode r: b = New(r.IsInterface ? "interface" : "record_body", t); break;
            default: b = New("typedecl", t); break;
        }
        b.SetStr("__type", Value.FromUserdata(t));
        return b;
    }
}

/// <summary>Blocks -> syntax tree. Every node gets the position of the macro invocation.</summary>
internal sealed class BlockReader
{
    private readonly int _line, _col;

    public BlockReader(int line, int col)
    {
        _line = line;
        _col = col;
    }

    private T At<T>(T n) where T : Node
    {
        n.Line = _line;
        n.Col = _col;
        return n;
    }

    private static Table Tbl(in Value v, string what)
    {
        if (!BlockInfo.IsBlock(v)) throw new MacroException($"expected a block for {what}, got {BlockInfo.KindOf(v)}");
        return v.AsTable;
    }

    private static string Kind(Table t) => t.GetStr("kind").AsString;
    private static string Tk(Table t)
    {
        Value v = t.GetStr("tk");
        return v.IsString ? v.AsString : v.IsNil ? "" : v.ToString();
    }

    private static string Name(in Value v, string what)
    {
        Table t = Tbl(v, what);
        string k = Kind(t);
        if (k != "identifier" && k != "argument") throw new MacroException($"expected an identifier for {what}, got {k}");
        string tk = Tk(t);
        if (tk.Length == 0) throw new MacroException($"identifier block for {what} has no name (set its 'tk' field)");
        return tk;
    }

    private static List<Value> Children(Table t)
    {
        var list = new List<Value>();
        int n = t.Length;
        for (int i = 1; i <= n; i++) list.Add(t.GetInt(i));
        return list;
    }

    // ------------------------------------------------------------------ expressions

    public Expr Expr(in Value v)
    {
        Table t = Tbl(v, "an expression");
        string kind = Kind(t);
        string tk = Tk(t);
        switch (kind)
        {
            case "nil": return At(new NilExpr());
            case "boolean": return At(new BoolExpr { Value = tk == "true" });
            case "integer":
            case "number":
                {
                    if (!LuaNumber.TryParse(tk, out Value num)) throw new MacroException($"malformed number '{tk}' in {kind} block");
                    var n = At(new NumberExpr());
                    if (num.IsInteger) { n.IsInteger = true; n.IntValue = num.I; }
                    else n.FloatValue = num.N;
                    return n;
                }
            case "string": return At(new StringExpr { Value = BlockInfo.Unquote(tk) });
            case "...": return At(new VarargExpr());
            case "identifier":
                if (tk.Length == 0) throw new MacroException("identifier block has no name (set its 'tk' field)");
                return At(new NameExpr { Name = tk });
            case "paren": return At(new ParenExpr { Inner = Expr(t.GetInt(1)) });
            case "op_dot":
                return At(new IndexExpr { Object = Expr(t.GetInt(1)), Key = At(new StringExpr { Value = Name(t.GetInt(2), "a field name") }), IsDot = true });
            case "op_index":
                return At(new IndexExpr { Object = Expr(t.GetInt(1)), Key = Expr(t.GetInt(2)) });
            case "op_funcall":
                {
                    Value callee = t.GetInt(1);
                    List<Expr> args = ExprList(t.GetInt(2));
                    if (BlockInfo.KindOf(callee) == "op_colon")
                    {
                        Table c = callee.AsTable;
                        return At(new MethodCallExpr { Object = Expr(c.GetInt(1)), Name = Name(c.GetInt(2), "a method name"), Args = args });
                    }
                    return At(new CallExpr { Callee = Expr(callee), Args = args, CameoDotCall = t.Get("cameo_dot_call").IsTruthy });
                }
            case "op_colon": throw new MacroException("a method reference (obj:name) must be called");
            case "function": return At(new FunctionExpr { Body = Function(t, 2, "anonymous", false) });
            case "literal_table":
                {
                    var tb = At(new TableExpr());
                    foreach (Value iv in Children(t))
                    {
                        Table it = Tbl(iv, "a table item");
                        if (Kind(it) != "literal_table_item") throw new MacroException("expected literal_table_item, got " + Kind(it));
                        var item = At(new TableItem());
                        Value key = it.GetInt(1), val = it.GetInt(2), typed = it.GetInt(3);
                        if (!typed.IsNil)
                        {
                            item.Kind = TableItemKind.Named;
                            item.Key = At(new StringExpr { Value = BlockInfo.Unquote(Tk(Tbl(key, "a table key"))) });
                            item.DeclaredType = Type(val, _line, _col);
                            item.Value = Expr(typed);
                        }
                        else if (it.GetStr("__positional").IsTruthy || key.IsNil)
                        {
                            item.Kind = TableItemKind.Positional;
                            item.Value = Expr(val);
                        }
                        else if (it.GetStr("__named").IsTruthy && BlockInfo.KindOf(key) == "string")
                        {
                            item.Kind = TableItemKind.Named;
                            item.Key = At(new StringExpr { Value = BlockInfo.Unquote(Tk(key.AsTable)) });
                            item.Value = Expr(val);
                        }
                        else
                        {
                            item.Kind = TableItemKind.Keyed;
                            item.Key = Expr(key);
                            item.Value = Expr(val);
                        }
                        tb.Items.Add(item);
                    }
                    return tb;
                }
            case "op_as":
                {
                    var c = At(new CastExpr { Inner = Expr(t.GetInt(1)) });
                    Value ty = t.GetInt(2);
                    if (BlockInfo.KindOf(ty) == "type_list")
                    {
                        foreach (Value e in Children(ty.AsTable)) c.Types.Add(Type(e, _line, _col));
                        c.IsTuple = ty.AsTable.GetStr("__tuple").IsTruthy || c.Types.Count != 1;
                    }
                    else c.Types.Add(Type(ty, _line, _col));
                    return c;
                }
            case "op_is": return At(new IsExpr { Inner = Expr(t.GetInt(1)), Type = Type(t.GetInt(2), _line, _col) });
            case "macro_invocation": return MacroCall(t);
            case "statements":
                {
                    // a statements block holding a single expression
                    List<Value> ch = Children(t);
                    if (ch.Count == 1 && !BlockInfo.IsStatementKind(BlockInfo.KindOf(ch[0]))) return Expr(ch[0]);
                    throw new MacroException("statements cannot be used as an expression");
                }
        }
        foreach (var (op, k, _) in BlockInfo.BinaryOps)
            if (k == kind) return At(new BinaryExpr { Op = op, Left = Expr(t.GetInt(1)), Right = Expr(t.GetInt(2)) });
        foreach (var (op, k, _) in BlockInfo.UnaryOps)
            if (k == kind) return At(new UnaryExpr { Op = op, Operand = Expr(t.GetInt(1)) });
        throw new MacroException($"a block of kind '{kind}' cannot be used as an expression");
    }

    private MacroCallExpr MacroCall(Table t)
    {
        var mc = At(new MacroCallExpr { Name = Name(t.GetInt(1), "a macro name") });
        Value args = t.GetInt(2);
        if (!args.IsNil)
        {
            foreach (Value a in Children(Tbl(args, "macro arguments")))
            {
                if (BlockInfo.KindOf(a) == "statements") mc.Args.Add(new MacroArg { Stats = Block(a) });
                else mc.Args.Add(new MacroArg { Expr = Expr(a) });
            }
        }
        return mc;
    }

    private List<Expr> ExprList(in Value v)
    {
        var list = new List<Expr>();
        if (v.IsNil) return list;
        foreach (Value e in Children(Tbl(v, "an expression list"))) list.Add(Expr(e));
        return list;
    }

    // ------------------------------------------------------------------ functions

    private FunctionBody Function(Table t, int first, string name, bool isMethod)
    {
        var fb = At(new FunctionBody { Name = name });
        if (t.GetStr("__typeparams").O is List<TypeParam> tps) fb.TypeParams = tps;
        if (isMethod) fb.Params.Add(At(new Param { Name = "self", IsImplicitSelf = true }));
        Value args = t.GetInt(first + 1);
        if (!args.IsNil)
        {
            foreach (Value av in Children(Tbl(args, "an argument list")))
            {
                Table a = Tbl(av, "an argument");
                string an = Tk(a);
                TypeNode? type = null;
                bool optional = false;
                foreach (Value c in Children(a))
                {
                    if (BlockInfo.KindOf(c) == "question") optional = true;
                    else type ??= Type(c, _line, _col);
                }
                if (an == "...")
                {
                    fb.IsVarArg = true;
                    fb.VarArgType = type;
                }
                else
                {
                    if (an.Length == 0) throw new MacroException("argument block has no name (set its 'tk' field)");
                    fb.Params.Add(At(new Param { Name = an, Type = type, Optional = optional }));
                }
            }
        }
        Value rets = t.GetInt(first + 2);
        if (!rets.IsNil)
        {
            Table rt = Tbl(rets, "a return type list");
            foreach (Value r in Children(rt)) fb.Returns.Add(Type(r, _line, _col));
            fb.ReturnsVarArg = rt.GetStr("__vararg").IsTruthy;
            fb.HasReturnAnnotation = rt.GetStr("__annotated").IsTruthy || fb.Returns.Count > 0;
        }
        Value body = t.GetInt(first + 3);
        fb.Body = body.IsNil ? At(new Syntax.Block()) : Block(body);
        fb.EndLine = _line;
        return fb;
    }

    // ------------------------------------------------------------------ statements

    public Syntax.Block Block(in Value v)
    {
        var b = At(new Syntax.Block());
        Stats(v, b.Stats);
        return b;
    }

    private AttribName VarName(in Value v)
    {
        Table t = Tbl(v, "a variable name");
        var an = At(new AttribName { Name = Name(v, "a variable name"), IsCameoLet = v.AsTable.Get("cameo_let").IsTruthy });
        Value attr = t.GetInt(1);
        if (!attr.IsNil)
        {
            an.Attrib = Name(attr, "a variable attribute");
            if (an.Attrib is not ("const" or "close" or "total")) throw new MacroException($"unknown variable attribute '{an.Attrib}'");
        }
        return an;
    }

    private void Declaration(Table t, List<AttribName> names, out List<TypeNode>? types, List<Expr> values)
    {
        foreach (Value n in Children(Tbl(t.GetInt(1), "a variable list"))) names.Add(VarName(n));
        if (names.Count == 0) throw new MacroException("declaration without variables");
        types = null;
        Value decl = t.GetInt(2);
        if (!decl.IsNil)
        {
            List<Value> ch = Children(Tbl(decl, "a type list"));
            if (ch.Count > 0)
            {
                types = new List<TypeNode>();
                foreach (Value ty in ch) types.Add(Type(ty, _line, _col));
            }
        }
        values.AddRange(ExprList(t.GetInt(3)));
    }

    /// <summary>Appends the statement(s) a block stands for.</summary>
    public void Stats(in Value v, List<Stat> into)
    {
        Table t = Tbl(v, "a statement");
        string kind = Kind(t);
        switch (kind)
        {
            case "statements":
                foreach (Value c in Children(t)) Stats(c, into);
                return;
            case ";":
            case "comment":
            case "hashbang":
            case "pragma":
                return;
            case "local_declaration":
                {
                    var l = At(new LocalStat());
                    Declaration(t, l.Names, out l.Types, l.Values);
                    into.Add(l);
                    return;
                }
            case "global_declaration":
                {
                    var g = At(new GlobalStat());
                    Declaration(t, g.Names, out g.Types, g.Values);
                    into.Add(g);
                    return;
                }
            case "assignment":
                {
                    var a = At(new AssignStat());
                    foreach (Value tv in Children(Tbl(t.GetInt(1), "assignment targets")))
                    {
                        Expr target = Expr(tv);
                        if (target is not (NameExpr or IndexExpr or MacroCallExpr)) throw new MacroException("cannot assign to this expression");
                        a.Targets.Add(target);
                    }
                    a.Values = ExprList(t.GetInt(3));
                    if (a.Targets.Count == 0 || a.Values.Count == 0) throw new MacroException("incomplete assignment block");
                    into.Add(a);
                    return;
                }
            case "op_funcall":
                into.Add(At(new CallStat { Call = Expr(v) }));
                return;
            case "macro_invocation":
                into.Add(At(new MacroCallStat { Call = MacroCall(t) }));
                return;
            case "do":
                into.Add(At(new DoStat { Body = Block(t.GetInt(1)) }));
                return;
            case "while":
                into.Add(At(new WhileStat { Cond = Expr(t.GetInt(1)), Body = Block(t.GetInt(2)) }));
                return;
            case "repeat":
                into.Add(At(new RepeatStat { Body = Block(t.GetInt(1)), Cond = Expr(t.GetInt(2)) }));
                return;
            case "if":
                {
                    var s = At(new IfStat());
                    foreach (Value bv in Children(t))
                    {
                        Table ib = Tbl(bv, "an if block");
                        Value cond = ib.GetInt(1);
                        if (cond.IsNil)
                        {
                            if (s.Else != null) throw new MacroException("if block with more than one else");
                            s.Else = Block(ib.GetInt(2));
                        }
                        else
                        {
                            if (s.Else != null) throw new MacroException("if block with a condition after else");
                            s.Clauses.Add((Expr(cond), Block(ib.GetInt(2))));
                        }
                    }
                    if (s.Clauses.Count == 0) throw new MacroException("if block without a condition");
                    into.Add(s);
                    return;
                }
            case "fornum":
                {
                    var f = At(new ForNumStat { Var = At(new AttribName { Name = Name(t.GetInt(1), "a loop variable") }) });
                    f.Start = Expr(t.GetInt(2));
                    f.Limit = Expr(t.GetInt(3));
                    if (!t.GetInt(4).IsNil) f.Step = Expr(t.GetInt(4));
                    f.Body = Block(t.GetInt(5));
                    into.Add(f);
                    return;
                }
            case "forin":
                {
                    var f = At(new ForInStat());
                    foreach (Value n in Children(Tbl(t.GetInt(1), "loop variables")))
                        f.Names.Add(At(new AttribName { Name = Name(n, "a loop variable") }));
                    f.Exprs = ExprList(t.GetInt(2));
                    f.Body = Block(t.GetInt(3));
                    if (f.Names.Count == 0 || f.Exprs.Count == 0) throw new MacroException("incomplete forin block");
                    into.Add(f);
                    return;
                }
            case "record_function":
                {
                    // flatten the owner chain a.b.c
                    var path = new List<string>();
                    Value owner = t.GetInt(1);
                    while (BlockInfo.KindOf(owner) == "op_dot")
                    {
                        path.Insert(0, Name(owner.AsTable.GetInt(2), "a function name"));
                        owner = owner.AsTable.GetInt(1);
                    }
                    string root = Name(owner, "a function name");
                    if (!t.GetInt(2).IsNil) path.Add(Name(t.GetInt(2), "a function name"));
                    bool isMethod = Tk(t) == ":";
                    var f = At(new FunctionStat { Root = At(new NameExpr { Name = root }), Path = path, IsMethod = isMethod });
                    string full = root + (path.Count > 0 ? "." + string.Join(".", path) : "");
                    f.Body = Function(t, 3, full, isMethod);
                    into.Add(f);
                    return;
                }
            case "local_function":
                {
                    AttribName n = VarName(t.GetInt(1));
                    into.Add(At(new LocalFunctionStat { Name = n, Body = Function(t, 2, n.Name, false) }));
                    return;
                }
            case "global_function":
                {
                    string n = Name(t.GetInt(1), "a function name");
                    into.Add(At(new GlobalFunctionStat { Name = n, Body = Function(t, 2, n, false) }));
                    return;
                }
            case "return":
                into.Add(At(new ReturnStat { Values = ExprList(t.GetInt(1)) }));
                return;
            case "break":
                into.Add(At(new BreakStat { Label = t.GetInt(1).IsNil ? null : Name(t.GetInt(1), "a label") }));
                return;
            case "continue":
                into.Add(At(new ContinueStat { Label = t.GetInt(1).IsNil ? null : Name(t.GetInt(1), "a label") }));
                return;
            case "goto":
                into.Add(At(new GotoStat { Label = Name(t.GetInt(1), "a label") }));
                return;
            case "label":
                into.Add(At(new LabelStat { Name = Name(t.GetInt(1), "a label") }));
                return;
            case "local_type":
            case "global_type":
                {
                    var td = At(new TypeDeclStat { IsGlobal = kind == "global_type", Kind = TypeDeclKind.Alias });
                    td.Name = At(new AttribName { Name = Name(t.GetInt(1), "a type name") });
                    if (t.GetStr("__typeparams").O is List<TypeParam> tps) td.TypeParams = tps;
                    Value val = t.GetInt(2);
                    if (!val.IsNil)
                    {
                        td.Def = Type(val, _line, _col);
                        if (td.Def is RecordTypeNode r) td.Kind = r.IsInterface ? TypeDeclKind.Interface : TypeDeclKind.Record;
                        else if (td.Def is EnumTypeNode) td.Kind = TypeDeclKind.Enum;
                    }
                    else if (!td.IsGlobal) throw new MacroException("local_type block without a value");
                    into.Add(td);
                    return;
                }
            case "local_macroexp":
                if (t.GetStr("__stat").O is MacroExpStat me) { into.Add(me); return; }
                break;
        }
        throw new MacroException($"a block of kind '{kind}' cannot be used as a statement");
    }

    // ------------------------------------------------------------------ types

    public static TypeNode Type(in Value v, int line, int col)
    {
        Table t = Tbl(v, "a type");
        string kind = Kind(t);
        object? hidden = t.GetStr("__type").O;
        switch (kind)
        {
            case "newtype":
            case "typedecl":
                if (hidden is TypeNode th) return th;
                return Type(t.GetInt(1), line, col);
            case "identifier":
                return Nominal(Tk(t), line, col);
            case "nominal_type":
                {
                    Value nameBlock = t.GetInt(1);
                    string name = nameBlock.IsNil ? Tk(t) : Name(nameBlock, "a type name");
                    if (hidden is NameTypeNode hn && (hn.FullName == name || hn.Names.Count > 1)) return hn;
                    if (name.Length == 0) throw new MacroException("nominal_type block has no name");
                    return Nominal(name, line, col);
                }
        }
        if (hidden is TypeNode ht) return ht;
        throw new MacroException($"a block of kind '{kind}' cannot be used as a type");
    }

    private static TypeNode Nominal(string name, int line, int col)
    {
        var n = new NameTypeNode { Line = line, Col = col };
        n.Names.AddRange(name.Split('.'));
        return n;
    }
}
