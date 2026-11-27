using System.Text;

namespace Cameo.Runtime.Syntax;

public enum TokenKind { Name, Keyword, Number, String, Op, Eof, MacroQuote, Interp, Regex }

public sealed class Token
{
    public TokenKind Kind;
    /// <summary>Identifier/keyword/operator text, raw numeral text, or the decoded string value.</summary>
    public string Text = "";
    /// <summary>For Regex tokens: trailing flag letters (e.g. "im").</summary>
    public string? Flags;
    public int Line, Col;
    /// <summary>Source offsets [Pos, End).</summary>
    public int Pos, End;
    /// <summary>For MacroQuote tokens: true for a triple-backtick (statements) quote.</summary>
    public bool Triple;
    /// <summary>For Interp tokens: the literal pieces (decoded) and the source of each {expression}.</summary>
    public List<(bool IsExpr, string Text)>? Parts;
    public override string ToString() => Kind == TokenKind.Eof ? "<eof>" : Kind == TokenKind.String ? "\"" + Text + "\"" : Kind == TokenKind.Regex ? "/" + Text + "/" + (Flags ?? "") : Text;
}

public sealed record Diagnostic(string File, int Line, int Col, string Message, bool IsWarning = false)
{
    public override string ToString() => $"{File}:{Line}:{Col}: {(IsWarning ? "warning: " : "")}{Message}";
}

public sealed class CameoSyntaxException : Exception
{
    public Diagnostic Diagnostic { get; }
    public CameoSyntaxException(Diagnostic d) : base(d.ToString()) { Diagnostic = d; }
}

/// <summary>Tokenizer for Teal (Lua 5.4 lexical grammar plus '?').</summary>
public sealed class Lexer
{
    private static readonly HashSet<string> Keywords = new()
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    private readonly string _src;
    private readonly string _file;
    private readonly bool _cameo;
    private int _pos;
    private int _line = 1;
    private int _lineStart;
    private Token? _prev;

    public Lexer(string source, string fileName, bool cameo = false)
    {
        _src = source;
        _file = fileName;
        _cameo = cameo;
        // skip shebang
        if (_src.StartsWith("#!"))
            while (_pos < _src.Length && _src[_pos] != '\n') _pos++;
    }

    public static List<Token> Tokenize(string source, string fileName, bool cameo = false)
    {
        var lx = new Lexer(source, fileName, cameo);
        var list = new List<Token>();
        while (true)
        {
            Token t = lx.Next();
            list.Add(t);
            if (t.Kind == TokenKind.Eof) return list;
        }
    }

    private CameoSyntaxException Error(string msg, int line, int col) => new(new Diagnostic(_file, line, col, msg));
    private CameoSyntaxException Error(string msg) => Error(msg, _line, _pos - _lineStart + 1);

    private char Cur => _pos < _src.Length ? _src[_pos] : '\0';
    private char Peek(int n = 1) => _pos + n < _src.Length ? _src[_pos + n] : '\0';

    private void NewLine()
    {
        // _pos is at '\n' or '\r'
        char c = _src[_pos++];
        if (_pos < _src.Length && (_src[_pos] == '\n' || _src[_pos] == '\r') && _src[_pos] != c) _pos++;
        _line++;
        _lineStart = _pos;
    }

    public Token Next()
    {
        SkipTrivia();
        var t = new Token { Line = _line, Col = _pos - _lineStart + 1, Pos = _pos };
        if (_pos >= _src.Length)
        {
            t.Kind = TokenKind.Eof;
            t.End = _pos;
            return t;
        }
        char c = Cur;
        if (char.IsAsciiLetter(c) || c == '_')
        {
            int s = _pos;
            while (char.IsAsciiLetterOrDigit(Cur) || Cur == '_') _pos++;
            t.Text = _src.Substring(s, _pos - s);
            t.Kind = Keywords.Contains(t.Text) ? TokenKind.Keyword : TokenKind.Name;
        }
        else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek())))
        {
            t.Kind = TokenKind.Number;
            t.Text = ReadNumber();
        }
        else if (_cameo && c == '-' && Peek() == '-')
        {
            throw Error("use '//' for line comments (or '/* ... */' for block comments) in Cameo; write '-(-x)' for double negation", t.Line, t.Col);
        }
        else if (_cameo && c == '$' && Peek() == '"' && Peek(2) == '"' && Peek(3) == '"')
        {
            // $""" ... {expr} ... """: interpolated raw multi-line string in Cameo
            _pos++;
            t.Kind = TokenKind.Interp;
            t.Parts = ReadInterpolatedTripleQuote(t);
        }
        else if (_cameo && c == '"' && Peek() == '"' && Peek(2) == '"')
        {
            // """ ... """: raw multi-line string in Cameo (frees [[...]] for nested list literals)
            t.Kind = TokenKind.String;
            t.Text = ReadTripleQuotedString();
        }
        else if (c == '"' || c == '\'')
        {
            t.Kind = TokenKind.String;
            t.Text = ReadString(c);
        }
        else if (!_cameo && c == '$' && Peek() == '[' && LongBracketLevelAt(_pos + 1) >= 0)
        {
            // $[[ ... {expr} ... ]]: an interpolated long string (may span lines, no escapes)
            _pos++;
            t.Kind = TokenKind.Interp;
            t.Parts = ReadInterpolatedLong(t, LongBracketLevel());
        }
        else if (c == '$' && (Peek() == '"' || Peek() == '\''))
        {
            // interpolated string $"...{expr}..." (an extension): the parser takes it apart
            _pos++;
            t.Kind = TokenKind.Interp;
            t.Parts = ReadInterpolated(t);
        }
        else if (!_cameo && c == '[' && (Peek() == '[' || Peek() == '='))
        {
            int level = LongBracketLevel();
            if (level >= 0)
            {
                t.Kind = TokenKind.String;
                t.Text = ReadLongString(level, "string");
            }
            else
            {
                t.Kind = TokenKind.Op;
                t.Text = "[";
                _pos++;
            }
        }
        else if (c == '`')
        {
            // macro quote: `expr` or ```statements```; the content is parsed separately
            t.Kind = TokenKind.MacroQuote;
            t.Triple = Peek() == '`' && Peek(2) == '`';
            string delim = t.Triple ? "```" : "`";
            int start = _pos + delim.Length;
            int end = _src.IndexOf(delim, start, StringComparison.Ordinal);
            if (end < 0) throw Error("unfinished macro quote", t.Line, t.Col);
            t.Text = _src.Substring(start, end - start);
            for (int q = start; q < end; q++)
            {
                if (_src[q] == '\n') { _line++; _lineStart = q + 1; }
            }
            _pos = end + delim.Length;
        }
        else if (_cameo && c == '/' && Peek() is not ('/' or '*' or '=') && CanStartRegexLiteral())
        {
            t.Kind = TokenKind.Regex;
            t.Text = ReadRegex(t, out string flags);
            t.Flags = flags;
        }
        else
        {
            t.Kind = TokenKind.Op;
            t.Text = ReadOp();
        }
        t.End = _pos;
        _prev = t;
        return t;
    }

    private bool CanStartRegexLiteral()
    {
        if (_prev == null) return true;
        return _prev.Kind switch
        {
            TokenKind.Number or TokenKind.String or TokenKind.Interp or TokenKind.Regex or TokenKind.Name => false,
            TokenKind.Keyword => _prev.Text is not ("true" or "false" or "nil" or "end"),
            TokenKind.Op => _prev.Text is not (")" or "]" or "}" or "..."),
            _ => true,
        };
    }

    private string ReadRegex(Token start, out string flags)
    {
        _pos++; // consume opening '/'
        var sb = new StringBuilder();
        bool inClass = false;
        bool closed = false;
        while (_pos < _src.Length)
        {
            char ch = _src[_pos++];
            if (ch == '\n' || ch == '\r')
                throw Error("unterminated regex literal", start.Line, start.Col);
            if (ch == '\\')
            {
                if (_pos >= _src.Length || _src[_pos] == '\n' || _src[_pos] == '\r')
                    throw Error("unterminated regex literal", start.Line, start.Col);
                char next = _src[_pos++];
                if (next == '/') sb.Append('/');
                else { sb.Append('\\'); sb.Append(next); }
                continue;
            }
            if (ch == '[' && !inClass) { inClass = true; sb.Append(ch); continue; }
            if (ch == ']' && inClass) { inClass = false; sb.Append(ch); continue; }
            if (ch == '/' && !inClass) { closed = true; break; }
            sb.Append(ch);
        }
        if (!closed) throw Error("unterminated regex literal", start.Line, start.Col);
        var fl = new StringBuilder();
        while (_pos < _src.Length && char.IsAsciiLetter(_src[_pos]))
            fl.Append(_src[_pos++]);
        flags = fl.ToString();
        return sb.ToString();
    }

    private void SkipTrivia()
    {
        while (_pos < _src.Length)
        {
            char c = _src[_pos];
            if (c == '\n' || c == '\r') NewLine();
            else if (c == ' ' || c == '\t' || c == '\f' || c == '\v') _pos++;
            else if (_cameo && c == '/' && Peek() == '/')
            {
                _pos += 2;
                while (_pos < _src.Length && _src[_pos] != '\n' && _src[_pos] != '\r') _pos++;
            }
            else if (_cameo && c == '/' && Peek() == '*')
            {
                int startLine = _line, startCol = _pos - _lineStart + 1;
                _pos += 2;
                int depth = 1;
                while (_pos < _src.Length && depth > 0)
                {
                    char ch = _src[_pos];
                    if (ch == '\n' || ch == '\r') { NewLine(); continue; }
                    if (ch == '/' && Peek() == '*') { _pos += 2; depth++; continue; }
                    if (ch == '*' && Peek() == '/') { _pos += 2; depth--; continue; }
                    _pos++;
                }
                if (depth > 0) throw Error("unfinished block comment", startLine, startCol);
            }
            else if (!_cameo && c == '-' && Peek() == '-')
            {
                _pos += 2;
                if (Cur == '[')
                {
                    int level = LongBracketLevel();
                    if (level >= 0) { ReadLongString(level, "comment"); continue; }
                }
                while (_pos < _src.Length && _src[_pos] != '\n' && _src[_pos] != '\r') _pos++;
            }
            else break;
        }
    }

    private string ReadTripleQuotedString()
    {
        int startLine = _line, startCol = _pos - _lineStart + 1;
        _pos += 3; // consume opening """
        if (Cur == '\r' || Cur == '\n') NewLine();
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _src.Length) throw Error("unfinished multi-line string", startLine, startCol);
            if (_src[_pos] == '"' && Peek() == '"' && Peek(2) == '"')
            {
                _pos += 3;
                return sb.ToString();
            }
            char c = _src[_pos];
            if (c == '\n' || c == '\r')
            {
                NewLine();
                sb.Append('\n');
            }
            else
            {
                sb.Append(c);
                _pos++;
            }
        }
    }

    // At '[': returns the level of a long bracket opening ("[[" = 0, "[=[" = 1, ...) or -1.
    private int LongBracketLevel() => LongBracketLevelAt(_pos);

    private int LongBracketLevelAt(int at)
    {
        if (at >= _src.Length || _src[at] != '[') return -1;
        int p = at + 1;
        int level = 0;
        while (p < _src.Length && _src[p] == '=') { p++; level++; }
        return p < _src.Length && _src[p] == '[' ? level : -1;
    }

    private string ReadLongString(int level, string what)
    {
        int startLine = _line, startCol = _pos - _lineStart + 1;
        _pos += level + 2;
        // first newline is skipped
        if (Cur == '\r' || Cur == '\n') NewLine();
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _src.Length) throw Error($"unfinished long {what}", startLine, startCol);
            char c = _src[_pos];
            if (c == ']')
            {
                int p = _pos + 1, l = 0;
                while (p < _src.Length && _src[p] == '=') { p++; l++; }
                if (l == level && p < _src.Length && _src[p] == ']')
                {
                    _pos = p + 1;
                    return sb.ToString();
                }
                sb.Append(c);
                _pos++;
            }
            else if (c == '\n' || c == '\r')
            {
                NewLine();
                sb.Append('\n');
            }
            else
            {
                sb.Append(c);
                _pos++;
            }
        }
    }

    private string ReadNumber()
    {
        int s = _pos;
        if (Cur == '0' && (Peek() == 'x' || Peek() == 'X'))
        {
            _pos += 2;
            while (true)
            {
                char c = Cur;
                if (char.IsAsciiHexDigit(c) || c == '.') _pos++;
                else if ((c == 'p' || c == 'P'))
                {
                    _pos++;
                    if (Cur == '+' || Cur == '-') _pos++;
                }
                else break;
            }
        }
        else
        {
            while (true)
            {
                char c = Cur;
                if (char.IsAsciiDigit(c) || c == '.') _pos++;
                else if (c == 'e' || c == 'E')
                {
                    _pos++;
                    if (Cur == '+' || Cur == '-') _pos++;
                }
                else break;
            }
        }
        if (char.IsAsciiLetter(Cur) || Cur == '_')
        {
            while (char.IsAsciiLetterOrDigit(Cur) || Cur == '_') _pos++;
            throw Error("malformed number near '" + _src.Substring(s, _pos - s) + "'");
        }
        return _src.Substring(s, _pos - s);
    }

    private string ReadString(char quote)
    {
        int startLine = _line, startCol = _pos - _lineStart + 1;
        _pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _src.Length) throw Error("unfinished string", startLine, startCol);
            char c = _src[_pos];
            if (c == quote) { _pos++; break; }
            if (c == '\n' || c == '\r') throw Error("unfinished string", startLine, startCol);
            if (c != '\\') { sb.Append(c); _pos++; continue; }
            _pos++;
            c = Cur;
            switch (c)
            {
                case 'n': sb.Append('\n'); _pos++; break;
                case 't': sb.Append('\t'); _pos++; break;
                case 'r': sb.Append('\r'); _pos++; break;
                case 'a': sb.Append('\a'); _pos++; break;
                case 'b': sb.Append('\b'); _pos++; break;
                case 'f': sb.Append('\f'); _pos++; break;
                case 'v': sb.Append('\v'); _pos++; break;
                case '\\': sb.Append('\\'); _pos++; break;
                case '"': sb.Append('"'); _pos++; break;
                case '\'': sb.Append('\''); _pos++; break;
                case '\n': case '\r': NewLine(); sb.Append('\n'); break;
                case 'x':
                    {
                        _pos++;
                        int v = 0;
                        for (int i = 0; i < 2; i++)
                        {
                            int h = Vm.LuaNumber.HexVal(Cur);
                            if (h < 0) throw Error("hexadecimal digit expected");
                            v = v * 16 + h;
                            _pos++;
                        }
                        sb.Append((char)v);
                        break;
                    }
                case 'z':
                    _pos++;
                    while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos]))
                    {
                        if (_src[_pos] == '\n' || _src[_pos] == '\r') NewLine(); else _pos++;
                    }
                    break;
                case 'u':
                    {
                        _pos++;
                        if (Cur != '{') throw Error("missing '{' in \\u{xxxx}");
                        _pos++;
                        long v = 0;
                        int digits = 0;
                        while (Vm.LuaNumber.HexVal(Cur) >= 0)
                        {
                            v = v * 16 + Vm.LuaNumber.HexVal(Cur);
                            if (v > 0x7FFFFFFF) throw Error("UTF-8 value too large");
                            _pos++;
                            digits++;
                        }
                        if (digits == 0) throw Error("hexadecimal digit expected");
                        if (Cur != '}') throw Error("missing '}' in \\u{xxxx}");
                        _pos++;
                        Vm.LuaText.AppendUtf8(sb, v); // strings are bytes: emit the UTF-8 encoding
                        break;
                    }
                default:
                    if (char.IsAsciiDigit(c))
                    {
                        int v = 0;
                        for (int i = 0; i < 3 && char.IsAsciiDigit(Cur); i++)
                        {
                            v = v * 10 + (Cur - '0');
                            _pos++;
                        }
                        if (v > 255) throw Error("decimal escape too large");
                        sb.Append((char)v);
                    }
                    else throw Error("invalid escape sequence '\\" + c + "'");
                    break;
            }
        }
        return sb.ToString();
    }

    // Compound assignment (+=, ..=, ...) is an extension to Teal.
    private static readonly string[] Ops3 = { "...", "..=", "//=", "<<=", ">>=" };
    private static readonly string[] Ops2 = { "==", "~=", "!=", "|>", "??", "?.", "<=", ">=", "//", "::", "<<", ">>", "..", "+=", "-=", "*=", "/=", "%=", "^=", "&=", "|=" };
    private static readonly string[] CameoOps3 = { "...", "..=", "~/=", "**=", "<<=", ">>=" };
    private static readonly string[] CameoOps2 = { "==", "!=", "~=", "&&", "||", "|>", "??", "?.", "<=", ">=", "~/", "**", "::", "<<", ">>", "..", "+=", "-=", "*=", "/=", "%=", "^=", "&=", "|=" };
    private const string Ops1 = "+-*/%^#&~|<>=(){}[];:,.?!$";

    // At the opening quote of $"...". Splits the string into literal pieces and {expression}
    // sources. Inside an expression, quotes and nested braces belong to the expression, so
    // $"{t["key"]}" and $"{f({1, 2})}" work; {{ and }} are literal braces.
    private List<(bool IsExpr, string Text)> ReadInterpolated(Token t)
    {
        char quote = Cur;
        _pos++;
        var parts = new List<(bool, string)>();
        var literal = new StringBuilder(); // source text of the current literal piece, escapes included
        void Flush()
        {
            if (literal.Length == 0) return;
            // escapes are decoded exactly as in an ordinary string
            var sub = new Lexer(quote + literal.ToString() + quote, _file) { _line = t.Line };
            parts.Add((false, sub.ReadString(quote)));
            literal.Clear();
        }
        while (true)
        {
            if (_pos >= _src.Length || Cur == '\n' || Cur == '\r') throw Error("unfinished string", t.Line, t.Col);
            char c = Cur;
            if (c == quote) { _pos++; break; }
            if (c == '\\')
            {
                literal.Append(c);
                _pos++;
                if (_pos < _src.Length)
                {
                    if (Cur == '\n' || Cur == '\r') { literal.Append('\n'); NewLine(); }
                    else { literal.Append(Cur); _pos++; }
                }
                continue;
            }
            if (c == '}')
            {
                if (Peek() != '}') throw Error("'}' without '{' in an interpolated string (write '}}' for a literal brace)", t.Line, t.Col);
                literal.Append('}');
                _pos += 2;
                continue;
            }
            if (c != '{') { literal.Append(c); _pos++; continue; }
            if (Peek() == '{') { literal.Append('{'); _pos += 2; continue; }
            Flush();
            _pos++;
            int start = _pos, depth = 1;
            while (true)
            {
                if (_pos >= _src.Length) throw Error("unfinished '{' in an interpolated string (write '{{' for a literal brace)", t.Line, t.Col);
                char e = Cur;
                if (e == '\n' || e == '\r') { NewLine(); continue; }
                if (e == '"' || e == '\'')
                {
                    // a string inside the expression: its braces and quotes are its own
                    _pos++;
                    while (_pos < _src.Length && Cur != e && Cur != '\n' && Cur != '\r')
                    {
                        if (Cur == '\\') _pos++;
                        _pos++;
                    }
                    if (_pos >= _src.Length || Cur != e) throw Error("unfinished string in an interpolated expression", t.Line, t.Col);
                    _pos++;
                    continue;
                }
                if (e == '{') depth++;
                else if (e == '}' && --depth == 0) break;
                _pos++;
            }
            string source = _src.Substring(start, _pos - start);
            if (source.Trim().Length == 0) throw Error("empty '{}' in an interpolated string", t.Line, t.Col);
            parts.Add((true, source));
            _pos++;
        }
        Flush();
        return parts;
    }

    // At the '[' of $[[ ... ]]. Like a long string, the text is taken as written (no escapes,
    // a first line break is dropped); {expr} is interpolated and {{ }} are literal braces.
    private List<(bool IsExpr, string Text)> ReadInterpolatedLong(Token t, int level)
    {
        _pos += level + 2;
        return ReadInterpolatedDelimited(t, "]" + new string('=', level) + "]", "unfinished long string");
    }

    // At the opening '"' of $""" ... """. Raw multi-line interpolated string in Cameo.
    private List<(bool IsExpr, string Text)> ReadInterpolatedTripleQuote(Token t)
    {
        _pos += 3;
        return ReadInterpolatedDelimited(t, "\"\"\"", "unfinished multi-line string");
    }

    private List<(bool IsExpr, string Text)> ReadInterpolatedDelimited(Token t, string close, string eofError)
    {
        if (Cur == '\r' || Cur == '\n') NewLine();
        var parts = new List<(bool, string)>();
        var literal = new StringBuilder();
        while (true)
        {
            if (_pos >= _src.Length) throw Error(eofError, t.Line, t.Col);
            if (string.CompareOrdinal(_src, _pos, close, 0, close.Length) == 0) { _pos += close.Length; break; }
            char c = Cur;
            if (c == '\n' || c == '\r') { literal.Append('\n'); NewLine(); continue; }
            if (c == '}')
            {
                if (Peek() != '}') throw Error("'}' without '{' in an interpolated string (write '}}' for a literal brace)", _line, _pos - _lineStart + 1);
                literal.Append('}');
                _pos += 2;
                continue;
            }
            if (c != '{') { literal.Append(c); _pos++; continue; }
            if (Peek() == '{') { literal.Append('{'); _pos += 2; continue; }
            if (literal.Length > 0) { parts.Add((false, literal.ToString())); literal.Clear(); }
            _pos++;
            int start = _pos, depth = 1;
            while (true)
            {
                if (_pos >= _src.Length) throw Error("unfinished '{' in an interpolated string (write '{{' for a literal brace)", t.Line, t.Col);
                char e = Cur;
                if (e == '\n' || e == '\r') { NewLine(); continue; }
                if (e == '"' || e == '\'')
                {
                    _pos++;
                    while (_pos < _src.Length && Cur != e && Cur != '\n' && Cur != '\r')
                    {
                        if (Cur == '\\') _pos++;
                        _pos++;
                    }
                    if (_pos >= _src.Length || Cur != e) throw Error("unfinished string in an interpolated expression", t.Line, t.Col);
                    _pos++;
                    continue;
                }
                if (e == '{') depth++;
                else if (e == '}' && --depth == 0) break;
                _pos++;
            }
            string source = _src.Substring(start, _pos - start);
            if (source.Trim().Length == 0) throw Error("empty '{}' in an interpolated string", t.Line, t.Col);
            parts.Add((true, source));
            _pos++;
        }
        if (literal.Length > 0) parts.Add((false, literal.ToString()));
        return parts;
    }

    private string ReadOp()
    {
        if (_cameo && Cur == '~' && Peek() == '/' && _prev is { Kind: TokenKind.Op, Text: "=" or "!" } && _prev.End == _pos)
        {
            _pos++;
            return "~";
        }
        string[] ops3 = _cameo ? CameoOps3 : Ops3;
        string[] ops2 = _cameo ? CameoOps2 : Ops2;
        foreach (string op in ops3)
            if (string.CompareOrdinal(_src, _pos, op, 0, 3) == 0) { _pos += 3; return op; }
        foreach (string op in ops2)
            if (string.CompareOrdinal(_src, _pos, op, 0, 2) == 0) { _pos += 2; return op; }
        char c = Cur;
        if (Ops1.IndexOf(c) >= 0) { _pos++; return c.ToString(); }
        throw Error($"unexpected character '{c}'");
    }
}
