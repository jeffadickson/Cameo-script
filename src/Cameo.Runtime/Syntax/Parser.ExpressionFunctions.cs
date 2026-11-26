namespace Cameo.Runtime.Syntax;

public sealed partial class Parser
{
    private readonly bool _allowExpressionFunctions;
    private sealed class ExpressionState
    {
        internal Token? First;
        internal bool LegacyReparse;
    }
    private ExpressionState _expressionState = new();
    private Dictionary<int, int>? _parenthesisPairs;

    // Pair once per token stream: nested ordinary parentheses must not repeatedly scan
    // their whole suffix. Token strings/comments are already separate from punctuation.
    private bool AtExpressionFunction()
    {
        if (!_allowExpressionFunctions || !IsOp("(")) return false;
        if (_parenthesisPairs == null)
        {
            _parenthesisPairs = new();
            var opens = new Stack<int>();
            for (int i = 0; i < _toks.Count; i++)
            {
                Token token = _toks[i];
                if (TokIsOp(token, "(")) opens.Push(i);
                else if (TokIsOp(token, ")") && opens.TryPop(out int from)) _parenthesisPairs[from] = i;
            }
        }
        if (!_parenthesisPairs.TryGetValue(_p, out int close)) return false;
        if (close + 2 < _toks.Count)
        {
            Token equals = _toks[close + 1], greater = _toks[close + 2];
            if (TokIsOp(equals, "=") && TokIsOp(greater, ">") && equals.End == greater.Pos) return true;
        }
        // Optional return annotation before `=> { ... }`: `(x: integer) -> integer => { ... }`
        if (close + 2 < _toks.Count && TokIsOp(_toks[close + 1], "-") && TokIsOp(_toks[close + 2], ">")
            && _toks[close + 1].End == _toks[close + 2].Pos)
        {
            int depth = 0;
            for (int k = close + 3; k + 1 < _toks.Count; k++)
            {
                if (TokIsOp(_toks[k], "(") || TokIsOp(_toks[k], "{") || TokIsOp(_toks[k], "<")) depth++;
                else if (TokIsOp(_toks[k], ")") || TokIsOp(_toks[k], "}") || TokIsOp(_toks[k], ">"))
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0 && TokIsOp(_toks[k], "=") && TokIsOp(_toks[k + 1], ">") && _toks[k].End == _toks[k + 1].Pos)
                    return true;
                else if (depth == 0 && (_toks[k].Kind == TokenKind.Eof || TokIsOp(_toks[k], ";")))
                    break;
            }
        }
        return false;
    }

    private bool AtArrowBlockBody(bool hasReturnType)
    {
        if (!IsOp("{")) return false;
        if (hasReturnType) return true;
        Token first = PeekTok();
        if (first.Kind == TokenKind.Keyword && first.Text is "return" or "local" or "if" or "while" or "for" or "repeat" or "do")
            return true;
        if (first.Kind == TokenKind.Name && first.Text is "let" or "var" or "using" or "fn" or "record" or "enum")
            return true;
        // Probe whether `{ ... }` parses as a table literal; if not, treat as a block body.
        var probe = CreateSyntaxProbe();
        try
        {
            probe.ParseTable();
            return false;
        }
        catch (CameoSyntaxException)
        {
            return true;
        }
    }

    private Expr ParseExpressionFunction()
    {
        Token open = ExpectOp("(");
        var body = At(new FunctionBody { Name = "Cameo expression", IsExpressionFunction = true }, open);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!IsOp(")"))
        {
            do
            {
                if (IsOp("...")) throw Error("CAMEO005: expression functions do not accept varargs; use a full function(...) ... end with explicit parameter and result types");
                Token name = ExpectName();
                if (!names.Add(name.Text)) throw Error($"CAMEO005: duplicate expression parameter '{name.Text}'; rename or remove the duplicate parameter", name);
                var parameter = At(new Param { Name = name.Text }, name);
                if (AcceptOp(":")) parameter.Type = ParseType();
                body.Params.Add(parameter);
            } while (AcceptOp(","));
        }
        ExpectClose(")", "(", open);
        bool hasReturnType = false;
        if (AcceptCameoReturnArrow())
        {
            hasReturnType = true;
            body.HasReturnAnnotation = true;
            ParseReturnTypes(body.Returns, out body.ReturnsVarArg);
        }
        ExpectOp("=");
        ExpectOp(">");
        if (AtArrowBlockBody(hasReturnType))
        {
            body.IsExpressionFunction = !hasReturnType;
            body.Name = "anonymous";
            body.Body = ParseBraceBlock();
            body.EndLine = _toks[Math.Max(0, _p - 1)].Line;
            return At(new FunctionExpr { Body = body }, open);
        }
        _expressionState.First ??= open;
        Expr expression = ParseExpr();
        body.EndLine = _toks[Math.Max(0, _p - 1)].Line;
        body.Body = At(new Block(), open);
        var result = At(new ReturnStat(), open);
        // Lua parentheses truncate multiple results. The checker inspects the original
        // expression first; the compiler executes this explicit single-value lowering.
        result.Values.Add(At(new ParenExpr { Inner = expression }, open));
        body.Body.Stats.Add(result);
        return At(new FunctionExpr { Body = body }, open);
    }
}
