using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Syntax;

/// <summary>Recursive-descent parser for Teal. Throws <see cref="CameoSyntaxException"/> on the first error.</summary>
public sealed partial class Parser
{
    private List<Token> _toks;
    private readonly string _file;
    private readonly string _src;
    private int _p;

    private Parser(string source, string fileName, bool expressionFunctions = false)
    {
        _allowExpressionFunctions = expressionFunctions;
        _src = source;
        _file = fileName;
        _toks = Lexer.Tokenize(source, fileName, expressionFunctions);
    }

    public static Chunk Parse(string source, string fileName) => ParseLanguage(source, fileName, false);

    /// <summary>Parses Cameo declarations, brace control blocks and expression functions
    /// on the shared AST while retaining legacy syntax. Source uses LuaText byte strings.</summary>
    public static Chunk ParseCameo(string source, string fileName) => ParseLanguage(source, fileName, true);

    private static Chunk ParseLanguage(string source, string fileName, bool expressionFunctions)
    {
        var p = new Parser(source, fileName, expressionFunctions);
        try
        {
            Chunk chunk = p.ParseChunk();
            if (p._classRanges is { Count: > 0 } classes)
                chunk.ClassTags = classes.Values.Select(c => (c.Range, new Vm.ClassTag(c.Id))).ToList();
            if (p._expressionState.First is { } arrow && (p._macros.Used || p._expressionState.LegacyReparse))
                throw p.Error("CAMEO003: expression functions cannot share a source file with compile-time macros or where clauses in Cameo 0.1; replace all expression functions in this file with full function ... end forms, or move them to a separate file without macros or where clauses", arrow);
            // compile-time macros are expanded right after parsing, before anything else sees the tree
            if (p._macros.Used) Macros.MacroExpander.Expand(chunk, p._macros.Quotes);
            return chunk;
        }
        catch (InsufficientExecutionStackException)
        {
            throw p.Error("chunk is too deeply nested for the available stack");
        }
    }

    /// <summary>Parses a single expression (used to instantiate <c>where</c> clauses).
    /// Positions are reported relative to the given line.</summary>
    public static Expr ParseExpression(string source, string fileName, int line = 1, bool cameo = false)
    {
        var p = new Parser(source, fileName, cameo);
        foreach (Token t in p._toks) t.Line += line - 1;
        Expr e = p.ParseExpr();
        if (p.Cur.Kind != TokenKind.Eof) throw p.Error("unexpected '" + p.Cur + "' after expression");
        return e;
    }

    // ------------------------------------------------------------------ token helpers

    private Token Cur => _toks[_p];
    private Token PeekTok(int n = 1) => _toks[Math.Min(_p + n, _toks.Count - 1)];

    private CameoSyntaxException Error(string msg, Token? at = null)
    {
        at ??= Cur;
        return new CameoSyntaxException(new Diagnostic(_file, at.Line, at.Col, msg));
    }

    private bool IsOp(string op) => Cur.Kind == TokenKind.Op && Cur.Text == op;
    private bool IsKw(string kw) => Cur.Kind == TokenKind.Keyword && Cur.Text == kw;
    private bool IsName(string name) => Cur.Kind == TokenKind.Name && Cur.Text == name;
    private static bool TokIsOp(Token t, string op) => t.Kind == TokenKind.Op && t.Text == op;
    private static bool TokIsName(Token t) => t.Kind == TokenKind.Name;

    private bool AcceptOp(string op)
    {
        if (IsOp(op)) { _p++; return true; }
        return false;
    }
    private bool AcceptKw(string kw)
    {
        if (IsKw(kw)) { _p++; return true; }
        return false;
    }
    private Token ExpectOp(string op)
    {
        if (!IsOp(op)) throw Error($"'{op}' expected near '{Cur}'");
        return _toks[_p++];
    }
    private Token ExpectKw(string kw)
    {
        if (!IsKw(kw)) throw Error($"'{kw}' expected near '{Cur}'");
        return _toks[_p++];
    }
    private Token ExpectName()
    {
        if (Cur.Kind != TokenKind.Name) throw Error($"name expected near '{Cur}'");
        return _toks[_p++];
    }
    private Token ExpectClose(string close, string open, Token openTok)
    {
        if (IsOp(close) || IsKw(close)) return _toks[_p++];
        if (openTok.Line == Cur.Line) throw Error($"'{close}' expected near '{Cur}'");
        throw Error($"'{close}' expected (to close '{open}' at line {openTok.Line}) near '{Cur}'");
    }

    private static T At<T>(T node, Token t) where T : Node
    {
        node.Line = t.Line;
        node.Col = t.Col;
        return node;
    }

    // ------------------------------------------------------------------ chunk / blocks

    private Chunk ParseChunk()
    {
        Token first = Cur;
        bool savedTopLevel = _inChunkTopLevel;
        List<CameoExportItem>? savedExports = _chunkExports;
        _inChunkTopLevel = _allowExpressionFunctions;
        _chunkExports = null;
        try
        {
            Block body = ParseBlock();
            if (Cur.Kind != TokenKind.Eof) throw Error($"unexpected '{Cur}'");
            List<CameoExportItem>? exports = _chunkExports;
            if (exports is { Count: > 0 })
            {
                if (body.Stats.Count > 0 && body.Stats[^1] is ReturnStat)
                    throw Error("cannot combine 'export' declarations with an explicit top-level 'return'", first);
                var tbl = At(new TableExpr(), first);
                foreach (CameoExportItem exp in exports)
                {
                    if (!exp.HasRuntimeValue) continue;
                    tbl.Items.Add(new TableItem
                    {
                        Kind = TableItemKind.Named,
                        Key = new StringExpr { Value = exp.Name, Line = exp.Line, Col = exp.Col },
                        Value = new NameExpr { Name = exp.Name, Line = exp.Line, Col = exp.Col },
                        Line = exp.Line,
                        Col = exp.Col,
                    });
                }
                body.Stats.Add(new ReturnStat { Values = [tbl], Line = first.Line, Col = first.Col });
            }
            var main = At(new FunctionBody { Name = "main chunk", IsVarArg = true, Body = body, EndLine = Cur.Line }, first);
            return At(new Chunk { FileName = _file, Body = body, Main = main, Source = _src, CameoExports = exports, IsCameo = _allowExpressionFunctions }, first);
        }
        finally
        {
            _inChunkTopLevel = savedTopLevel;
            _chunkExports = savedExports;
        }
    }

    private bool BlockEnd()
    {
        Token t = Cur;
        if (t.Kind == TokenKind.Eof || (_inBraceBlock && IsOp("}"))) return true;
        if (_allowExpressionFunctions) return false;
        if (t.Kind != TokenKind.Keyword) return false;
        return t.Text is "end" or "else" or "elseif" or "until";
    }

    private Block ParseBlock(bool braced = false)
    {
        bool savedBrace = _inBraceBlock;
        int savedHeader = _headerExpressionDepth;
        bool isTopLevel = _inChunkTopLevel;
        _inChunkTopLevel = false;
        _inBraceBlock = braced;
        _headerExpressionDepth = 0;
        var savedHoist = _hoistedStats;
        bool savedAllowHoist = _allowTryHoist;
        try
        {
            var block = At(new Block(), Cur);
            while (!BlockEnd())
            {
                var hoist = new List<Stat>();
                _hoistedStats = hoist;
                _allowTryHoist = true;
                if (IsKw("return"))
                {
                    Stat ret = ParseReturn();
                    if (hoist.Count > 0) block.Stats.AddRange(hoist);
                    block.Stats.Add(ret);
                    break;
                }
                if (isTopLevel && AtCameoExport())
                {
                    EnterLevel();
                    try
                    {
                        var seq = ParseCameoExportSequence();
                        if (hoist.Count > 0) block.Stats.AddRange(hoist);
                        block.Stats.AddRange(seq);
                    }
                    finally { _levels--; }
                    continue;
                }
                if (AtCameoLet())
                {
                    EnterLevel();
                    try
                    {
                        var seq = ParseCameoLetSequence();
                        if (hoist.Count > 0) block.Stats.AddRange(hoist);
                        block.Stats.AddRange(seq);
                    }
                    finally { _levels--; }
                    continue;
                }
                if (AtCameoVar())
                {
                    EnterLevel();
                    try
                    {
                        var seq = ParseCameoVarSequence();
                        if (hoist.Count > 0) block.Stats.AddRange(hoist);
                        block.Stats.AddRange(seq);
                    }
                    finally { _levels--; }
                    continue;
                }
                if (AtCameoDeclaration("record"))
                {
                    EnterLevel();
                    try { block.Stats.AddRange(ParseCameoRecordSequence(isGlobal: false)); }
                    finally { _levels--; }
                    continue;
                }
                if (AtCameoDeclaration("interface"))
                {
                    EnterLevel();
                    try { block.Stats.Add(ParseCameoInterface(isGlobal: false)); }
                    finally { _levels--; }
                    continue;
                }
                if (_allowExpressionFunctions && IsName("global") && TokIsName(PeekTok()) && PeekTok().Text == "record" && TokIsName(PeekTok(2)) && HasBracedTypeDecl(2))
                {
                    EnterLevel();
                    try { _p++; block.Stats.AddRange(ParseCameoRecordSequence(isGlobal: true)); }
                    finally { _levels--; }
                    continue;
                }
                if (_allowExpressionFunctions && IsName("global") && TokIsName(PeekTok()) && PeekTok().Text == "interface" && TokIsName(PeekTok(2)) && HasBracedTypeDecl(2))
                {
                    EnterLevel();
                    try { _p++; block.Stats.Add(ParseCameoInterface(isGlobal: true)); }
                    finally { _levels--; }
                    continue;
                }
                Stat? s = ParseStatement();
                if (hoist.Count > 0) block.Stats.AddRange(hoist);
                if (s != null) block.Stats.Add(s);
            }
            return block;
        }
        finally { _inChunkTopLevel = isTopLevel; _inBraceBlock = savedBrace; _headerExpressionDepth = savedHeader; _hoistedStats = savedHoist; _allowTryHoist = savedAllowHoist; }
    }

    private Stat ParseReturn()
    {
        Token t = ExpectKw("return");
        var r = At(new ReturnStat(), t);
        if (!BlockEnd() && !IsOp(";")) r.Values = ParseExprList();
        AcceptOp(";");
        return r;
    }

    // ------------------------------------------------------------------ statements

    private Stat? ParseStatement()
    {
        EnterLevel();
        try { return ParseStatementCore(); }
        finally { _levels--; }
    }

    private Stat? ParseStatementCore()
    {
        if (AtCameoImport()) return ParseCameoImport();
        if (AtCameoMatch()) return At(new MatchStat { Match = ParseCameoMatch(isStatement: true) }, Cur);
        if (AtCameoLet()) return ParseCameoLet();
        if (AtCameoDeclaration("using") && (TokIsOp(PeekTok(2), "=") || TokIsOp(PeekTok(2), ":")))
            return ParseCameoUsing();
        if (AtCameoVar()) return ParseCameoVar();
        if (AtCameoDeclaration("fn")) return ParseCameoFunction();
        if (AtCameoDeclaration("record")) return ParseCameoRecord();
        if (AtCameoDeclaration("interface")) return ParseCameoInterface(isGlobal: false);
        if (AtCameoDeclaration("enum") && TokIsOp(PeekTok(2), "{")) return ParseCameoEnum();
        if (AtCameoTypeAlias()) return ParseCameoTypeAlias(isGlobal: false);
        if (!_allowExpressionFunctions && AtClassDeclaration()) RewriteClass(); // a class becomes a record, a metatable and functions
        Token t = Cur;
        if (t.Kind == TokenKind.Op)
        {
            if (t.Text == ";") { _p++; return null; }
            if (t.Text == "$" && _allowMacroVars && IsMacroVarStatement()) return ParseMacroVarStat();
            if (t.Text == "::")
            {
                _p++;
                Token n = ExpectName();
                ExpectOp("::");
                return At(new LabelStat { Name = n.Text }, t);
            }
        }
        else if (t.Kind == TokenKind.Keyword)
        {
            switch (t.Text)
            {
                case "if": return ParseIf();
                case "while":
                    {
                        _p++;
                        Expr cond = ParseControlExpr("do");
                        Block body = ParseControlBody("do", "while", t);
                        return At(new WhileStat { Cond = cond, Body = body }, t);
                    }
                case "do":
                    {
                        _p++;
                        Block body;
                        if (_allowExpressionFunctions)
                        {
                            if (!IsOp("{")) throw Error("use 'do { ... }' instead of 'do ... end' in Cameo", t);
                            body = ParseBraceBlock();
                        }
                        else { body = ParseBlock(); ExpectClose("end", "do", t); }
                        return At(new DoStat { Body = body }, t);
                    }
                case "for": return ParseFor();
                case "repeat":
                    {
                        if (_allowExpressionFunctions)
                            throw Error("'repeat ... until' is not supported in Cameo; use 'while' with '{ ... }'", t);
                        _p++;
                        Block body = ParseBlock();
                        ExpectClose("until", "repeat", t);
                        Expr cond = ParseExpr();
                        return At(new RepeatStat { Body = body, Cond = cond }, t);
                    }
                case "function":
                    if (_allowExpressionFunctions)
                        throw Error("use 'fn name(...) { ... }' instead of 'function ... end' in Cameo", t);
                    return ParseFunctionStat();
                case "local":
                    if (_allowExpressionFunctions)
                        throw Error("use 'let' (immutable) or 'var' (mutable) instead of 'local' in Cameo", t);
                    return ParseLocal();
                case "break": _p++; return At(new BreakStat { Label = LoopLabel(t) }, t);
                case "goto":
                    {
                        _p++;
                        Token n = ExpectName();
                        return At(new GotoStat { Label = n.Text }, t);
                    }
                case "end" when _allowExpressionFunctions:
                    throw Error("use '}' instead of 'end' in Cameo", t);
                case "then" when _allowExpressionFunctions:
                    throw Error("use '{ ... }' instead of 'then ... end' in Cameo", t);
                case "elseif" when _allowExpressionFunctions:
                    throw Error("use 'else if' instead of 'elseif' in Cameo", t);
                case "until" when _allowExpressionFunctions:
                    throw Error("'repeat ... until' is not supported in Cameo; use 'while' with '{ ... }'", t);
            }
        }
        else if (t.Kind == TokenKind.Name && t.Text == "global" && IsGlobalDecl())
        {
            return ParseGlobal();
        }
        return ParseExprStat();
    }

    // `global` is a contextual keyword: it starts a declaration when followed by a name or `function`.
    private bool IsGlobalDecl()
    {
        Token n = PeekTok();
        return n.Kind == TokenKind.Name || (n.Kind == TokenKind.Keyword && n.Text == "function");
    }

    private Stat ParseIf()
    {
        Token t = ExpectKw("if");
        var s = At(new IfStat(), t);
        Expr cond = ParseControlExpr("then");
        if (_allowExpressionFunctions)
        {
            if (IsKw("then")) throw Error("use '{ ... }' instead of 'then ... end' in Cameo", Cur);
            return ParseCameoIfRest(t, s, cond);
        }
        ExpectKw("then");
        s.Clauses.Add((cond, ParseBlock()));
        while (true)
        {
            if (IsKw("elseif"))
            {
                _p++;
                Expr c = ParseExpr();
                ExpectKw("then");
                s.Clauses.Add((c, ParseBlock()));
            }
            else if (IsKw("else"))
            {
                _p++;
                s.Else = ParseBlock();
                ExpectClose("end", "if", t);
                break;
            }
            else
            {
                ExpectClose("end", "if", t);
                break;
            }
        }
        return s;
    }

    private Stat ParseFor()
    {
        Token t = ExpectKw("for");
        Token n1 = ExpectName();
        if (IsOp("="))
        {
            _p++;
            var s = At(new ForNumStat { Var = At(new AttribName { Name = n1.Text }, n1) }, t);
            s.Start = ParseControlExpr("do");
            ExpectOp(",");
            s.Limit = ParseControlExpr("do");
            if (AcceptOp(",")) s.Step = ParseControlExpr("do");
            s.Body = ParseControlBody("do", "for", t);
            return s;
        }
        var f = At(new ForInStat(), t);
        f.Names.Add(At(new AttribName { Name = n1.Text }, n1));
        while (AcceptOp(","))
        {
            Token n = ExpectName();
            f.Names.Add(At(new AttribName { Name = n.Text }, n));
        }
        ExpectKw("in");
        f.Exprs = ParseControlExprList("do");
        f.CameoForIn = _allowExpressionFunctions && f.Exprs.Count == 1;
        f.Body = ParseControlBody("do", "for", t);
        return f;
    }

    private Stat ParseFunctionStat()
    {
        Token t = ExpectKw("function");
        // Inside a macro quote a name piece may be a macro variable: function $owner.$name()
        (Token Tok, string Text) NamePiece()
        {
            if (_allowMacroVars && IsOp("$"))
            {
                Token d = Cur;
                return (d, "$" + ExpectMacroVarName().Text); // consumes the '$' too
            }
            Token tok = ExpectName();
            return (tok, tok.Text);
        }
        var (n, rootName) = NamePiece();
        var s = At(new FunctionStat { Root = At(new NameExpr { Name = rootName }, n) }, t);
        string fullName = rootName;
        while (IsOp("."))
        {
            _p++;
            string piece = NamePiece().Text;
            s.Path.Add(piece);
            fullName += "." + piece;
        }
        if (IsOp(":"))
        {
            _p++;
            Token f = ExpectName();
            s.Path.Add(f.Text);
            s.IsMethod = true;
            fullName += ":" + f.Text;
        }
        s.Body = ParseFunctionBody(t, fullName, s.IsMethod);
        return s;
    }

    private static readonly HashSet<string> TypeDeclWords = new() { "record", "interface", "enum" };

    private Stat ParseLocal()
    {
        Token t = ExpectKw("local");
        if (IsKw("function"))
        {
            _p++;
            if (_allowMacroVars && IsOp("$"))
            {
                // local function $name(...) inside a macro quote
                Token d = Cur;
                Token mv = ExpectMacroVarName();
                var ms = At(new LocalFunctionStat { Name = At(new AttribName { Name = "$" + mv.Text, MacroVar = mv.Text }, d) }, t);
                ms.Body = ParseFunctionBody(t, mv.Text, false);
                return ms;
            }
            Token n = ExpectName();
            var s = At(new LocalFunctionStat { Name = At(new AttribName { Name = n.Text }, n) }, t);
            s.Body = ParseFunctionBody(t, n.Text, false);
            return s;
        }
        Stat? td = TryParseTypeDecl(t, false);
        if (td != null) return td;
        if (IsName("macroexp") && TokIsName(PeekTok()))
        {
            _p++;
            Token n = ExpectName();
            return At(new MacroExpStat { Name = n.Text, Macro = ParseMacroExp(t, n.Text) }, t);
        }
        if (IsName("macro") && TokIsName(PeekTok()) && TokIsOp(PeekTok(2), "!"))
            return ParseLocalMacro(t);
        if (_allowMacroVars && IsName("type") && TokIsOp(PeekTok(), "$"))
            return ParseMacroVarTypeDecl(t);
        var l = At(new LocalStat(), t);
        ParseAttribNames(l.Names);
        if (AcceptOp(":")) l.Types = ParseTypeList();
        if (AcceptOp("=")) l.Values = ParseExprList();
        return l;
    }

    private Stat ParseGlobal()
    {
        Token t = _toks[_p++]; // 'global'
        if (IsKw("function"))
        {
            if (_allowExpressionFunctions)
                throw Error("use 'global name: fn(...) -> ... = ...' or 'fn' instead of 'global function ... end' in Cameo", t);
            _p++;
            Token n = ExpectName();
            var s = At(new GlobalFunctionStat { Name = n.Text }, t);
            s.Body = ParseFunctionBody(t, n.Text, false);
            return s;
        }
        if (_allowExpressionFunctions && IsName("enum") && TokIsName(PeekTok()) && TokIsOp(PeekTok(2), "{"))
            return ParseCameoEnum(isGlobal: true);
        Stat? td = TryParseTypeDecl(t, true);
        if (td != null) return td;
        var g = At(new GlobalStat(), t);
        ParseAttribNames(g.Names);
        if (AcceptOp(":")) g.Types = ParseTypeList();
        if (AcceptOp("=")) g.Values = ParseExprList();
        return g;
    }

    // After `local`/`global`: record/interface/enum Name ..., or type Name [= newtype].
    private Stat? TryParseTypeDecl(Token start, bool isGlobal)
    {
        if (Cur.Kind != TokenKind.Name || !TokIsName(PeekTok())) return null;
        string word = Cur.Text;
        if (TypeDeclWords.Contains(word))
        {
            if (_allowExpressionFunctions)
                throw Error($"use '{word} Name {{ ... }}' instead of '{word} Name ... end' in Cameo", Cur);
            _p++;
            Token n = ExpectName();
            var s = At(new TypeDeclStat { IsGlobal = isGlobal, Name = At(new AttribName { Name = n.Text }, n) }, start);
            if (word == "enum")
            {
                s.Kind = TypeDeclKind.Enum;
                s.Def = ParseEnumBody(n);
            }
            else
            {
                s.Kind = word == "record" ? TypeDeclKind.Record : TypeDeclKind.Interface;
                s.Def = ParseRecordBody(n, word == "interface");
            }
            return s;
        }
        if (word == "type")
        {
            Token after = PeekTok(2);
            if (TokIsOp(after, "<"))
            {
                // generic alias: type Name<T, U> = ...
                _p++;
                Token gn = ExpectName();
                var gs = At(new TypeDeclStat { IsGlobal = isGlobal, Kind = TypeDeclKind.Alias, Name = At(new AttribName { Name = gn.Text }, gn) }, start);
                gs.TypeParams = ParseTypeParams();
                ExpectOp("=");
                gs.Def = ParseNewType(gn);
                if (gs.Def is RecordTypeNode gr)
                {
                    gs.Kind = gr.IsInterface ? TypeDeclKind.Interface : TypeDeclKind.Record;
                    gr.TypeParams ??= gs.TypeParams;
                }
                else if (gs.Def is EnumTypeNode) gs.Kind = TypeDeclKind.Enum;
                return gs;
            }
            bool hasDef = TokIsOp(after, "=");
            // `global type Name` is a forward declaration; `local type, x = ...` is an ordinary declaration.
            if (!hasDef && (!isGlobal || TokIsOp(after, ",") || TokIsOp(after, ":"))) return null;
            _p++;
            Token n = ExpectName();
            var s = At(new TypeDeclStat { IsGlobal = isGlobal, Kind = TypeDeclKind.Alias, Name = At(new AttribName { Name = n.Text }, n) }, start);
            if (hasDef)
            {
                _p++; // '='
                s.Def = ParseNewType(n);
                if (s.Def is RecordTypeNode r) s.Kind = r.IsInterface ? TypeDeclKind.Interface : TypeDeclKind.Record;
                else if (s.Def is EnumTypeNode) s.Kind = TypeDeclKind.Enum;
            }
            return s;
        }
        return null;
    }

    private void ParseAttribNames(List<AttribName> names)
    {
        do
        {
            if (_allowMacroVars && IsOp("$"))
            {
                Token d = _toks[_p++];
                Token mv = ExpectName();
                NoteMacroVar(mv.Text);
                names.Add(At(new AttribName { Name = "$" + mv.Text, MacroVar = mv.Text }, d));
                continue;
            }
            Token n = ExpectName();
            var an = At(new AttribName { Name = n.Text }, n);
            if (IsOp("<"))
            {
                _p++;
                Token a = ExpectName();
                if (a.Text is not ("const" or "close" or "total"))
                    throw Error($"unknown variable attribute '{a.Text}'", a);
                an.Attrib = a.Text;
                ExpectOp(">");
            }
            names.Add(an);
        } while (AcceptOp(","));
    }

    // `continue` is not a reserved word (programs may use it as a name): it is the statement
    // only where it stands alone, i.e. is not followed by something that continues an expression.
    private bool IsContinueStatement()
    {
        if (!IsName("continue")) return false;
        Token next = PeekTok();
        if (next.Kind == TokenKind.Name && next.Line == Cur.Line) return !ContinuesExpression(PeekTok(2)); // continue <label>
        if (next.Kind == TokenKind.String || next.Kind == TokenKind.Interp) return false;
        if (next.Kind != TokenKind.Op) return true;
        return next.Text is not ("=" or "," or "." or ":" or "(" or "[" or "{"
            or "+=" or "-=" or "*=" or "/=" or "//=" or "~/=" or "%=" or "^=" or "**=" or "..=" or "&=" or "|=" or "<<=" or ">>=");
    }

    private static bool ContinuesExpression(Token t) =>
        t.Kind == TokenKind.String || t.Kind == TokenKind.Interp || (t.Kind == TokenKind.Op && t.Text is "=" or "," or "." or ":" or "(" or "[" or "{"
            or "+=" or "-=" or "*=" or "/=" or "//=" or "~/=" or "%=" or "^=" or "**=" or "..=" or "&=" or "|=" or "<<=" or ">>=");

    // After `break` or `continue`: the name of the labelled loop to act on, if one follows on the
    // same line (`break outer`). A name on the next line, or one that starts a statement of its
    // own (`break  f()`), is not a label.
    private string? LoopLabel(Token keyword)
    {
        if (Cur.Kind != TokenKind.Name || Cur.Line != keyword.Line || ContinuesExpression(PeekTok())) return null;
        return _toks[_p++].Text;
    }

    private bool CompoundOp(string text, out BinaryOp op)
    {
        switch (text)
        {
            case "+=": op = BinaryOp.Add; return true;
            case "-=": op = BinaryOp.Sub; return true;
            case "*=": op = BinaryOp.Mul; return true;
            case "/=": op = BinaryOp.Div; return true;
            case "//=" when !_allowExpressionFunctions: op = BinaryOp.IDiv; return true;
            case "~/=" when _allowExpressionFunctions: op = BinaryOp.IDiv; return true;
            case "%=": op = BinaryOp.Mod; return true;
            case "^=": op = _allowExpressionFunctions ? BinaryOp.BXor : BinaryOp.Pow; return true;
            case "**=" when _allowExpressionFunctions: op = BinaryOp.Pow; return true;
            case "..=": op = BinaryOp.Concat; return true;
            case "&=": op = BinaryOp.BAnd; return true;
            case "|=": op = BinaryOp.BOr; return true;
            case "<<=": op = BinaryOp.Shl; return true;
            case ">>=": op = BinaryOp.Shr; return true;
            default: op = default; return false;
        }
    }

    // An expression that can be written twice without changing what the program does.
    private static bool IsRepeatable(Expr e) => e is NameExpr or StringExpr or NumberExpr;

    private static Expr Again(Expr e) => e switch
    {
        NameExpr n => new NameExpr { Name = n.Name, Line = n.Line, Col = n.Col },
        StringExpr s => new StringExpr { Value = s.Value, Line = s.Line, Col = s.Col },
        NumberExpr n => new NumberExpr { IsInteger = n.IsInteger, IntValue = n.IntValue, FloatValue = n.FloatValue, Line = n.Line, Col = n.Col },
        _ => throw new InvalidOperationException("not a repeatable expression"),
    };

    // `target op= value` means `target = target op (value)`, with the parts of the target
    // evaluated once: `f().n += 1` calls f once. It is rewritten here into ordinary statements,
    //     do local o = <object>; local k = <key>; o[k] = o[k] op value end
    // keeping a constant key in place (so that record fields still type-check) and leaving out
    // the locals for parts that are plain names.
    private Stat ParseCompoundAssign(Expr target, BinaryOp op, Token t, Token opTok)
    {
        if (target is not (NameExpr or IndexExpr))
            throw Error($"cannot use '{opTok.Text}' on this expression (a variable or field is needed)", t);
        Expr value = ParseExpr();
        if (IsOp(",")) throw Error($"'{opTok.Text}' takes one value", Cur);

        AssignStat Assign(Expr to, Expr from)
        {
            var a = At(new AssignStat(), t);
            a.Targets.Add(to);
            a.Values.Add(At(new BinaryExpr { Op = op, Left = from, Right = value }, opTok));
            return a;
        }

        if (target is NameExpr name) return Assign(name, Again(name));

        var index = (IndexExpr)target;
        var block = At(new Block(), t);
        Expr Hoist(Expr part, string hidden)
        {
            if (IsRepeatable(part)) return part;
            // a name no program can write, so it cannot shadow anything
            var local = At(new LocalStat(), t);
            local.Names.Add(At(new AttribName { Name = hidden }, t));
            local.Values.Add(part);
            block.Stats.Add(local);
            return new NameExpr { Name = hidden, Line = part.Line, Col = part.Col };
        }
        Expr obj = Hoist(index.Object, "(object)");
        Expr key = Hoist(index.Key, "(key)");
        IndexExpr Slot() => new() { Object = Again(obj), Key = Again(key), IsDot = index.IsDot, Line = index.Line, Col = index.Col };
        AssignStat assign = Assign(Slot(), Slot());
        if (block.Stats.Count == 0) return assign;
        block.Stats.Add(assign);
        return At(new DoStat { Body = block }, t);
    }

    // $"text {expr} text": each {expr} is replaced by tostring(expr); {{ and }} are literal
    // braces. It is rewritten into ("text %s text"):format(expr), which cannot be affected by
    // a local named `tostring` or `string`.
    private Expr ParseInterpolated(Token t)
    {
        var format = new System.Text.StringBuilder();
        var literal = new System.Text.StringBuilder();
        var args = new List<Expr>();
        foreach (var (isExpr, text) in t.Parts!)
        {
            if (!isExpr)
            {
                literal.Append(text);
                format.Append(text.Replace("%", "%%"));
                continue;
            }
            try
            {
                List<Token> toks = Lexer.Tokenize(text, _file, _allowExpressionFunctions);
                foreach (Token st in toks) { st.Line = t.Line; st.Col = t.Col; }
                var sub = new Parser(toks, this);
                args.Add(sub.ParseExpr());
                if (sub.Cur.Kind != TokenKind.Eof) throw sub.Error($"unexpected '{sub.Cur}'");
            }
            catch (CameoSyntaxException e)
            {
                Diagnostic d = e.Diagnostic;
                throw new CameoSyntaxException(new Diagnostic(d.File, t.Line, t.Col, $"in interpolated '{{{text.Trim()}}}': {d.Message}"));
            }
            format.Append("%s");
        }
        if (args.Count == 0) return At(new StringExpr { Value = literal.ToString() }, t);
        var call = At(new MethodCallExpr { Object = At(new StringExpr { Value = format.ToString() }, t), Name = "format" }, t);
        call.Args.AddRange(args);
        return call;
    }

    private Stat ParseExprStat()
    {
        Token t = Cur;
        if (IsContinueStatement()) { _p++; return At(new ContinueStat { Label = LoopLabel(t) }, t); }
        Expr e = ParseSuffixedExpr();
        if (Cur.Kind == TokenKind.Op && CompoundOp(Cur.Text, out BinaryOp compound))
        {
            Token opTok = _toks[_p++];
            return ParseCompoundAssign(e, compound, t, opTok);
        }
        if (IsOp("=") || IsOp(","))
        {
            var a = At(new AssignStat(), t);
            a.Targets.Add(CheckAssignable(e, t));
            while (AcceptOp(","))
            {
                Token tt = Cur;
                a.Targets.Add(CheckAssignable(ParseSuffixedExpr(), tt));
            }
            ExpectOp("=");
            a.Values = ParseExprList();
            return a;
        }
        if (e is CallExpr || e is MethodCallExpr) return At(new CallStat { Call = e }, t);
        if (e is MacroCallExpr mc) return At(new MacroCallStat { Call = mc }, t);
        throw Error($"syntax error near '{Cur}'", Cur.Kind == TokenKind.Eof ? t : Cur);
    }

    private Expr CheckAssignable(Expr e, Token t)
    {
        if (e is NameExpr || e is IndexExpr || e is MacroCallExpr || e is MacroVarExpr) return e;
        throw Error("cannot assign to this expression", t);
    }

    // ------------------------------------------------------------------ functions

    private FunctionBody ParseFunctionBody(Token start, string name, bool isMethod, bool cameo = false)
    {
        var fb = At(new FunctionBody { Name = name }, start);
        Token? varArgName = null;
        if (IsOp("<")) fb.TypeParams = ParseTypeParams();
        Token open = ExpectOp("(");
        if (isMethod) fb.Params.Add(At(new Param { Name = "self", IsImplicitSelf = true }, start));
        if (!IsOp(")"))
        {
            do
            {
                if (IsOp("..."))
                {
                    Token dots = _toks[_p++];
                    fb.IsVarArg = true;
                    if (Cur.Kind == TokenKind.Name) varArgName = _toks[_p++];
                    if (AcceptOp(":")) fb.VarArgType = ParseType();
                    break;
                }
                Token n = ExpectName();
                var prm = At(new Param { Name = n.Text }, n);
                if (AcceptOp("?")) prm.Optional = true;
                if (AcceptOp(":")) prm.Type = ParseType();
                fb.Params.Add(prm);
            } while (AcceptOp(","));
        }
        ExpectClose(")", "(", open);
        if (cameo ? AcceptCameoReturnArrow() : AcceptOp(":"))
        {
            fb.HasReturnAnnotation = true;
            ParseReturnTypes(fb.Returns, out fb.ReturnsVarArg);
        }
        Token? brace = cameo ? ExpectOp("{") : null;
        fb.Body = ParseBlock(cameo);
        if (varArgName != null)
        {
            // Named variadic parameter `...name: T`: inside the function, `name` is a constant
            // holding the packed extra arguments, i.e. `local name <const>: table.PackTable<T> = table.pack(...)`.
            var decl = At(new LocalStat(), varArgName);
            decl.Names.Add(At(new AttribName { Name = varArgName.Text, Attrib = "const" }, varArgName));
            var packType = At(new NameTypeNode(), varArgName);
            packType.Names.Add("table");
            packType.Names.Add("PackTable");
            TypeNode elemType = fb.VarArgType ?? AnyTypeNode(varArgName);
            packType.TypeArgs = new List<TypeNode> { elemType };
            decl.Types = new List<TypeNode> { packType };
            var pack = At(new IndexExpr
            {
                Object = At(new NameExpr { Name = "table" }, varArgName),
                Key = At(new StringExpr { Value = "pack" }, varArgName),
                IsDot = true,
            }, varArgName);
            var call = At(new CallExpr { Callee = pack }, varArgName);
            call.Args.Add(At(new VarargExpr(), varArgName));
            decl.Values.Add(call);
            fb.Body.Stats.Insert(0, decl);
        }
        fb.EndLine = Cur.Line;
        if (cameo) ExpectClose("}", "{", brace!);
        else ExpectClose("end", "function", start);
        return fb;
    }

    private static TypeNode AnyTypeNode(Token at)
    {
        var n = At(new NameTypeNode(), at);
        n.Names.Add("any");
        return n;
    }

    private MacroExp ParseMacroExp(Token start, string name)
    {
        _expressionState.LegacyReparse = true;
        int from = Cur.Pos, line = Cur.Line;
        var fb = ParseFunctionBody(start, name, false);
        int to = _toks[_p - 1].End;
        string source = new string('\n', line - 1) + "function" + _src.Substring(from, to - from);
        return At(new MacroExp { Body = fb, Source = source }, start);
    }

    /// <summary>Parses a fresh copy of a macroexp (from <see cref="MacroExp.Source"/>).</summary>
    public static FunctionBody ParseMacroBody(string source, string fileName) =>
        ((FunctionExpr)ParseExpression(source, fileName)).Body;

    // ------------------------------------------------------------------ expressions

    private List<Expr> ParseExprList()
    {
        var list = new List<Expr> { ParseExpr() };
        while (AcceptOp(",")) list.Add(ParseExpr());
        return list;
    }

    private const int IsPriority = 3;
    private const int CameoIsPriority = 5;
    private const int CameoComparePriority = 6;
    private const int CameoUnaryLimit = 15;

    private bool BinaryInfo(Token t, out BinaryOp op, out int left, out int right)
    {
        op = default; left = right = 0;
        if (_allowExpressionFunctions)
        {
            if (t.Kind == TokenKind.Keyword)
            {
                if (t.Text == "and") throw Error("use '&&' instead of 'and' in Cameo", t);
                if (t.Text == "or") throw Error("use '||' instead of 'or' in Cameo", t);
                return false;
            }
            if (t.Kind != TokenKind.Op) return false;
            switch (t.Text)
            {
                case "??": op = BinaryOp.Coalesce; left = 2; right = 1; return true;
                case "||": op = BinaryOp.Or; left = right = 3; return true;
                case "&&": op = BinaryOp.And; left = right = 4; return true;
                case "<": op = BinaryOp.Lt; left = right = CameoComparePriority; return true;
                case ">": op = BinaryOp.Gt; left = right = CameoComparePriority; return true;
                case "<=": op = BinaryOp.Le; left = right = CameoComparePriority; return true;
                case ">=": op = BinaryOp.Ge; left = right = CameoComparePriority; return true;
                case "!=": op = BinaryOp.Ne; left = right = CameoComparePriority; return true;
                case "==": op = BinaryOp.Eq; left = right = CameoComparePriority; return true;
                case "~=": throw Error("use '!=' instead of '~=' in Cameo", t);
                case "|": op = BinaryOp.BOr; left = right = 7; return true;
                case "^": op = BinaryOp.BXor; left = right = 8; return true;
                case "~": throw Error("use '^' for bitwise XOR in Cameo ('~' is unary bitwise NOT)", t);
                case "&": op = BinaryOp.BAnd; left = right = 9; return true;
                case "<<": op = BinaryOp.Shl; left = right = 10; return true;
                case ">>": op = BinaryOp.Shr; left = right = 10; return true;
                case "..": op = BinaryOp.Concat; left = 12; right = 11; return true;
                case "+": op = BinaryOp.Add; left = right = 13; return true;
                case "-": op = BinaryOp.Sub; left = right = 13; return true;
                case "*": op = BinaryOp.Mul; left = right = 14; return true;
                case "/": op = BinaryOp.Div; left = right = 14; return true;
                case "~/": op = BinaryOp.IDiv; left = right = 14; return true;
                case "%": op = BinaryOp.Mod; left = right = 14; return true;
                case "**": op = BinaryOp.Pow; left = 17; right = 16; return true;
            }
            return false;
        }
        if (t.Kind == TokenKind.Keyword)
        {
            if (t.Text == "or") { op = BinaryOp.Or; left = right = 1; return true; }
            if (t.Text == "and") { op = BinaryOp.And; left = right = 2; return true; }
            return false;
        }
        if (t.Kind != TokenKind.Op) return false;
        switch (t.Text)
        {
            case "<": op = BinaryOp.Lt; left = right = 4; return true;
            case ">": op = BinaryOp.Gt; left = right = 4; return true;
            case "<=": op = BinaryOp.Le; left = right = 4; return true;
            case ">=": op = BinaryOp.Ge; left = right = 4; return true;
            case "~=": op = BinaryOp.Ne; left = right = 4; return true;
            case "==": op = BinaryOp.Eq; left = right = 4; return true;
            case "|": op = BinaryOp.BOr; left = right = 5; return true;
            case "~": op = BinaryOp.BXor; left = right = 6; return true;
            case "&": op = BinaryOp.BAnd; left = right = 7; return true;
            case "<<": op = BinaryOp.Shl; left = right = 8; return true;
            case ">>": op = BinaryOp.Shr; left = right = 8; return true;
            case "..": op = BinaryOp.Concat; left = 10; right = 9; return true;
            case "+": op = BinaryOp.Add; left = right = 11; return true;
            case "-": op = BinaryOp.Sub; left = right = 11; return true;
            case "*": op = BinaryOp.Mul; left = right = 12; return true;
            case "/": op = BinaryOp.Div; left = right = 12; return true;
            case "//": op = BinaryOp.IDiv; left = right = 12; return true;
            case "%": op = BinaryOp.Mod; left = right = 12; return true;
            case "^": op = BinaryOp.Pow; left = 15; right = 14; return true;
        }
        return false;
    }

    public Expr ParseExpr()
    {
        _expressionDepth++;
        try
        {
            Expr expr = ParseSubExpr(0);
            while (_allowExpressionFunctions && IsOp("|>"))
            {
                Token pipe = _toks[_p++];
                Expr stage = ParseSubExpr(0);
                expr = LowerCameoPipe(expr, stage, pipe);
            }
            return expr;
        }
        finally { _expressionDepth--; }
    }

    // Precedence climbing; binary operators with left priority > limit are consumed.
    /// <summary>Maximum nesting of expressions, blocks and types (the same bound as Lua's parser).</summary>
    public const int MaxSyntaxLevels = 200;
    private int _levels;

    private void EnterLevel()
    {
        if (++_levels > MaxSyntaxLevels) throw Error("chunk has too many syntax levels");
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
    }

    private Expr ParseSubExpr(int limit)
    {
        EnterLevel();
        try { return ParseSubExprCore(limit); }
        finally { _levels--; }
    }

    private Expr ParseSubExprCore(int limit)
    {
        Expr left;
        Token t = Cur;
        UnaryOp? uop = null;
        if (t.Kind == TokenKind.Keyword && t.Text == "not")
        {
            if (_allowExpressionFunctions) throw Error("use '!' instead of 'not' in Cameo", t);
            uop = UnaryOp.Not;
        }
        else if (t.Kind == TokenKind.Op)
        {
            if (_allowExpressionFunctions && t.Text == "!") uop = UnaryOp.Not;
            else if (t.Text == "-") uop = UnaryOp.Neg;
            else if (t.Text == "#") uop = UnaryOp.Len;
            else if (t.Text == "~") uop = UnaryOp.BNot;
        }
        if (uop != null)
        {
            _p++;
            Expr operand = ParseSubExpr(_allowExpressionFunctions ? CameoUnaryLimit : 13);
            // fold negative numeric literals
            if (uop == UnaryOp.Neg && operand is NumberExpr ne)
            {
                if (ne.IsInteger) ne.IntValue = unchecked(-ne.IntValue); else ne.FloatValue = -ne.FloatValue;
                ne.Line = t.Line; ne.Col = t.Col;
                left = ne;
            }
            else left = At(new UnaryExpr { Op = uop.Value, Operand = operand }, t);
        }
        else left = ParseSimpleExpr();

        int isPrio = _allowExpressionFunctions ? CameoIsPriority : IsPriority;
        while (true)
        {
            Token ot = Cur;
            if (ot.Kind == TokenKind.Name && ot.Text == "is" && isPrio > limit)
            {
                _p++;
                TypeNode ty = ParseType();
                // `!x is T` / `not x is T` means `!(x is T)`
                if (uop == UnaryOp.Not && left is UnaryExpr { Op: UnaryOp.Not } notExpr)
                    notExpr.Operand = At(new IsExpr { Inner = notExpr.Operand, Type = ty }, ot);
                else
                    left = At(new IsExpr { Inner = left, Type = ty }, ot);
                continue;
            }
            if (_allowExpressionFunctions && CameoComparePriority > limit && ot.Kind == TokenKind.Op
                && (ot.Text == "=" || ot.Text == "!") && TokIsOp(PeekTok(), "~") && ot.End == PeekTok().Pos)
            {
                bool negate = ot.Text == "!";
                _p += 2;
                Expr right = ParseSubExpr(CameoComparePriority);
                left = LowerCameoRegexMatch(left, right, ot, negate);
                uop = null;
                continue;
            }
            if (!BinaryInfo(ot, out BinaryOp op, out int lp, out int rp) || lp <= limit) break;
            _p++;
            bool prevHoist = _allowTryHoist;
            if (op is BinaryOp.And or BinaryOp.Or or BinaryOp.Coalesce) _allowTryHoist = false;
            Expr rhs;
            try { rhs = ParseSubExpr(rp); }
            finally { _allowTryHoist = prevHoist; }
            left = At(new BinaryExpr { Op = op, Left = left, Right = rhs }, ot);
            uop = null;
        }
        return left;
    }

    private Expr ParseSimpleExpr()
    {
        Token t = Cur;
        Expr e;
        switch (t.Kind)
        {
            case TokenKind.Number:
                _p++;
                e = MakeNumber(t);
                break;
            case TokenKind.String:
                _p++;
                e = At(new StringExpr { Value = t.Text }, t);
                if (_allowExpressionFunctions && (IsOp(".") || IsOp(":") || IsOp("?."))) e = ParseSuffixedRest(e);
                break;
            case TokenKind.Interp:
                _p++;
                e = ParseInterpolated(t);
                if (_allowExpressionFunctions && (IsOp(".") || IsOp(":") || IsOp("?."))) e = ParseSuffixedRest(e);
                break;
            case TokenKind.Keyword when t.Text == "nil":
                _p++;
                e = At(new NilExpr(), t);
                break;
            case TokenKind.Keyword when t.Text == "true":
                _p++;
                e = At(new BoolExpr { Value = true }, t);
                break;
            case TokenKind.Keyword when t.Text == "false":
                _p++;
                e = At(new BoolExpr { Value = false }, t);
                break;
            case TokenKind.Keyword when t.Text == "function":
                if (_allowExpressionFunctions)
                    throw Error("use '(...) => ...' or '(...) => { ... }' instead of 'function(...) ... end' in Cameo", t);
                _p++;
                e = At(new FunctionExpr { Body = ParseFunctionBody(t, "anonymous", false) }, t);
                break;
            case TokenKind.Op when t.Text == "...":
                _p++;
                e = At(new VarargExpr(), t);
                break;
            case TokenKind.Op when t.Text == "{":
                e = ParseTable();
                break;
            default:
                e = ParseSuffixedExpr();
                break;
        }
        // `as` binds tighter than any operator
        while (IsName("as"))
        {
            Token at = _toks[_p++];
            var c = At(new CastExpr { Inner = e }, at);
            if (IsOp("("))
            {
                // either a parenthesized type or a tuple cast `as (A, B)`
                int save = _p;
                _p++;
                var types = new List<TypeNode>();
                if (!IsOp(")"))
                {
                    types.Add(ParseType());
                    while (AcceptOp(",")) types.Add(ParseType());
                }
                ExpectOp(")");
                if (types.Count == 1 && !IsOp("|"))
                {
                    c.Types = types;
                }
                else if (types.Count == 1)
                {
                    _p = save;
                    c.Types.Add(ParseType());
                }
                else
                {
                    c.Types = types;
                    c.IsTuple = true;
                }
            }
            else c.Types.Add(ParseType());
            e = c;
        }
        return e;
    }

    private Expr MakeNumber(Token t)
    {
        if (!LuaNumber.TryParse(t.Text, out Value v)) throw Error($"malformed number near '{t.Text}'", t);
        var n = At(new NumberExpr(), t);
        if (v.IsInteger) { n.IsInteger = true; n.IntValue = v.I; }
        else n.FloatValue = v.N;
        return n;
    }

    private Expr ParsePrimaryExpr()
    {
        if (AtExpressionFunction()) return ParseExpressionFunction();
        if (_allowExpressionFunctions && _allowTryHoist && _hoistedStats != null && AtCameoTry())
            return ParseCameoTryExpr();
        Token t = Cur;
        if (_allowExpressionFunctions && t.Kind == TokenKind.Regex)
        {
            _p++;
            return BuildCameoRegexLiteral(t);
        }
        if (_allowExpressionFunctions && IsOp("["))
            return ParseCameoListExpr();
        if (AtCameoMatch())
            return ParseCameoMatch(isStatement: false);
        if (t.Kind == TokenKind.Name)
        {
            _p++;
            if (IsOp("!") && t.End == Cur.Pos && !TokIsOp(PeekTok(), "~")) return ParseMacroCall(t);
            return At(new NameExpr { Name = t.Text }, t);
        }
        if (IsOp("("))
        {
            _p++;
            Expr inner = ParseExpr();
            ExpectClose(")", "(", t);
            return At(new ParenExpr { Inner = inner }, t);
        }
        if (t.Kind == TokenKind.MacroQuote) return ParseMacroQuote();
        if (_allowMacroVars && IsOp("$")) return ParseMacroVarExpr();
        throw Error($"unexpected '{t}'");
    }

    private Expr ParseSuffixedExpr() => ParseSuffixedRest(ParsePrimaryExpr());

    private Expr ParseSuffixedRest(Expr e)
    {
        while (true)
        {
            Token t = Cur;
            if (t.Kind == TokenKind.String && (!_allowExpressionFunctions || (_p > 0 && _toks[_p - 1].Line == t.Line)))
            {
                _p++;
                var call = At(new CallExpr { Callee = e, CameoDotCall = _allowExpressionFunctions && e is IndexExpr { IsDot: true } }, t);
                call.Args.Add(At(new StringExpr { Value = t.Text }, t));
                e = call;
                continue;
            }
            if (t.Kind != TokenKind.Op) break;
            switch (t.Text)
            {
                case ".":
                    {
                        _p++;
                        Token n = Cur;
                        if (_allowMacroVars && IsOp("$"))
                        {
                            e = At(new IndexExpr { Object = e, Key = ParseMacroVarExpr(), IsDot = true }, t);
                            continue;
                        }
                        if (n.Kind != TokenKind.Name && n.Kind != TokenKind.Keyword) throw Error($"name expected near '{Cur}'");
                        _p++;
                        e = At(new IndexExpr { Object = e, Key = At(new StringExpr { Value = n.Text }, n), IsDot = true }, t);
                        continue;
                    }
                case "?." when _allowExpressionFunctions:
                    {
                        _p++;
                        if (IsOp("["))
                        {
                            Token open = _toks[_p++];
                            Expr key = ParseExpr();
                            ExpectClose("]", "[", open);
                            e = At(new IndexExpr { Object = e, Key = key, IsOptional = true }, t);
                            continue;
                        }
                        if (IsOp("("))
                        {
                            var optCall = At(new CallExpr { Callee = e, IsOptional = true }, t);
                            optCall.Args = ParseArgs();
                            e = optCall;
                            continue;
                        }
                        Token n = Cur;
                        if (n.Kind != TokenKind.Name && n.Kind != TokenKind.Keyword) throw Error($"name, '[' or '(' expected after '?.' near '{Cur}'");
                        _p++;
                        e = At(new IndexExpr { Object = e, Key = At(new StringExpr { Value = n.Text }, n), IsDot = true, IsOptional = true }, t);
                        continue;
                    }
                case "[":
                    {
                        _p++;
                        Expr key = ParseExpr();
                        ExpectClose("]", "[", t);
                        e = At(new IndexExpr { Object = e, Key = key }, t);
                        continue;
                    }
                case ":":
                    {
                        _p++;
                        Token n = ExpectName();
                        var mc = At(new MethodCallExpr { Object = e, Name = n.Text }, n);
                        mc.Args = ParseArgs();
                        e = mc;
                        continue;
                    }
                case "(":
                    {
                        // As in tl, a '(' at the start of a new line begins a new statement
                        // rather than continuing the previous expression as a call.
                        if (_p > 0 && _toks[_p - 1].Line != t.Line) return e;
                        var call = At(new CallExpr
                        {
                            Callee = e,
                            CameoDotCall = _allowExpressionFunctions && e is IndexExpr { IsDot: true },
                            IsOptional = e is IndexExpr { IsOptional: true },
                        }, t);
                        call.Args = call.CameoDotCall && e is IndexExpr { Key: StringExpr { Value: "await" } }
                            && PeekTok().Text == "timeout" && TokIsName(PeekTok()) && TokIsOp(PeekTok(2), ":")
                            ? ParseCameoAwaitArgs()
                            : ParseArgs();
                        e = call;
                        continue;
                    }
                case "{":
                    {
                        if (AtControlHeaderTop()) return e;
                        var call = At(new CallExpr { Callee = e, CameoDotCall = _allowExpressionFunctions && e is IndexExpr { IsDot: true } }, t);
                        call.Args.Add(ParseTable());
                        e = call;
                        continue;
                    }
            }
            break;
        }
        return e;
    }

    // Contextual labels lower in source order to the shared Future.await ABI.
    // Parenthesize each option internally so a final call cannot expand extra results.
    private List<Expr> ParseCameoAwaitArgs()
    {
        Token open = Cur;
        ExpectOp("(");
        Token timeout = ExpectName();
        ExpectOp(":");
        var args = new List<Expr> { At(new ParenExpr { Inner = ParseExpr() }, timeout) };
        if (AcceptOp(","))
        {
            Token cancel = Cur;
            if (!TokIsName(cancel) || cancel.Text != "cancelOnTimeout")
                throw Error("expected 'cancelOnTimeout:' after the timeout option");
            _p++;
            ExpectOp(":");
            args.Add(At(new ParenExpr { Inner = ParseExpr() }, cancel));
        }
        ExpectClose(")", "(", open);
        return args;
    }

    private List<Expr> ParseArgs()
    {
        Token t = Cur;
        if (t.Kind == TokenKind.String)
        {
            _p++;
            return new List<Expr> { At(new StringExpr { Value = t.Text }, t) };
        }
        if (IsOp("{") && !AtControlHeaderTop()) return new List<Expr> { ParseTable() };
        ExpectOp("(");
        var args = new List<Expr>();
        if (!IsOp(")")) args = ParseExprList();
        ExpectClose(")", "(", t);
        return args;
    }

    private Expr ParseTable()
    {
        Token open = ExpectOp("{");
        var tbl = At(new TableExpr(), open);
        while (!IsOp("}"))
        {
            Token t = Cur;
            var item = At(new TableItem(), t);
            if (IsOp("["))
            {
                _p++;
                item.Kind = TableItemKind.Keyed;
                item.Key = ParseExpr();
                ExpectClose("]", "[", t);
                ExpectOp("=");
                item.Value = ParseExpr();
            }
            else if (t.Kind == TokenKind.Name && TokIsOp(PeekTok(), "="))
            {
                _p += 2;
                item.Kind = TableItemKind.Named;
                item.Key = At(new StringExpr { Value = t.Text }, t);
                item.Value = ParseExpr();
            }
            else if (t.Kind == TokenKind.Name && TokIsOp(PeekTok(), ":") && TryParseTypedField(item))
            {
                // handled: `name: Type = value`
            }
            else
            {
                item.Kind = TableItemKind.Positional;
                item.Value = ParseExpr();
            }
            tbl.Items.Add(item);
            if (!AcceptOp(",") && !AcceptOp(";")) break;
        }
        ExpectClose("}", "{", open);
        return tbl;
    }

    // `name: Type = value` inside a table constructor; backtracks if it is really `obj:method(...)`.
    private bool TryParseTypedField(TableItem item)
    {
        int save = _p;
        Token name = Cur;
        try
        {
            _p += 2;
            TypeNode ty = ParseType();
            if (!IsOp("=")) { _p = save; return false; }
            _p++;
            item.Kind = TableItemKind.Named;
            item.Key = At(new StringExpr { Value = name.Text }, name);
            item.DeclaredType = ty;
        }
        catch (CameoSyntaxException)
        {
            _p = save;
            return false;
        }
        item.Value = ParseExpr();
        return true;
    }

    // ------------------------------------------------------------------ types

    private List<TypeParam> ParseTypeParams()
    {
        ExpectOp("<");
        var list = new List<TypeParam>();
        do
        {
            Token n = ExpectName();
            var tp = At(new TypeParam { Name = n.Text }, n);
            if (IsName("is"))
            {
                _p++;
                tp.Constraint = ParseType();
            }
            list.Add(tp);
        } while (AcceptOp(","));
        ExpectTypeClose();
        return list;
    }

    // Consumes a '>' that closes a type argument list, splitting '>>' and '>=' tokens when needed.
    private void ExpectTypeClose()
    {
        Token t = Cur;
        if (t.Kind == TokenKind.Op)
        {
            if (t.Text == ">") { _p++; return; }
            if (t.Text == ">>" || t.Text == ">=" || t.Text == ">>=")
            {
                // split: leave the second character as its own token
                string rest = t.Text.Substring(1);
                PrepareTokenMutation();
                _toks[_p] = new Token { Kind = TokenKind.Op, Text = rest, Line = t.Line, Col = t.Col + 1, Pos = t.Pos + 1, End = t.End };
                return;
            }
        }
        throw Error($"'>' expected near '{Cur}'");
    }

    private List<TypeNode> ParseTypeList()
    {
        var list = new List<TypeNode> { ParseType() };
        while (IsOp(",") && !CommaStartsNewParam())
        {
            _p++;
            list.Add(ParseType());
        }
        return list;
    }

    // In `function(cb: function(): A, x: B)` the comma after A separates parameters, not return types.
    private bool CommaStartsNewParam()
    {
        Token a = PeekTok(1), b = PeekTok(2), c = PeekTok(3);
        if (a.Kind == TokenKind.Name && TokIsOp(b, ":")) return true;
        if (a.Kind == TokenKind.Name && TokIsOp(b, "?") && TokIsOp(c, ":")) return true;
        if (TokIsOp(a, "...") && TokIsOp(b, ":")) return true;
        if (TokIsOp(a, "?")) return true;
        return false;
    }

    public TypeNode ParseType()
    {
        Token t = Cur;
        TypeNode first = ParseBaseType();
        if (!IsOp("|")) return first;
        var u = At(new UnionTypeNode(), t);
        u.Members.Add(first);
        while (AcceptOp("|")) u.Members.Add(ParseBaseType());
        return u;
    }

    private TypeNode ParseBaseType()
    {
        EnterLevel();
        try { return ParseBaseTypeCore(); }
        finally { _levels--; }
    }

    private TypeNode ParseBaseTypeCore()
    {
        Token t = Cur;
        if (t.Kind == TokenKind.Op)
        {
            if (t.Text == "$" && _allowMacroVars) return ParseMacroVarType();
            if (t.Text == "(")
            {
                _p++;
                TypeNode inner = ParseType();
                ExpectClose(")", "(", t);
                return inner;
            }
            if (t.Text == "{")
            {
                _p++;
                TypeNode first = ParseType();
                if (AcceptOp(":"))
                {
                    TypeNode val = ParseType();
                    ExpectClose("}", "{", t);
                    return At(new MapTypeNode { Key = first, Value = val }, t);
                }
                if (IsOp(","))
                {
                    var tup = At(new TupleTypeNode(), t);
                    tup.Elements.Add(first);
                    while (AcceptOp(",")) tup.Elements.Add(ParseType());
                    ExpectClose("}", "{", t);
                    return tup;
                }
                ExpectClose("}", "{", t);
                return At(new ArrayTypeNode { Element = first }, t);
            }
        }
        else if (t.Kind == TokenKind.Keyword)
        {
            if (t.Text == "function") return ParseFunctionType();
            if (t.Text == "nil")
            {
                _p++;
                var n = At(new NameTypeNode(), t);
                n.Names.Add("nil");
                return n;
            }
        }
        else if (t.Kind == TokenKind.Name)
        {
            if (_allowExpressionFunctions && t.Text == "fn" && (TokIsOp(PeekTok(), "(") || TokIsOp(PeekTok(), "<")))
                return ParseFunctionType(cameoFn: true);
            _p++;
            var n = At(new NameTypeNode(), t);
            n.Names.Add(t.Text);
            while (IsOp(".") && TokIsName(PeekTok()))
            {
                _p++;
                n.Names.Add(ExpectName().Text);
            }
            if (IsOp("<")) TryParseTypeArgs(n);
            return n;
        }
        throw Error($"type expected near '{t}'");
    }

    // Type arguments after a type name. Backtracks when '<' turns out to be a comparison.
    private void TryParseTypeArgs(NameTypeNode n)
    {
        int save = _p;
        Token saved = _toks[_p];
        try
        {
            _p++;
            var args = new List<TypeNode> { ParseTypeArg() };
            while (AcceptOp(",")) args.Add(ParseTypeArg());
            ExpectTypeClose();
            n.TypeArgs = args;
        }
        catch (CameoSyntaxException)
        {
            _p = save;
            PrepareTokenMutation();
            _toks[_p] = saved;
        }
    }

    private TypeNode ParseTypeArg()
    {
        if (Cur.Kind == TokenKind.String)
        {
            Token st = _toks[_p++];
            return At(new StringLitTypeNode { Value = st.Text }, st);
        }
        return ParseType();
    }

    private TypeNode ParseFunctionType(bool cameoFn = false)
    {
        Token t = cameoFn ? _toks[_p++] : ExpectKw("function");
        var f = At(new FunctionTypeNode(), t);
        if (IsOp("<")) f.TypeParams = ParseTypeParams();
        if (!IsOp("("))
        {
            f.IsBare = true;
            return f;
        }
        Token open = ExpectOp("(");
        if (!IsOp(")"))
        {
            do
            {
                if (IsOp("..."))
                {
                    _p++;
                    if (Cur.Kind == TokenKind.Name) _p++; // named variadic parameter: the name is documentation
                    ExpectOp(":");
                    f.VarArg = ParseType();
                    break;
                }
                var prm = new ParamTypeNode();
                if (Cur.Kind == TokenKind.Name && TokIsOp(PeekTok(), ":"))
                {
                    prm.Name = Cur.Text;
                    _p += 2;
                }
                else if (Cur.Kind == TokenKind.Name && TokIsOp(PeekTok(), "?") && TokIsOp(PeekTok(2), ":"))
                {
                    prm.Name = Cur.Text;
                    prm.Optional = true;
                    _p += 3;
                }
                else if (AcceptOp("?")) prm.Optional = true;
                prm.Type = ParseType();
                if (IsOp("..."))
                {
                    _p++;
                    f.VarArg = prm.Type;
                    break;
                }
                f.Params.Add(prm);
            } while (AcceptOp(","));
        }
        ExpectClose(")", "(", open);
        if (cameoFn ? AcceptCameoReturnArrow() : (AcceptOp(":") || (_allowExpressionFunctions && AcceptCameoReturnArrow())))
            ParseReturnTypes(f.Returns, out f.ReturnsVarArg);
        return f;
    }

    // retlist ::= '(' [typelist] ['...'] ')' | typelist ['...']
    private void ParseReturnTypes(List<TypeNode> returns, out bool varArg)
    {
        varArg = false;
        if (IsOp("("))
        {
            Token open = Cur;
            int save = _p;
            _p++;
            if (AcceptOp(")")) return;
            returns.Add(ParseType());
            while (AcceptOp(",")) returns.Add(ParseType());
            if (AcceptOp("...")) varArg = true;
            ExpectClose(")", "(", open);
            if (returns.Count == 1 && !varArg && IsOp("|"))
            {
                // It was a parenthesized type that starts a union: `(A) | B`
                returns.Clear();
                _p = save;
            }
            else return;
        }
        returns.Add(ParseType());
        while (IsOp(",") && !CommaStartsNewParam())
        {
            _p++;
            returns.Add(ParseType());
        }
        if (AcceptOp("...")) varArg = true;
    }

    private TypeNode ParseNewType(Token nameTok)
    {
        Token t = Cur;
        if (t.Kind == TokenKind.Name)
        {
            // `record`/`interface`/`enum` start a body unless they are type names in their own right.
            Token nx = PeekTok();
            bool bodyFollows = !(TokIsOp(nx, ".") || TokIsOp(nx, "|"));
            if ((t.Text == "record" || t.Text == "interface") && bodyFollows)
            {
                _p++;
                return ParseRecordBody(t, t.Text == "interface", braced: _allowExpressionFunctions);
            }
            if (t.Text == "enum" && bodyFollows)
            {
                _p++;
                return ParseEnumBody(t, braced: _allowExpressionFunctions);
            }
            if (t.Text == "require" && TokIsOp(nx, "("))
            {
                _p += 2;
                if (Cur.Kind != TokenKind.String) throw Error("module name string expected");
                var r = At(new RequireTypeNode { Module = Cur.Text }, t);
                _p++;
                ExpectOp(")");
                while (AcceptOp(".")) r.Path.Add(ExpectName().Text);
                return r;
            }
        }
        return ParseType();
    }

    private EnumTypeNode ParseEnumBody(Token start, bool braced = false)
    {
        var e = At(new EnumTypeNode(), start);
        Token? open = braced ? ExpectOp("{") : null;
        while (Cur.Kind == TokenKind.String)
        {
            e.Values.Add(Cur.Text);
            _p++;
            if (braced) AcceptOp(",");
        }
        if (braced) ExpectClose("}", "{", open!);
        else ExpectClose("end", "enum", start);
        return e;
    }

    private RecordTypeNode ParseRecordBody(Token start, bool isInterface, bool braced = false)
    {
        var r = At(new RecordTypeNode { IsInterface = isInterface }, start);
        if (ClassRanges(this).TryGetValue(start, out var classInfo))
        {
            r.ClassRange = classInfo.Range; r.ClassDefines = classInfo.Defines;
            r.ClassAbstract = classInfo.Abstract; r.ClassReadOnly = classInfo.ReadOnly;
            // `value is Name` asks the object at run time whether it is of this class
            r.WhereSource = $"__isa(self, \"{classInfo.Id}\")";
            r.Where = ParseExpression(r.WhereSource, _file);
        }
        if (IsOp("<")) r.TypeParams = ParseTypeParams();
        while (IsName("is") && !TokIsOp(PeekTok(), ":")
            || (_allowExpressionFunctions && braced && !isInterface && IsName("implements") && !TokIsOp(PeekTok(), ":"))
            || (_allowExpressionFunctions && braced && isInterface && IsName("extends") && !TokIsOp(PeekTok(), ":")))
        {
            bool isImplements = IsName("implements");
            if (isImplements) r.CheckCameoImplements = true;
            _p++;
            do
            {
                if (IsOp("{") && !isImplements)
                {
                    Token ot = _toks[_p++];
                    if (r.ArrayElement != null) throw Error("duplicated declaration of array element type", ot);
                    r.ArrayElement = ParseArrayPart(ot);
                }
                else
                {
                    TypeNode it = ParseBaseType();
                    if (it is not NameTypeNode) throw Error("interface name expected");
                    r.Interfaces.Add(it);
                }
            } while (AcceptOp(","));
        }
        if (IsName("where") && !TokIsOp(PeekTok(), ":"))
        {
            _expressionState.LegacyReparse = true;
            _p++;
            int startPos = Cur.Pos;
            int whereLine = Cur.Line;
            r.Where = braced ? ParseBraceHeaderExpr() : ParseExpr();
            int endPos = _toks[_p - 1].End;
            // pad with newlines so re-parsing reports the original line
            r.WhereSource = new string('\n', whereLine - 1) + _src.Substring(startPos, endPos - startPos);
        }
        Token? brace = braced ? ExpectOp("{") : null;
        if (_allowExpressionFunctions && braced && !isInterface) r.IsCameoRecord = true;
        while (!(braced ? IsOp("}") : IsKw("end")))
        {
            if (braced && (AcceptOp(",") || AcceptOp(";"))) continue;
            Token t = Cur;
            if (t.Kind == TokenKind.Eof)
                throw braced
                    ? Error($"'}}' expected (to close '{{' at line {brace!.Line})")
                    : Error($"'end' expected (to close '{(isInterface ? "interface" : "record")}' at line {start.Line})");
            if (t.Kind == TokenKind.Op && t.Text == "{")
            {
                _p++;
                if (r.ArrayElement != null) throw Error("duplicated declaration of array element type", t);
                r.ArrayElement = ParseArrayPart(t);
                continue;
            }
            if (t.Kind == TokenKind.Op && t.Text == "[")
            {
                _p++;
                if (Cur.Kind != TokenKind.String) throw Error("string literal expected as record key");
                string key = Cur.Text;
                _p++;
                ExpectOp("]");
                bool optKey = _allowExpressionFunctions && braced && AcceptOp("?");
                ExpectOp(":");
                RecordField kf = ParseFieldRest(t, key, false);
                if (optKey) MakeOptionalField(kf, t);
                r.Fields.Add(kf);
                continue;
            }
            if (t.Kind == TokenKind.Name)
            {
                Token nx = PeekTok();
                bool optionalField = _allowExpressionFunctions && braced && TokIsOp(nx, "?") && TokIsOp(PeekTok(2), ":");
                bool fieldFollows = TokIsOp(nx, ":") || optionalField;
                if (!fieldFollows)
                {
                    if (_allowExpressionFunctions && braced && t.Text == "fn" && TokIsName(nx))
                    {
                        if (isInterface) ParseCameoInterfaceMethod(r);
                        else ParseCameoRecordMethod(r, start.Text, isStatic: false);
                        continue;
                    }
                    if (_allowExpressionFunctions && braced && !isInterface && t.Text == "static"
                        && TokIsName(nx) && nx.Text == "fn" && TokIsName(PeekTok(2)))
                    {
                        _p++; // static
                        ParseCameoRecordMethod(r, start.Text, isStatic: true);
                        continue;
                    }
                    if (t.Text == "userdata")
                    {
                        _p++;
                        r.IsUserdata = true;
                        continue;
                    }
                    if (t.Text == "type" && TokIsName(nx) && TokIsOp(PeekTok(2), "="))
                    {
                        _p += 3;
                        r.NestedTypes.Add(At(new NestedTypeDecl { Name = nx.Text, Def = ParseNewType(nx) }, t));
                        continue;
                    }
                    if ((t.Text == "record" || t.Text == "interface") && TokIsName(nx))
                    {
                        _p += 2;
                        r.NestedTypes.Add(At(new NestedTypeDecl { Name = nx.Text, Def = ParseRecordBody(nx, t.Text == "interface", braced: braced && _allowExpressionFunctions) }, t));
                        continue;
                    }
                    if (t.Text == "enum" && TokIsName(nx))
                    {
                        _p += 2;
                        r.NestedTypes.Add(At(new NestedTypeDecl { Name = nx.Text, Def = ParseEnumBody(nx, braced: braced && _allowExpressionFunctions) }, t));
                        continue;
                    }
                    if (t.Text is "private" or "protected" && (nx.Kind is TokenKind.Name or TokenKind.Keyword) && TokIsOp(PeekTok(2), ":"))
                    {
                        // a member only the declaring class (private) or it and its subclasses (protected) may use
                        _p += 3;
                        RecordField hidden = ParseFieldRest(nx, nx.Text, false);
                        hidden.Visibility = t.Text == "private" ? 2 : 1;
                        r.Fields.Add(hidden);
                        continue;
                    }
                    if (t.Text == "metamethod" && TokIsName(nx))
                    {
                        _p += 2;
                        ExpectOp(":");
                        r.Fields.Add(ParseFieldRest(nx, nx.Text, true));
                        continue;
                    }
                    throw Error($"syntax error in record body near '{nx}'", nx);
                }
                _p += optionalField ? 3 : 2;
                RecordField rf = ParseFieldRest(t, t.Text, false);
                if (optionalField) MakeOptionalField(rf, t);
                r.Fields.Add(rf);
                continue;
            }
            if (t.Kind == TokenKind.Keyword && TokIsOp(PeekTok(), ":"))
            {
                // Lua keywords may be used as field names in declarations.
                _p += 2;
                r.Fields.Add(ParseFieldRest(t, t.Text, false));
                continue;
            }
            throw Error($"syntax error in record body near '{t}'");
        }
        if (braced) ExpectClose("}", "{", brace!);
        else ExpectClose("end", isInterface ? "interface" : "record", start);
        return r;
    }

    // After '{' in a record's array declaration: `{T}` (array) or `{A, B}` (tuple).
    private TypeNode ParseArrayPart(Token open)
    {
        TypeNode first = ParseType();
        if (!IsOp(","))
        {
            ExpectClose("}", "{", open);
            return first;
        }
        var tup = At(new TupleTypeNode(), open);
        tup.Elements.Add(first);
        while (AcceptOp(",")) tup.Elements.Add(ParseType());
        ExpectClose("}", "{", open);
        return tup;
    }

    private RecordField ParseFieldRest(Token nameTok, string name, bool isMeta)
    {
        var f = At(new RecordField { Name = name, IsMetamethod = isMeta }, nameTok);
        f.Type = ParseType();
        if (IsOp("=") && TokIsName(PeekTok()) && PeekTok().Text == "macroexp")
        {
            _p += 2;
            f.Macro = ParseMacroExp(nameTok, name);
        }
        return f;
    }
}
