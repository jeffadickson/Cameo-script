namespace Cameo.Runtime.Syntax;

public sealed partial class Parser
{
    // Contextual lookahead preserves calls, assignments and fields named let in both languages.
    private bool AtCameoLet() => _allowExpressionFunctions && IsName("let")
        && (TokIsName(PeekTok()) || TokIsOp(PeekTok(), "{") || TokIsOp(PeekTok(), "[")
            || (_allowMacroVars && TokIsOp(PeekTok(), "$")));

    private bool AtCameoVar() => _allowExpressionFunctions && IsName("var")
        && (TokIsName(PeekTok()) || TokIsOp(PeekTok(), "{") || TokIsOp(PeekTok(), "["));

    private Stat ParseCameoLet() => ParseCameoLetSequence(allowTry: false)[0];

    private Expr LowerCameoPipe(Expr lhs, Expr stage, Token pipe)
    {
        if (stage is CallExpr call)
        {
            call.Args.Insert(0, lhs);
            return call;
        }
        if (stage is MethodCallExpr mc)
        {
            mc.Args.Insert(0, lhs);
            return mc;
        }
        return At(new CallExpr
        {
            Callee = stage,
            Args = [lhs],
            CameoDotCall = stage is IndexExpr { IsDot: true }
        }, pipe);
    }

    // Hoisted statements for expression-position `try` inside the current block statement.
    private List<Stat>? _hoistedStats;
    private bool _allowTryHoist;

    private bool AtCameoTry() => IsName("try")
        && (TokIsName(PeekTok()) || TokIsOp(PeekTok(), "("))
        && PeekTok().Line == Cur.Line;

    private List<Stat> ParseCameoLetSequence(bool allowTry = true)
    {
        Token start = _toks[_p++];
        if (IsOp("{") || IsOp("[")) return ParseCameoDestructure(start, isConst: true);
        var declaration = At(new LocalStat(), start);
        ParseAttribNames(declaration.Names);
        foreach (AttribName name in declaration.Names)
        {
            if (name.Attrib != null) throw Error("let bindings do not accept local attributes", start);
            name.Attrib = "const";
            name.IsCameoLet = true;
        }
        if (AcceptOp(":")) declaration.Types = ParseTypeList();
        if (!AcceptOp("=")) throw Error("let binding requires an initializer ('= expression')", start);
        if (allowTry && AtCameoTry())
            return LowerCameoTry(declaration, _toks[_p++]);
        declaration.Values = ParseExprList();
        return [declaration];
    }

    private List<Stat> ParseCameoDestructure(Token start, bool isConst)
    {
        bool isArray = IsOp("[");
        Token open = ExpectOp(isArray ? "[" : "{");
        var specs = new List<(string? Field, int Index, Token Binding)>();
        int idx = 1;
        while (!IsOp(isArray ? "]" : "}"))
        {
            Token fieldOrName = ExpectName();
            if (isArray)
            {
                if (fieldOrName.Text != "_") specs.Add((null, idx, fieldOrName));
                idx++;
            }
            else
            {
                Token binding = fieldOrName;
                if (AcceptOp("=") || AcceptOp(":")) binding = ExpectName();
                specs.Add((fieldOrName.Text, 0, binding));
            }
            if (!AcceptOp(",") && !AcceptOp(";")) break;
        }
        ExpectClose(isArray ? "]" : "}", isArray ? "[" : "{", open);
        if (!AcceptOp("=")) throw Error("destructuring binding requires an initializer ('= expression')", start);
        Expr rhs = ParseExpr();
        string tmpName = "\0destr-" + start.Pos;
        var tmp = At(new LocalStat
        {
            Names = [At(new AttribName { Name = tmpName }, start)],
            Values = [At(new ParenExpr { Inner = rhs }, start)],
        }, start);
        var stats = new List<Stat>(specs.Count + 1) { tmp };
        foreach (var (field, index, binding) in specs)
        {
            Expr key = field != null
                ? At(new StringExpr { Value = field }, binding)
                : At(new NumberExpr { IsInteger = true, IntValue = index }, binding);
            var attr = At(new AttribName { Name = binding.Text, Attrib = isConst ? "const" : null, IsCameoLet = isConst }, binding);
            stats.Add(At(new LocalStat
            {
                Names = [attr],
                Values = [At(new IndexExpr { Object = At(new NameExpr { Name = tmpName }, binding), Key = key, IsDot = field != null }, binding)],
            }, binding));
        }
        return stats;
    }

    // Parses an expression-position `try expr [catch ...]` by hoisting the lowering statements
    // before the enclosing statement and returning the payload temporary.
    private Expr ParseCameoTryExpr()
    {
        Token start = _toks[_p++];
        string payload = "\0try-expr-" + start.Pos;
        var stats = LowerCameoTryCore(start, payload);
        _hoistedStats!.AddRange(stats);
        return At(new NameExpr { Name = payload }, start);
    }

    // Lower before resolution, so propagation uses the same return and resource
    // cleanup path as source returns. NUL cannot appear in a source identifier.
    private List<Stat> LowerCameoTry(LocalStat declaration, Token start)
    {
        if (declaration.Names.Count != 1 || declaration.Types is { Count: not 1 })
            throw Error("try requires one binding", start);
        string payload = "\0try-value-" + start.Pos;
        List<Stat> stats = LowerCameoTryCore(start, payload);
        declaration.Values = [At(new NameExpr { Name = payload }, start)];
        stats.Add(declaration);
        return stats;
    }

    private Block ParseCameoCatchBody(Token open, Token? binding, Func<string, Expr> inspect, Func<Expr, AssignStat> setPayload)
    {
        var block = At(new Block(), open);
        if (binding != null)
        {
            block.Stats.Add(At(new LocalStat
            {
                Names = [At(new AttribName { Name = binding.Text, Attrib = "const", IsCameoLet = true }, binding)],
                Values = [inspect("issue")],
            }, binding));
        }
        while (!IsOp("}") && Cur.Kind != TokenKind.Eof)
        {
            if (IsKw("return"))
            {
                block.Stats.Add(ParseReturn());
                break;
            }
            if (AtCameoLet())
            {
                EnterLevel();
                try { block.Stats.AddRange(ParseCameoLetSequence()); }
                finally { _levels--; }
                continue;
            }
            if (AtCameoVar())
            {
                EnterLevel();
                try { block.Stats.AddRange(ParseCameoVarSequence()); }
                finally { _levels--; }
                continue;
            }
            if (AtCameoDeclaration("using") && (TokIsOp(PeekTok(2), "=") || TokIsOp(PeekTok(2), ":")))
            {
                block.Stats.Add(ParseCameoUsing());
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
            if (IsKw("if") || IsKw("while") || IsKw("for") || IsKw("do") || IsKw("repeat") || IsKw("local")
                || AtCameoDeclaration("fn")
                || (AtCameoDeclaration("enum") && TokIsOp(PeekTok(2), "{"))
                || IsOp(";"))
            {
                Stat? st = ParseStatement();
                if (st != null) block.Stats.Add(st);
                continue;
            }
            // Could be a trailing recovery expression, an assignment statement, or a call statement.
            int saved = _p;
            Expr expr = ParseExpr();
            AcceptOp(";");
            if (IsOp("}"))
            {
                block.Stats.Add(setPayload(expr));
                break;
            }
            // Assignment or compound assignment statement inside catch block
            if (IsOp("=") || IsOp(",") || (Cur.Kind == TokenKind.Op && CompoundOp(Cur.Text, out _)))
            {
                _p = saved;
                Stat? st = ParseStatement();
                if (st != null) block.Stats.Add(st);
                continue;
            }
            if (expr is CallExpr or MethodCallExpr)
            {
                block.Stats.Add(At(new CallStat { Call = expr }, open));
                continue;
            }
            throw Error("expected '}' after catch recovery expression");
        }
        ExpectClose("}", "{", open);
        return block;
    }

    private List<Stat> LowerCameoTryCore(Token start, string payload)
    {
        if (_allowMacroVars) throw Error("try is unavailable inside macro quotes", start);
        string capture = "\0try-result-" + start.Pos;
        NameExpr Name(string name) => At(new NameExpr { Name = name }, start);
        Expr Inspect(string method) => At(new MethodCallExpr { Object = Name(capture), Name = method }, start);
        Block Body(Stat statement) => At(new Block { Stats = [statement] }, start);
        LocalStat Temp(string name) => At(new LocalStat { Names = [At(new AttribName { Name = name }, start)] }, start);
        AssignStat SetPayload(Expr value) => At(new AssignStat { Targets = [Name(payload)], Values = [value] }, start);

        LocalStat operand = Temp(capture);
        operand.Values.Add(At(new ParenExpr { Inner = ParseExpr() }, start));
        operand.CameoTryOperand = true;
        LocalStat valueSlot = Temp(payload);
        valueSlot.CameoTryPayloadOf = capture;
        var branch = At(new IfStat(), start);
        branch.Clauses.Add((Inspect("isOk"), Body(SetPayload(Inspect("value")))));
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool hasCatchAll = false;
        while (IsName("catch"))
        {
            if (TokIsOp(PeekTok(), "(") && TokIsName(PeekTok(2)) && TokIsOp(PeekTok(3), ")") && TokIsOp(PeekTok(4), "{"))
            {
                if (hasCatchAll) throw Error("duplicate catch-all clause", Cur);
                _p++; // catch
                _p++; // (
                Token errName = ExpectName();
                ExpectOp(")");
                Token open = ExpectOp("{");
                branch.Else = ParseCameoCatchBody(open, errName, Inspect, SetPayload);
                hasCatchAll = true;
                continue;
            }
            if (TokIsName(PeekTok()) && PeekTok().Text == "_" && TokIsOp(PeekTok(2), "{"))
            {
                if (hasCatchAll) throw Error("duplicate catch-all clause", Cur);
                _p++; // catch
                _p++; // _
                Token open = ExpectOp("{");
                branch.Else = ParseCameoCatchBody(open, null, Inspect, SetPayload);
                hasCatchAll = true;
                continue;
            }
            bool namedPlain = TokIsName(PeekTok()) && TokIsOp(PeekTok(2), "{");
            bool namedBound = TokIsName(PeekTok()) && TokIsOp(PeekTok(2), "(") && TokIsName(PeekTok(3)) && TokIsOp(PeekTok(4), ")") && TokIsOp(PeekTok(5), "{");
            if (!namedPlain && !namedBound) break;
            if (hasCatchAll) throw Error("catch-all clause must be last", Cur);
            _p++; // catch
            Token name = ExpectName();
            if (!seen.Add(name.Text)) throw Error($"duplicate catch case '{name.Text}'", name);
            Token? errBinding = null;
            if (namedBound)
            {
                ExpectOp("(");
                errBinding = ExpectName();
                ExpectOp(")");
            }
            Token openBrace = ExpectOp("{");
            Block recoveryBlock = ParseCameoCatchBody(openBrace, errBinding, Inspect, SetPayload);
            var kind = At(new IndexExpr { Object = Inspect("issue"), Key = At(new StringExpr { Value = "kind" }, name), IsDot = true }, name);
            var label = At(new StringExpr { Value = name.Text }, name);
            (operand.CameoCatchCases ??= []).Add(label);
            branch.Clauses.Add((At(new BinaryExpr { Left = kind, Op = BinaryOp.Eq, Right = label }, name), recoveryBlock));
        }
        if (!hasCatchAll)
            branch.Else = Body(At(new ReturnStat { CameoErrorPropagation = true, CameoTrySource = operand, Values = [Name(capture)] }, start));
        return [operand, valueSlot, branch];
    }

    private Stat ParseCameoEnum(bool isGlobal = false)
    {
        Token start = _toks[_p++]; // enum
        Token name = ExpectName();
        Token open = ExpectOp("{");
        var def = At(new EnumTypeNode(), name);
        while (Cur.Kind == TokenKind.String)
        {
            def.Values.Add(Cur.Text);
            _p++;
            AcceptOp(",");
        }
        ExpectClose("}", "{", open);
        return At(new TypeDeclStat
        {
            IsGlobal = isGlobal,
            Name = At(new AttribName { Name = name.Text }, name),
            Kind = TypeDeclKind.Enum,
            Def = def,
        }, start);
    }

    private bool AtCameoTypeAlias()
    {
        if (!_allowExpressionFunctions || !IsName("type") || !TokIsName(PeekTok())) return false;
        Token after = PeekTok(2);
        return TokIsOp(after, "=") || TokIsOp(after, "<");
    }

    private Stat ParseCameoTypeAlias(bool isGlobal)
    {
        Token start = _toks[_p++]; // type
        Token name = ExpectName();
        var s = At(new TypeDeclStat
        {
            IsGlobal = isGlobal,
            Kind = TypeDeclKind.Alias,
            Name = At(new AttribName { Name = name.Text }, name),
        }, start);
        if (IsOp("<")) s.TypeParams = ParseTypeParams();
        ExpectOp("=");
        s.Def = ParseNewType(name);
        if (s.Def is RecordTypeNode r)
        {
            s.Kind = r.IsInterface ? TypeDeclKind.Interface : TypeDeclKind.Record;
            r.TypeParams ??= s.TypeParams;
        }
        else if (s.Def is EnumTypeNode) s.Kind = TypeDeclKind.Enum;
        return s;
    }

    private Expr BuildCameoRegexLiteral(Token regexTok)
    {
        string patternStr = regexTok.Text;
        string flagsStr = regexTok.Flags ?? "";
        var regexNew = At(new CallExpr
        {
            Callee = At(new IndexExpr
            {
                Object = At(new NameExpr { Name = "regex" }, regexTok),
                Key = At(new StringExpr { Value = "new" }, regexTok),
                IsDot = true,
            }, regexTok),
            Args = flagsStr.Length > 0
                ? [At(new StringExpr { Value = patternStr }, regexTok), At(new StringExpr { Value = flagsStr }, regexTok)]
                : [At(new StringExpr { Value = patternStr }, regexTok)],
        }, regexTok);
        return At(new CallExpr
        {
            Callee = At(new NameExpr { Name = "unwrap" }, regexTok),
            Args = [regexNew],
        }, regexTok);
    }

    private Expr LowerCameoRegexMatch(Expr lhs, Expr rhs, Token opTok, bool negate)
    {
        var testCallee = At(new IndexExpr
        {
            Object = At(new NameExpr { Name = "regex" }, opTok),
            Key = At(new StringExpr { Value = "test" }, opTok),
            IsDot = true,
        }, opTok);
        // Fast path: `s =~ /pat/flags` avoids allocating a Regex userdata object per evaluation.
        List<Expr> args;
        if (rhs is CallExpr { Callee: NameExpr { Name: "unwrap" }, Args: [CallExpr { Callee: IndexExpr { Object: NameExpr { Name: "regex" }, Key: StringExpr { Value: "new" } } } inner] }
            && inner.Args.Count is 1 or 2)
        {
            args = inner.Args.Count == 2 ? [lhs, inner.Args[0], inner.Args[1]] : [lhs, inner.Args[0]];
        }
        else
        {
            args = [lhs, rhs];
        }
        Expr call = At(new CallExpr { Callee = testCallee, Args = args }, opTok);
        return negate ? At(new UnaryExpr { Op = UnaryOp.Not, Operand = call }, opTok) : call;
    }

    // A closer belongs to its immediate block. Legacy nested blocks never inherit it.
    private bool _inBraceBlock;
    private int _expressionDepth;
    private int _headerExpressionDepth;
    private bool _borrowedTokens;

    // Probes borrow tokens only while the caller is suspended. The three token-list
    // mutation sites isolate their stream. Header memo keys use token identity, not
    // indices: splitting generic closers and deterministic class lowering preserve
    // source grammar. Replacement tokens have new identities; unchanged headers
    // retain reusable decisions even when a speculative stream is normalized.
    private void PrepareTokenMutation()
    {
        if (_borrowedTokens) { _toks = new List<Token>(_toks); _borrowedTokens = false; }
    }

    private bool AtCameoDeclaration(string name) => _allowExpressionFunctions && IsName(name)
        && TokIsName(PeekTok());

    // A using declaration has exactly one binding. Reuse the lexical close
    // attribute so all existing normal and exceptional exit paths own cleanup.
    private Stat ParseCameoUsing()
    {
        Token start = _toks[_p++];
        Token name = ExpectName();
        var binding = At(new AttribName { Name = name.Text, Attrib = "close", IsCameoLet = true }, name);
        var resource = At(new LocalStat { Names = [binding] }, start);
        if (AcceptOp(":"))
        {
            resource.Types = ParseTypeList();
            if (resource.Types.Count != 1) throw Error("using requires one resource type", start);
        }
        ExpectOp("=");
        resource.Values.Add(At(new ParenExpr { Inner = ParseExpr() }, start));
        return resource;
    }

    private Stat ParseCameoVar() => ParseCameoVarSequence(allowTry: false)[0];

    private List<Stat> ParseCameoVarSequence(bool allowTry = true)
    {
        Token start = _toks[_p++];
        if (IsOp("{") || IsOp("[")) return ParseCameoDestructure(start, isConst: false);
        var declaration = At(new LocalStat(), start);
        ParseAttribNames(declaration.Names);
        if (declaration.Names.Any(name => name.Attrib != null))
            throw Error("var bindings do not accept local attributes", start);
        if (AcceptOp(":")) declaration.Types = ParseTypeList();
        if (!AcceptOp("=")) throw Error("var binding requires an initializer ('= expression')", start);
        if (allowTry && AtCameoTry())
            return LowerCameoTry(declaration, _toks[_p++]);
        declaration.Values = ParseExprList();
        return [declaration];
    }

    private Stat ParseCameoFunction()
    {
        Token start = _toks[_p++];
        Token name = ExpectName();
        var declaration = At(new LocalFunctionStat { Name = At(new AttribName { Name = name.Text }, name) }, start);
        declaration.Body = ParseFunctionBody(start, name.Text, false, cameo: true);
        return declaration;
    }

    private bool AcceptCameoReturnArrow()
    {
        if (!IsOp("-") || !TokIsOp(PeekTok(), ">") || Cur.End != PeekTok().Pos) return false;
        _p += 2;
        return true;
    }

    private Stat ParseCameoRecord() => ParseCameoRecordSequence(isGlobal: false)[0];

    private bool HasBracedTypeDecl(int nameOffset)
    {
        int i = _p + nameOffset + 1; // token after the type name
        int angle = 0;
        while (i < _toks.Count)
        {
            Token t = _toks[i];
            if (t.Kind == TokenKind.Eof) return false;
            if (t.Kind == TokenKind.Keyword && t.Text == "end") return false;
            if (t.Kind == TokenKind.Op && t.Text == "<") { angle++; i++; continue; }
            if (t.Kind == TokenKind.Op && t.Text == ">") { if (angle > 0) angle--; i++; continue; }
            if (angle == 0 && t.Kind == TokenKind.Op && t.Text == "{")
            {
                // `is {T}` is an array-part clause rather than a braced body
                if (i > 0 && _toks[i - 1].Kind == TokenKind.Name && _toks[i - 1].Text == "is")
                {
                    int depth = 1; i++;
                    while (i < _toks.Count && depth > 0)
                    {
                        if (_toks[i].Kind == TokenKind.Op && _toks[i].Text == "{") depth++;
                        else if (_toks[i].Kind == TokenKind.Op && _toks[i].Text == "}") depth--;
                        i++;
                    }
                    continue;
                }
                return true;
            }
            // Stop once we see something that starts a legacy body (e.g. `field: T` on a new line)
            if (angle == 0 && t.Kind == TokenKind.Op && t.Text == ":") return false;
            i++;
        }
        return false;
    }

    private Stat ParseCameoInterface(bool isGlobal)
    {
        Token start = _toks[_p++]; // interface
        Token name = ExpectName();
        return At(new TypeDeclStat
        {
            IsGlobal = isGlobal,
            Name = At(new AttribName { Name = name.Text }, name),
            Kind = TypeDeclKind.Interface,
            Def = ParseRecordBody(name, isInterface: true, braced: true),
        }, start);
    }

    // Inside a braced `interface`: `fn name[<T>](param: Type, ...) [-> RetType...]`
    // Implicitly prepends `self: self` unless the user explicitly wrote `self` as param 0.
    private void ParseCameoInterfaceMethod(RecordTypeNode r)
    {
        Token fnTok = _toks[_p++]; // fn
        Token methodName = ExpectName();
        var f = At(new FunctionTypeNode(), fnTok);
        if (IsOp("<")) f.TypeParams = ParseTypeParams();
        Token open = ExpectOp("(");
        bool explicitSelf = false;
        if (!IsOp(")"))
        {
            bool first = true;
            do
            {
                if (IsOp("..."))
                {
                    _p++;
                    if (Cur.Kind == TokenKind.Name) _p++;
                    ExpectOp(":");
                    f.VarArg = ParseType();
                    break;
                }
                if (first && Cur.Kind == TokenKind.Name && Cur.Text == "self"
                    && (TokIsOp(PeekTok(), ",") || TokIsOp(PeekTok(), ")") || TokIsOp(PeekTok(), ":")))
                {
                    Token st = _toks[_p++];
                    explicitSelf = true;
                    TypeNode selfType = AcceptOp(":") ? ParseType() : At(new NameTypeNode { Names = ["self"] }, st);
                    f.Params.Add(new ParamTypeNode { Name = "self", Type = selfType });
                    first = false;
                    continue;
                }
                first = false;
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
        if (!explicitSelf)
            f.Params.Insert(0, new ParamTypeNode { Name = "self", Type = At(new NameTypeNode { Names = ["self"] }, fnTok) });
        if (AcceptCameoReturnArrow()) ParseReturnTypes(f.Returns, out f.ReturnsVarArg);
        r.Fields.Add(At(new RecordField { Name = methodName.Text, Type = f }, methodName));
    }

    // Inside a braced `record`: `[static] fn name[<T>](params...) [-> RetType...] { body }`
    private void ParseCameoRecordMethod(RecordTypeNode r, string recordName, bool isStatic)
    {
        Token fnTok = _toks[_p++]; // fn
        Token methodName = ExpectName();
        bool explicitSelf = false;
        if (!isStatic)
        {
            // Peek past optional `<T, ...>` to see if param 0 is explicitly named `self`.
            int idx = _p;
            if (idx < _toks.Count && TokIsOp(_toks[idx], "<"))
            {
                int depth = 1; idx++;
                while (idx < _toks.Count && depth > 0)
                {
                    if (TokIsOp(_toks[idx], "<")) depth++;
                    else if (TokIsOp(_toks[idx], ">")) depth--;
                    idx++;
                }
            }
            if (idx + 2 < _toks.Count && TokIsOp(_toks[idx], "(")
                && _toks[idx + 1].Kind == TokenKind.Name && _toks[idx + 1].Text == "self"
                && (TokIsOp(_toks[idx + 2], ",") || TokIsOp(_toks[idx + 2], ")") || TokIsOp(_toks[idx + 2], ":")))
            {
                explicitSelf = true;
            }
        }
        string fullName = $"{recordName}{(isStatic ? "." : ":")}{methodName.Text}";
        FunctionBody body = ParseFunctionBody(fnTok, fullName, isMethod: !isStatic && !explicitSelf, cameo: true);
        if (!isStatic && explicitSelf && body.Params.Count > 0 && body.Params[0].Type == null)
            body.Params[0].IsImplicitSelf = true;
        body.IsCameoMethod = !isStatic;

        // Synthesize the field signature on the record type so interfaces and callers see the method.
        var sig = At(new FunctionTypeNode(), fnTok);
        if (body.TypeParams != null) sig.TypeParams = new List<TypeParam>(body.TypeParams);
        for (int i = 0; i < body.Params.Count; i++)
        {
            Param p = body.Params[i];
            TypeNode pt = p.Type ?? (!isStatic && i == 0
                ? At(new NameTypeNode { Names = ["self"] }, fnTok)
                : AnyTypeNode(fnTok));
            sig.Params.Add(new ParamTypeNode { Name = p.Name, Optional = p.Optional, Type = pt });
        }
        if (body.IsVarArg) sig.VarArg = body.VarArgType ?? AnyTypeNode(fnTok);
        sig.Returns.AddRange(body.Returns);
        sig.ReturnsVarArg = body.ReturnsVarArg;
        r.Fields.Add(At(new RecordField { Name = methodName.Text, Type = sig }, methodName));

        var fnStat = At(new FunctionStat
        {
            Root = At(new NameExpr { Name = recordName }, methodName),
            Path = [methodName.Text],
            IsMethod = !isStatic,
            Body = body,
        }, fnTok);
        (r.CameoMethodStats ??= []).Add(fnStat);
    }

    private List<Stat> ParseCameoRecordSequence(bool isGlobal)
    {
        Token start = _toks[_p++]; // record
        Token name = ExpectName();
        RecordTypeNode def = ParseRecordBody(name, isInterface: false, braced: true);
        var decl = At(new TypeDeclStat
        {
            IsGlobal = isGlobal,
            Name = At(new AttribName { Name = name.Text }, name),
            Kind = TypeDeclKind.Record,
            Def = def,
        }, start);

        if (def.CameoMethodStats is not { Count: > 0 } methods)
            return [decl];

        string mtVar = "_\0mt-" + name.Text + "-" + start.Pos;
        string recId = $"{_file}:{start.Pos}:{name.Text}";
        def.CameoMetatableVar = mtVar;
        def.CameoRecordId = recId;
        if (def.Where == null)
        {
            string escaped = recId.Replace("\\", "\\\\").Replace("\"", "\\\"");
            def.WhereSource = $"__isa(self, \"{escaped}\")";
            def.Where = ParseExpression(def.WhereSource, _file);
        }

        // Add `metamethod __call` so `Rec { field = val }` and `Rec()` type-check and return `Rec`.
        if (!def.Fields.Any(f => f.IsMetamethod && f.Name == "__call"))
        {
            var callSig = At(new FunctionTypeNode(), name);
            if (def.TypeParams is { Count: > 0 } tps)
            {
                callSig.TypeParams = new List<TypeParam>(tps);
                var recInst = At(new NameTypeNode
                {
                    Names = [name.Text],
                    TypeArgs = tps.Select(tp => (TypeNode)At(new NameTypeNode { Names = [tp.Name] }, name)).ToList(),
                }, name);
                callSig.Params.Add(new ParamTypeNode { Name = "_cls", Type = AnyTypeNode(name) });
                callSig.Params.Add(new ParamTypeNode { Name = "init", Optional = true, Type = recInst });
                callSig.Returns.Add(recInst);
            }
            else
            {
                var selfType = At(new NameTypeNode { Names = ["self"] }, name);
                callSig.Params.Add(new ParamTypeNode { Name = "self", Type = selfType });
                callSig.Params.Add(new ParamTypeNode { Name = "init", Optional = true, Type = selfType });
                callSig.Returns.Add(selfType);
            }
            def.Fields.Add(At(new RecordField { Name = "__call", IsMetamethod = true, Type = callSig }, name));
        }

        var mtLocal = At(new LocalStat
        {
            Names = [At(new AttribName { Name = mtVar }, start)],
            Types = [AnyTypeNode(start)],
            Values = [At(new TableExpr(), start)],
        }, start);

        var registerCall = At(new CallStat
        {
            Call = At(new CallExpr
            {
                Callee = At(new NameExpr { Name = "__cameo_record" }, start),
                Args =
                [
                    At(new NameExpr { Name = name.Text }, name),
                    At(new NameExpr { Name = mtVar }, start),
                    At(new StringExpr { Value = recId }, start),
                    At(new StringExpr { Value = name.Text }, name),
                ],
            }, start),
        }, start);

        var stats = new List<Stat>(methods.Count + 3) { decl, mtLocal, registerCall };
        stats.AddRange(methods);
        return stats;
    }

    private Block ParseBraceBlock()
    {
        Token open = ExpectOp("{");
        Block body = ParseBlock(braced: true);
        ExpectClose("}", "{", open);
        return body;
    }

    private Block ParseControlBody(string keyword, string construct, Token start)
    {
        if (_allowExpressionFunctions)
        {
            if (IsKw(keyword))
                throw Error($"use '{{ ... }}' instead of '{keyword} ... end' in Cameo", Cur);
            return ParseBraceBlock();
        }
        ExpectKw(keyword);
        Block body = ParseBlock();
        ExpectClose("end", construct, start);
        return body;
    }

    private Stat ParseCameoIfRest(Token start, IfStat statement, Expr condition)
    {
        statement.Clauses.Add((condition, ParseBraceBlock()));
        while (IsKw("elseif") || IsKw("else"))
        {
            if (IsKw("elseif"))
                throw Error("use 'else if' instead of 'elseif' in Cameo", Cur);
            _p++; // else
            if (IsKw("if"))
            {
                _p++; // if
                Expr next = ParseControlExpr("then");
                if (IsKw("then")) throw Error("use '{ ... }' instead of 'then ... end' in Cameo", Cur);
                statement.Clauses.Add((next, ParseBraceBlock()));
            }
            else
            {
                statement.Else = ParseBraceBlock();
                break;
            }
        }
        return statement;
    }

    private bool AtControlHeaderTop() => _headerExpressionDepth != 0
        && _expressionDepth == _headerExpressionDepth;

    // Mutable parse flags and quote output stay private; signatures and tokens
    // are borrowed read-only until the first declaration or token normalization.
    private Parser CreateSyntaxProbe()
    {
        return new Parser(_toks, this)
        {
            _p = _p,
            _borrowedTokens = true,
            _levels = _levels,
            _classOwner = null,
            _expressionState = new ExpressionState(),
            _quoteVars = _quoteVars == null ? null : new HashSet<string>(_quoteVars),
            _macros = new MacroState
            {
                Sigs = _macros.Sigs,
                BorrowedSignatures = true,
                SignatureVersion = _macros.SignatureVersion,
                SignatureContexts = _macros.SignatureContexts
            }
        };
    }

    private bool HasLegacyRecordDeclaration()
    {
        if (_allowExpressionFunctions) return false;
        var probe = CreateSyntaxProbe();
        try { return probe.TryParseTypeDecl(probe.Cur, false) != null; }
        catch (CameoSyntaxException) { return false; }
    }

    private Expr ParseBraceHeaderExpr()
    {
        int previous = _headerExpressionDepth;
        _headerExpressionDepth = _expressionDepth + 1;
        try { return ParseExpr(); }
        finally { _headerExpressionDepth = previous; }
    }

    private Expr ParseControlExpr(string keyword)
    {
        if (!_allowExpressionFunctions) return ParseExpr();
        return ParseBraceHeaderExpr();
    }

    private List<Expr> ParseControlExprList(string keyword)
    {
        if (!_allowExpressionFunctions) return ParseExprList();
        int previous = _headerExpressionDepth;
        _headerExpressionDepth = _expressionDepth + 1;
        try { return ParseExprList(); }
        finally { _headerExpressionDepth = previous; }
    }

    private Expr ParseCameoListExpr()
    {
        Token open = ExpectOp("[");
        if (AcceptOp("]"))
            return At(new CameoListExpr(), open);
        Expr first = ParseExpr();
        if (IsKw("for"))
        {
            _p++; // consume 'for'
            var vars = new List<AttribName>();
            Token n1 = ExpectName();
            vars.Add(At(new AttribName { Name = n1.Text }, n1));
            while (AcceptOp(","))
            {
                Token nk = ExpectName();
                vars.Add(At(new AttribName { Name = nk.Text }, nk));
            }
            ExpectKw("in");
            Expr iterable = ParseExpr();
            Expr? filter = null;
            if (IsKw("if"))
            {
                _p++;
                filter = ParseExpr();
            }
            ExpectClose("]", "[", open);
            return At(new CameoListCompExpr
            {
                Body = first,
                Vars = vars,
                Iterable = iterable,
                Filter = filter,
            }, open);
        }
        var list = At(new CameoListExpr(), open);
        list.Items.Add(first);
        while (AcceptOp(","))
        {
            if (IsOp("]")) break;
            list.Items.Add(ParseExpr());
        }
        ExpectClose("]", "[", open);
        return list;
    }

    private bool _inChunkTopLevel;
    private List<CameoExportItem>? _chunkExports;

    private static void MakeOptionalField(RecordField rf, Token at)
    {
        rf.IsOptional = true;
        var nilNode = At(new NameTypeNode { Names = ["nil"] }, at);
        if (rf.Type is UnionTypeNode u)
        {
            u.Members.Add(nilNode);
        }
        else
        {
            rf.Type = At(new UnionTypeNode { Members = [rf.Type, nilNode] }, at);
        }
    }

    private bool AtCameoExport()
    {
        if (!_allowExpressionFunctions || !IsName("export")) return false;
        Token nx = PeekTok();
        if (nx.Kind != TokenKind.Name) return false;
        return nx.Text is "fn" or "let" or "var" or "record" or "interface" or "enum" or "type";
    }

    private List<Stat> ParseCameoExportSequence()
    {
        Token exportTok = _toks[_p++]; // export
        List<CameoExportItem> exports = _chunkExports ??= new List<CameoExportItem>();
        void RecordExport(string name, bool hasRuntimeValue, Token at)
        {
            if (exports.Any(e => e.Name == name))
                throw Error($"duplicate export '{name}'", at);
            exports.Add(At(new CameoExportItem { Name = name, HasRuntimeValue = hasRuntimeValue }, at));
        }

        if (AtCameoDeclaration("fn"))
        {
            Stat fnStat = ParseCameoFunction();
            if (fnStat is LocalFunctionStat lfs)
                RecordExport(lfs.Name.Name, true, exportTok);
            return [fnStat];
        }
        if (AtCameoLet())
        {
            List<Stat> seq = ParseCameoLetSequence();
            foreach (Stat s in seq)
                if (s is LocalStat ls)
                    foreach (AttribName n in ls.Names)
                        if (!n.Name.StartsWith('\0')) RecordExport(n.Name, true, exportTok);
            return seq;
        }
        if (AtCameoVar())
        {
            List<Stat> seq = ParseCameoVarSequence();
            foreach (Stat s in seq)
                if (s is LocalStat ls)
                    foreach (AttribName n in ls.Names)
                        if (!n.Name.StartsWith('\0')) RecordExport(n.Name, true, exportTok);
            return seq;
        }
        if (AtCameoDeclaration("record"))
        {
            List<Stat> seq = ParseCameoRecordSequence(isGlobal: false);
            if (seq[0] is TypeDeclStat tds)
                RecordExport(tds.Name.Name, true, exportTok);
            return seq;
        }
        if (AtCameoDeclaration("interface"))
        {
            Stat iface = ParseCameoInterface(isGlobal: false);
            if (iface is TypeDeclStat tds)
                RecordExport(tds.Name.Name, false, exportTok);
            return [iface];
        }
        if (AtCameoDeclaration("enum") && TokIsOp(PeekTok(2), "{"))
        {
            Stat en = ParseCameoEnum();
            if (en is TypeDeclStat tds)
                RecordExport(tds.Name.Name, false, exportTok);
            return [en];
        }
        if (AtCameoTypeAlias())
        {
            Stat alias = ParseCameoTypeAlias(isGlobal: false);
            if (alias is TypeDeclStat tds)
                RecordExport(tds.Name.Name, tds.Kind == TypeDeclKind.Record, exportTok);
            return [alias];
        }
        throw Error("export must be followed by fn, let, var, record, interface, enum, or type", exportTok);
    }

    private bool AtCameoImport()
    {
        if (!_allowExpressionFunctions || !IsName("import")) return false;
        Token nx = PeekTok();
        if (TokIsOp(nx, "{")) return true;
        return TokIsName(nx) && TokIsName(PeekTok(2)) && PeekTok(2).Text == "from";
    }

    private Stat ParseCameoImport()
    {
        Token start = _toks[_p++]; // import
        var stat = At(new CameoImportStat(), start);
        if (IsOp("{"))
        {
            Token open = _toks[_p++];
            var items = new List<CameoImportItem>();
            while (!IsOp("}") && Cur.Kind != TokenKind.Eof)
            {
                Token expTok = ExpectName();
                Token localTok = expTok;
                if (IsName("as"))
                {
                    _p++;
                    localTok = ExpectName();
                }
                items.Add(At(new CameoImportItem
                {
                    ExportName = expTok.Text,
                    LocalName = At(new AttribName { Name = localTok.Text, Attrib = "const", IsCameoLet = true }, localTok),
                }, expTok));
                if (!AcceptOp(",") && !AcceptOp(";")) break;
            }
            ExpectClose("}", "{", open);
            if (items.Count == 0) throw Error("import { ... } requires at least one binding", open);
            stat.Named = items;
        }
        else
        {
            Token nameTok = ExpectName();
            stat.DefaultName = At(new AttribName { Name = nameTok.Text, Attrib = "const", IsCameoLet = true }, nameTok);
        }
        if (!IsName("from")) throw Error("expected 'from' in import statement");
        _p++;
        if (Cur.Kind != TokenKind.String) throw Error("expected module name string after 'from'");
        stat.Module = _toks[_p++].Text;
        AcceptOp(";");
        return stat;
    }

    private bool AtCameoMatch()
    {
        if (!_allowExpressionFunctions || !IsName("match")) return false;
        Token nx = PeekTok();
        if (nx.Kind == TokenKind.Eof) return false;
        if (nx.Kind == TokenKind.Op && nx.Text is "=" or "." or ":" or "[" or "," or ";" or ")" or "}" or "=>" or "|>" or "==" or "!=" or "~=")
            return false;
        var probe = CreateSyntaxProbe();
        probe._p++; // skip 'match'
        try
        {
            probe.ParseBraceHeaderExpr();
            return probe.IsOp("{");
        }
        catch (CameoSyntaxException)
        {
            return false;
        }
    }

    private CameoMatchExpr ParseCameoMatch(bool isStatement)
    {
        Token start = _toks[_p++]; // match
        Expr scrutinee = ParseBraceHeaderExpr();
        Token open = ExpectOp("{");
        var match = At(new CameoMatchExpr { Scrutinee = scrutinee, IsStatement = isStatement }, start);
        while (!IsOp("}") && Cur.Kind != TokenKind.Eof)
        {
            if (AcceptOp(",") || AcceptOp(";")) continue;
            Token armTok = Cur;
            var arm = At(new MatchArm(), armTok);
            arm.Patterns.Add(ParseMatchPattern());
            while (AcceptOp("|"))
                arm.Patterns.Add(ParseMatchPattern());
            ExpectOp("=");
            ExpectOp(">");
            if (IsOp("{"))
            {
                if (isStatement)
                {
                    arm.BlockBody = ParseBraceBlock();
                }
                else
                {
                    ParseCameoMatchArmBlock(Cur, arm);
                }
            }
            else
            {
                Expr expr = ParseExpr();
                if (isStatement)
                {
                    if (expr is CallExpr or MethodCallExpr)
                        arm.BlockBody = At(new Block { Stats = [At(new CallStat { Call = expr }, armTok)] }, armTok);
                    else
                        arm.ExprBody = expr;
                }
                else
                {
                    arm.ExprBody = expr;
                }
            }
            match.Arms.Add(arm);
            AcceptOp(",");
            AcceptOp(";");
        }
        ExpectClose("}", "{", open);
        if (match.Arms.Count == 0) throw Error("match requires at least one arm", start);
        return match;
    }

    private MatchPattern ParseMatchPattern()
    {
        Token t = Cur;
        if (t.Kind == TokenKind.Name && t.Text == "_")
        {
            _p++;
            return At(new MatchPattern { IsWildcard = true }, t);
        }
        if (t.Kind == TokenKind.Name && t.Text == "is")
        {
            _p++;
            TypeNode typeNode = ParseType();
            AttribName? binding = null;
            if (AcceptOp("("))
            {
                Token b = ExpectName();
                binding = At(new AttribName { Name = b.Text, Attrib = "const", IsCameoLet = true }, b);
                ExpectOp(")");
            }
            return At(new MatchPattern { TypeTest = typeNode, Binding = binding }, t);
        }
        if (t.Kind == TokenKind.String)
        {
            _p++;
            return At(new MatchPattern { Literal = At(new StringExpr { Value = t.Text }, t) }, t);
        }
        if (t.Kind == TokenKind.Number)
        {
            _p++;
            return At(new MatchPattern { Literal = MakeNumber(t) }, t);
        }
        if (IsOp("-") && PeekTok().Kind == TokenKind.Number)
        {
            _p++;
            Token num = _toks[_p++];
            return At(new MatchPattern { Literal = At(new UnaryExpr { Op = UnaryOp.Neg, Operand = MakeNumber(num) }, t) }, t);
        }
        if (t.Kind == TokenKind.Keyword && t.Text is "true" or "false" or "nil")
        {
            _p++;
            Expr lit = t.Text == "nil"
                ? At(new NilExpr(), t)
                : At(new BoolExpr { Value = t.Text == "true" }, t);
            return At(new MatchPattern { Literal = lit }, t);
        }
        throw Error($"expected match pattern (literal, '_', or 'is Type'), got '{t}'", t);
    }

    private void ParseCameoMatchArmBlock(Token open, MatchArm arm)
    {
        ExpectOp("{");
        var block = At(new Block(), open);
        while (!IsOp("}") && Cur.Kind != TokenKind.Eof)
        {
            if (IsKw("return"))
            {
                block.Stats.Add(ParseReturn());
                break;
            }
            if (AtCameoLet())
            {
                EnterLevel();
                try { block.Stats.AddRange(ParseCameoLetSequence()); }
                finally { _levels--; }
                continue;
            }
            if (AtCameoVar())
            {
                EnterLevel();
                try { block.Stats.AddRange(ParseCameoVarSequence()); }
                finally { _levels--; }
                continue;
            }
            if (AtCameoDeclaration("using") && (TokIsOp(PeekTok(2), "=") || TokIsOp(PeekTok(2), ":")))
            {
                block.Stats.Add(ParseCameoUsing());
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
            if (IsKw("if") || IsKw("while") || IsKw("for") || IsKw("do") || IsKw("repeat") || IsKw("local")
                || AtCameoDeclaration("fn")
                || (AtCameoDeclaration("enum") && TokIsOp(PeekTok(2), "{"))
                || IsOp(";"))
            {
                Stat? st = ParseStatement();
                if (st != null) block.Stats.Add(st);
                continue;
            }
            int saved = _p;
            Expr expr = ParseExpr();
            AcceptOp(";");
            if (IsOp("}"))
            {
                arm.ExprBody = expr;
                break;
            }
            if (IsOp("=") || IsOp(",") || (Cur.Kind == TokenKind.Op && CompoundOp(Cur.Text, out _)))
            {
                _p = saved;
                Stat? st = ParseStatement();
                if (st != null) block.Stats.Add(st);
                continue;
            }
            if (expr is CallExpr or MethodCallExpr)
            {
                block.Stats.Add(At(new CallStat { Call = expr }, open));
                continue;
            }
            throw Error("expected '}' after match arm expression");
        }
        ExpectClose("}", "{", open);
        if (block.Stats.Count > 0) arm.BlockBody = block;
    }
}
