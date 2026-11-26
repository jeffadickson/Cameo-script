namespace Cameo.Runtime.Syntax;

// Parsing of compile-time macros: `local macro name!(...)`, invocations `name!(...)`,
// quotes (`expr` and ```statements```) and `$name` splices inside quotes.
public sealed partial class Parser
{
    private sealed class MacroSig
    {
        public List<bool> ParamIsStatement = new();
        public bool? VarargIsStatement;
    }

    // Identity nodes deliberately have no parent or structural equality. Context
    // hashing stays constant-time even after thousands of sequential declarations.
    private sealed class MacroSignatureContext { }

    private sealed class MacroSignatureContexts
    {
        // Both sealed key components use object identity equality/hash. Interning a
        // transition makes speculative and actual declarations select the same node.
        private readonly Dictionary<(MacroSignatureContext Previous, Token Declaration), MacroSignatureContext> _transitions = new();

        internal MacroSignatureContext Next(MacroSignatureContext previous, Token declaration)
        {
            var key = (previous, declaration);
            if (!_transitions.TryGetValue(key, out var next))
                _transitions.Add(key, next = new MacroSignatureContext());
            return next;
        }
    }

    /// <summary>State shared between a parser and the sub-parsers it creates for quotes and statement arguments.</summary>
    private sealed class MacroState
    {
        public bool Used;
        public MacroSignatureContext SignatureVersion = new();
        public MacroSignatureContexts SignatureContexts = new();
        public Dictionary<string, MacroSig> Sigs = new();
        public bool BorrowedSignatures;

        internal void SetSignature(string name, MacroSig signature)
        {
            // The caller is suspended during probing; reads borrow its immutable view.
            // A declaration isolates the dictionary before publishing its new signature.
            if (BorrowedSignatures) { Sigs = new Dictionary<string, MacroSig>(Sigs); BorrowedSignatures = false; }
            Sigs[name] = signature;
        }
        public readonly List<MacroQuote> Quotes = new();
    }

    private MacroState _macros = new();
    private bool _inMacro;          // inside a `local macro` body: quotes are allowed
    private bool _allowMacroVars;   // inside a quote: `$name` is allowed
    private HashSet<string>? _quoteVars; // macro variables referenced by the quote being parsed

    private Parser(List<Token> tokens, Parser parent)
    {
        _allowExpressionFunctions = parent._allowExpressionFunctions;
        _expressionState = parent._expressionState;
        _src = parent._src;
        _classOwner = parent;
        _file = parent._file;
        _toks = tokens;
        _macros = parent._macros;
        _inMacro = parent._inMacro;
        _allowMacroVars = parent._allowMacroVars;
        _quoteVars = parent._quoteVars;
    }

    private void NoteMacroVar(string name) => _quoteVars?.Add(name);

    // ------------------------------------------------------------------ declaration

    private Stat ParseLocalMacro(Token start)
    {
        _p++; // 'macro'
        Token name = ExpectName();
        ExpectOp("!");
        _macros.Used = true;
        bool savedIn = _inMacro, savedAllow = _allowMacroVars;
        _inMacro = true;
        _allowMacroVars = false;
        FunctionBody body;
        try
        {
            body = ParseFunctionBody(start, name.Text + "!", false);
        }
        finally
        {
            _inMacro = savedIn;
            _allowMacroVars = savedAllow;
        }
        var decl = At(new MacroDeclStat { Name = name.Text, Body = body }, start);
        var sig = new MacroSig();
        foreach (Param p in body.Params) sig.ParamIsStatement.Add(MacroParamKind(p.Type, p));
        if (body.IsVarArg) sig.VarargIsStatement = MacroParamKind(body.VarArgType, body);
        decl.ParamIsStatement = sig.ParamIsStatement;
        decl.VarargIsStatement = sig.VarargIsStatement;
        _macros.SetSignature(name.Text, sig);
        // Stable declaration identity gives speculative and actual parses the same
        // signature context; the prior context distinguishes shadowed definitions.
        _macros.SignatureVersion = _macros.SignatureContexts.Next(_macros.SignatureVersion, name);
        return decl;
    }

    private bool MacroParamKind(TypeNode? type, Node at)
    {
        if (type is NameTypeNode { Names.Count: 1, TypeArgs: null } n)
        {
            if (n.Names[0] == "Statement") return true;
            if (n.Names[0] == "Expression") return false;
        }
        Node where = type ?? at;
        throw new CameoSyntaxException(new Diagnostic(_file, where.Line, where.Col, "macro argument type must be 'Statement' or 'Expression'"));
    }

    // ------------------------------------------------------------------ invocation

    // At '!' after the macro name.
    private Expr ParseMacroCall(Token name)
    {
        _p++; // '!'
        _macros.Used = true;
        var call = At(new MacroCallExpr { Name = name.Text }, name);
        Token t = Cur;
        if (t.Kind == TokenKind.String)
        {
            _p++;
            call.Args.Add(new MacroArg { Expr = At(new StringExpr { Value = t.Text }, t) });
            return call;
        }
        if (IsOp("{"))
        {
            call.Args.Add(new MacroArg { Expr = ParseTable() });
            return call;
        }
        if (t.Kind == TokenKind.MacroQuote)
            throw Error("macro quotes can only be used inside macro statements");
        if (!IsOp("(")) throw Error("expected macro arguments");
        Token open = _toks[_p++];
        _macros.Sigs.TryGetValue(name.Text, out MacroSig? sig);
        int n = 0;
        if (!IsOp(")"))
        {
            while (true)
            {
                bool stmt = false;
                if (sig != null)
                    stmt = n < sig.ParamIsStatement.Count ? sig.ParamIsStatement[n] : sig.VarargIsStatement == true;
                if (stmt)
                {
                    bool canSplit = sig!.VarargIsStatement == true || n < sig.ParamIsStatement.Count - 1;
                    call.Args.Add(new MacroArg { Stats = ParseStatementArg(canSplit) });
                }
                else call.Args.Add(new MacroArg { Expr = ParseExpr() });
                n++;
                if (!AcceptOp(",")) break;
            }
        }
        ExpectClose(")", "(", open);
        return call;
    }

    // A Statement argument: one or more statements up to a top-level ',' or the closing ')'.
    // Statements may themselves contain commas, so candidate end points are tried and the
    // last one at which the tokens parse as statements wins (as the reference reader does).
    private Block ParseStatementArg(bool canSplitOnComma)
    {
        int start = _p;
        var block = At(new Block(), Cur);
        // bare type declarations: record Name ... end
        while (Cur.Kind == TokenKind.Name && (Cur.Text is "record" or "interface" or "enum") && TokIsName(PeekTok()))
        {
            if (_allowExpressionFunctions && IsName("record") && !HasLegacyRecordDeclaration())
            {
                block.Stats.Add(ParseCameoRecord());
                continue;
            }
            Stat? td = TryParseTypeDecl(Cur, false);
            if (td == null) break;
            block.Stats.Add(td);
        }
        int first = _p;
        int depth = 0;
        int best = -1;
        Block? bestBlock = null;
        int j = first;
        for (; _toks[j].Kind != TokenKind.Eof; j++)
        {
            Token t = _toks[j];
            if (t.Kind != TokenKind.Op) continue;
            if (t.Text == "(") depth++;
            else if (t.Text == ")")
            {
                if (depth == 0)
                {
                    Block? b = TryParseSlice(first, j);
                    if (b != null) { best = j; bestBlock = b; }
                    break;
                }
                depth--;
            }
            else if (t.Text == "," && depth == 0 && canSplitOnComma)
            {
                Block? b = TryParseSlice(first, j);
                if (b != null) { best = j; bestBlock = b; }
            }
        }
        if (bestBlock == null)
        {
            if (j == first && block.Stats.Count > 0)
            {
                // only type declarations
                return block;
            }
            // nothing parsed cleanly: parse again to report the real syntax error
            bestBlock = ParseSlice(first, j);
            best = j;
        }
        if (bestBlock.Stats.Count == 1 && bestBlock.Stats[0] is LocalStat ls && (ls.Names.Count > 1 || ls.Values.Count > 1))
            throw Error("wrap the statement in 'do ... end'", _toks[start]);
        block.Stats.AddRange(bestBlock.Stats);
        _p = best;
        return block;
    }

    private Block? TryParseSlice(int from, int to)
    {
        if (to <= from) return null;
        try
        {
            return ParseSlice(from, to);
        }
        catch (CameoSyntaxException)
        {
            return null;
        }
    }

    private Block ParseSlice(int from, int to)
    {
        var slice = new List<Token>(to - from + 1);
        for (int k = from; k < to; k++) slice.Add(_toks[k]);
        Token last = _toks[Math.Max(from, to - 1)];
        slice.Add(new Token { Kind = TokenKind.Eof, Line = last.Line, Col = last.Col, Pos = last.End, End = last.End });
        var sub = new Parser(slice, this);
        Block b = sub.ParseBlock();
        if (sub.Cur.Kind != TokenKind.Eof) throw sub.Error($"unexpected '{sub.Cur}'");
        return b;
    }

    // ------------------------------------------------------------------ quotes

    // A quote evaluates (at macro run time) to a block built from its template. It is compiled
    // as a call `__macro_quote(index, { name = name, ... })` passing the spliced macro variables.
    private Expr ParseMacroQuote()
    {
        Token t = _toks[_p++];
        if (!_inMacro) throw Error("macro quotes can only be used inside macro statements", t);
        if (t.Text.Trim().Length == 0) throw Error("macro quotes cannot be empty", t);
        int delim = t.Triple ? 3 : 1;
        var quote = new MacroQuote { IsStatements = t.Triple, Line = t.Line, Col = t.Col };
        var vars = new HashSet<string>();
        try
        {
            List<Token> toks = Lexer.Tokenize(t.Text, _file);
            foreach (Token qt in toks)
            {
                if (qt.Line == 1) qt.Col += t.Col + delim - 1;
                qt.Line += t.Line - 1;
            }
            var sub = new Parser(toks, this) { _allowMacroVars = true, _inMacro = false, _quoteVars = vars };
            if (t.Triple) quote.Stats = sub.ParseBlock();
            else quote.Expr = sub.ParseExpr();
            if (sub.Cur.Kind != TokenKind.Eof) throw sub.Error($"unexpected '{sub.Cur}'");
        }
        catch (CameoSyntaxException e)
        {
            Diagnostic d = e.Diagnostic;
            throw new CameoSyntaxException(new Diagnostic(d.File, d.Line, d.Col,
                $"macro quote error: {d.Message} (quote starts at {_file}:{t.Line}:{t.Col})"));
        }
        int index = _macros.Quotes.Count;
        _macros.Quotes.Add(quote);
        var table = At(new TableExpr(), t);
        foreach (string v in vars.OrderBy(x => x, StringComparer.Ordinal))
        {
            table.Items.Add(At(new TableItem
            {
                Kind = TableItemKind.Named,
                Key = At(new StringExpr { Value = v }, t),
                Value = At(new NameExpr { Name = v }, t),
            }, t));
        }
        var call = At(new CallExpr { Callee = At(new NameExpr { Name = "__macro_quote" }, t) }, t);
        call.Args.Add(At(new NumberExpr { IsInteger = true, IntValue = index }, t));
        call.Args.Add(table);
        return call;
    }

    // ------------------------------------------------------------------ $name

    private Token ExpectMacroVarName()
    {
        _p++; // '$'
        Token n = ExpectName();
        NoteMacroVar(n.Text);
        return n;
    }

    private Expr ParseMacroVarExpr()
    {
        Token d = Cur;
        Token n = ExpectMacroVarName();
        return At(new MacroVarExpr { Name = n.Text }, d);
    }

    // `$name` alone on a statement line splices statements; otherwise it starts an expression.
    private bool IsMacroVarStatement()
    {
        Token n = PeekTok();
        if (n.Kind != TokenKind.Name) return false;
        Token after = PeekTok(2);
        if (ReferenceEquals(after, n) || after.Kind == TokenKind.Eof) return true;
        if (TokIsOp(after, ";")) return true;
        if (after.Kind == TokenKind.Keyword && after.Text is "end" or "else" or "elseif" or "until") return true;
        return after.Line > n.Line;
    }

    private Stat ParseMacroVarStat()
    {
        Token d = Cur;
        Token n = ExpectMacroVarName();
        AcceptOp(";");
        return At(new MacroVarStat { Name = n.Text }, d);
    }

    // `$T` or `$mod.$T` / `$mod.Name` in type position.
    private TypeNode ParseMacroVarType()
    {
        Token d = Cur;
        Token n = ExpectMacroVarName();
        if (!IsOp(".")) return At(new MacroVarTypeNode { Name = n.Text }, d);
        var nt = At(new NameTypeNode(), d);
        nt.Names.Add("$" + n.Text);
        while (AcceptOp("."))
        {
            if (IsOp("$")) nt.Names.Add("$" + ExpectMacroVarName().Text);
            else nt.Names.Add(ExpectName().Text);
        }
        return nt;
    }

    // `local type $name = <type>` (after `local`).
    private Stat ParseMacroVarTypeDecl(Token start)
    {
        _p++; // 'type'
        Token d = Cur;
        Token n = ExpectMacroVarName();
        var s = At(new TypeDeclStat
        {
            Kind = TypeDeclKind.Alias,
            Name = At(new AttribName { Name = "$" + n.Text, MacroVar = n.Text }, d),
        }, start);
        ExpectOp("=");
        s.Def = ParseNewType(n);
        if (s.Def is RecordTypeNode r) s.Kind = r.IsInterface ? TypeDeclKind.Interface : TypeDeclKind.Record;
        else if (s.Def is EnumTypeNode) s.Kind = TypeDeclKind.Enum;
        return s;
    }
}
