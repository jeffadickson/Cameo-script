using System.Text;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;

namespace Cameo;

/// <summary>Result of formatting one Cameo source string.</summary>
/// <param name="Formatted">Canonical formatted text when <see cref="Diagnostic"/> is null.</param>
/// <param name="Changed">True when the formatted output differs from the input.</param>
/// <param name="Diagnostic">Syntax error preventing formatting, or null on success.</param>
public sealed record CameoFormatOutcome(string? Formatted, bool Changed, CameoDiagnostic? Diagnostic);

/// <summary>Deterministic, comment- and literal-preserving source formatter for Cameo.</summary>
public static class CameoFormatter
{
    private const int IndentWidth = 4;

    /// <summary>Formats Cameo source into canonical 4-space style, throwing on syntax errors.</summary>
    public static string Format(string source, string fileName = "<format>")
    {
        ArgumentNullException.ThrowIfNull(source);
        CameoFormatOutcome outcome = TryFormat(source, fileName);
        if (outcome.Diagnostic != null)
            throw new CameoSyntaxException(new Diagnostic(
                outcome.Diagnostic.File ?? fileName,
                outcome.Diagnostic.Line ?? 1,
                outcome.Diagnostic.Column ?? 1,
                outcome.Diagnostic.Message));
        return outcome.Formatted!;
    }

    /// <summary>Migrates legacy Lua-surface Cameo source to clean Cameo syntax and formats it, throwing on syntax errors.</summary>
    public static string Migrate(string source, string fileName = "<migrate>")
    {
        ArgumentNullException.ThrowIfNull(source);
        CameoFormatOutcome outcome = TryMigrate(source, fileName);
        if (outcome.Diagnostic != null)
            throw new CameoSyntaxException(new Diagnostic(
                outcome.Diagnostic.File ?? fileName,
                outcome.Diagnostic.Line ?? 1,
                outcome.Diagnostic.Column ?? 1,
                outcome.Diagnostic.Message));
        return outcome.Formatted!;
    }

    /// <summary>Migrates legacy Lua-surface Cameo source to clean Cameo syntax and formats it, returning a structured diagnostic on syntax errors.</summary>
    public static CameoFormatOutcome TryMigrate(string source, string fileName = "<migrate>")
    {
        ArgumentNullException.ThrowIfNull(source);
        string migrated = CameoMigrator.MigrateSource(source);
        CameoFormatOutcome formatted = TryFormat(migrated, fileName);
        if (formatted.Diagnostic != null) return formatted;
        return new CameoFormatOutcome(formatted.Formatted, !string.Equals(source, formatted.Formatted, StringComparison.Ordinal), null);
    }

    /// <summary>Formats Cameo source into canonical style, returning a structured diagnostic on syntax errors.</summary>
    public static CameoFormatOutcome TryFormat(string source, string fileName = "<format>")
    {
        ArgumentNullException.ThrowIfNull(source);
        string normalized = NormalizeLineEndings(source);
        try
        {
            Parser.ParseCameo(LuaText.Encode(normalized), fileName);
        }
        catch (CameoSyntaxException ex)
        {
            Diagnostic d = ex.Diagnostic;
            return new CameoFormatOutcome(null, false,
                new CameoDiagnostic("CAMEOSYNTAX", LuaText.Decode(d.Message), fileName, d.Line, d.Col));
        }

        List<Token> tokens = Lexer.Tokenize(normalized, fileName, cameo: true);
        string formatted = FormatNormalized(normalized, tokens);

        // Safety invariant: formatted output must always remain syntactically valid Cameo.
        Parser.ParseCameo(LuaText.Encode(formatted), fileName);
        return new CameoFormatOutcome(formatted, !string.Equals(source, formatted, StringComparison.Ordinal), null);
    }

    private static string NormalizeLineEndings(string s)
    {
        if (!s.Contains('\r')) return s;
        return s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string FormatNormalized(string src, List<Token> tokens)
    {
        if (src.Length == 0) return "";

        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < src.Length; i++)
            if (src[i] == '\n') lineStarts.Add(i + 1);

        int lineCount = lineStarts.Count;
        var lines = new string[lineCount];
        for (int i = 0; i < lineCount; i++)
        {
            int start = lineStarts[i];
            int end = i + 1 < lineCount ? lineStarts[i + 1] - 1 : src.Length;
            lines[i] = src[start..end];
        }

        // Identify lines that begin strictly inside a multi-line token (long string,
        // interpolated long string, macro quote) or multi-line block comment (--[=[...]=]).
        var preserveLine = new bool[lineCount];
        var tokensByLine = new List<Token>[lineCount];
        for (int i = 0; i < lineCount; i++) tokensByLine[i] = [];

        int prevEnd = 0;
        foreach (Token tok in tokens)
        {
            MarkBlockCommentsInGap(src, prevEnd, tok.Pos, lineStarts, preserveLine);
            prevEnd = tok.End;

            if (tok.Kind == TokenKind.Eof) break;
            int tokLine = Math.Clamp(tok.Line - 1, 0, lineCount - 1);
            tokensByLine[tokLine].Add(tok);

            if (tok.End > tok.Pos)
                MarkInteriorLines(tok.Pos, tok.End, lineStarts, preserveLine);
        }

        var output = new StringBuilder(src.Length + 64);
        var stack = new Stack<(int CloseIndent, int BodyIndent, bool IsKeyword)>();
        bool emittedAny = false;
        bool pendingBlank = false;
        Token? prevNonEof = null;

        for (int i = 0; i < lineCount; i++)
        {
            List<Token> lineTokens = tokensByLine[i];

            if (preserveLine[i])
            {
                if (pendingBlank && emittedAny)
                {
                    output.Append('\n');
                    pendingBlank = false;
                }
                output.Append(lines[i]).Append('\n');
                emittedAny = true;
                int baseIndent = stack.Count > 0 ? stack.Peek().BodyIndent : 0;
                ApplyLineTokens(lineTokens, 0, baseIndent, false, stack, ref prevNonEof);
                continue;
            }

            string trimmed = lines[i].Trim(' ', '\t');
            if (trimmed.Length == 0)
            {
                if (emittedAny) pendingBlank = true;
                continue;
            }

            if (i == 0 && trimmed.StartsWith("#!", StringComparison.Ordinal))
            {
                output.Append(trimmed).Append('\n');
                emittedAny = true;
                continue;
            }

            if (pendingBlank && emittedAny)
            {
                output.Append('\n');
                pendingBlank = false;
            }

            // Phase A: leading closing delimiters dedent the current line.
            int k = 0;
            bool poppedCloser = false;
            int closeIndent = 0;
            while (k < lineTokens.Count && IsCloser(lineTokens[k]))
            {
                if (TryPopForCloser(stack, lineTokens[k], out var popped))
                {
                    closeIndent = popped.CloseIndent;
                    poppedCloser = true;
                }
                else
                {
                    closeIndent = 0;
                    poppedCloser = true;
                }
                prevNonEof = lineTokens[k];
                k++;
            }

            bool leadingMidKeyword = k == 0 && lineTokens.Count > 0 && IsMidBlockKeyword(lineTokens[0]);
            bool inElseIf = k == 0 && lineTokens.Count > 0 && lineTokens[0] is { Kind: TokenKind.Keyword, Text: "elseif" };

            int lineIndent;
            if (poppedCloser)
            {
                lineIndent = closeIndent;
            }
            else if (leadingMidKeyword && stack.Count > 0 && stack.Peek().IsKeyword)
            {
                lineIndent = stack.Peek().CloseIndent;
            }
            else
            {
                lineIndent = stack.Count > 0 ? stack.Peek().BodyIndent : 0;
            }

            // Hanging continuation lines starting with '.', '?.', or '|>' indent one level.
            if (!poppedCloser && !leadingMidKeyword && lineTokens.Count > 0 && IsContinuationLead(lineTokens[0]))
                lineIndent++;

            if (lineIndent > 0) output.Append(' ', lineIndent * IndentWidth);
            output.Append(trimmed).Append('\n');
            emittedAny = true;

            // Phase B: update nesting stack for remaining tokens on this line.
            ApplyLineTokens(lineTokens, k, lineIndent, inElseIf, stack, ref prevNonEof);
        }

        return emittedAny ? output.ToString() : "";
    }

    private static void ApplyLineTokens(
        List<Token> lineTokens,
        int startIndex,
        int lineIndent,
        bool inElseIf,
        Stack<(int CloseIndent, int BodyIndent, bool IsKeyword)> stack,
        ref Token? prevNonEof)
    {
        bool lineHasBrace = false;
        for (int j = startIndex; j < lineTokens.Count; j++)
        {
            if (lineTokens[j] is { Kind: TokenKind.Op, Text: "{" })
            {
                lineHasBrace = true;
                break;
            }
        }

        for (int j = startIndex; j < lineTokens.Count; j++)
        {
            Token tok = lineTokens[j];
            if (IsDelimOpener(tok))
            {
                stack.Push((lineIndent, lineIndent + 1, false));
            }
            else if (IsDelimCloser(tok))
            {
                TryPopForCloser(stack, tok, out _);
            }
            else if (tok.Kind == TokenKind.Keyword)
            {
                switch (tok.Text)
                {
                    case "then":
                        if (inElseIf) inElseIf = false;
                        else stack.Push((lineIndent, lineIndent + 1, true));
                        break;
                    case "do":
                    case "repeat":
                        stack.Push((lineIndent, lineIndent + 1, true));
                        break;
                    case "function":
                        if (!IsTypeContextPrefix(prevNonEof))
                            stack.Push((lineIndent, lineIndent + 1, true));
                        break;
                    case "end":
                    case "until":
                        TryPopForCloser(stack, tok, out _);
                        break;
                }
            }
            else if (!lineHasBrace && tok.Kind == TokenKind.Name &&
                     tok.Text is "record" or "enum" or "interface" &&
                     (prevNonEof is { Kind: TokenKind.Keyword, Text: "local" } ||
                      prevNonEof is { Kind: TokenKind.Name, Text: "global" }))
            {
                // Legacy Teal `local record ... end` / `global enum ... end`
                stack.Push((lineIndent, lineIndent + 1, true));
            }

            prevNonEof = tok;
        }
    }

    private static bool IsTypeContextPrefix(Token? prev) => prev != null &&
        ((prev.Kind == TokenKind.Op && prev.Text is ":" or "->" or "|" or "<" or "(" or "," or "{") ||
         (prev.Kind == TokenKind.Name && prev.Text is "is" or "as" or "type"));

    private static bool IsDelimOpener(Token t) => t.Kind == TokenKind.Op && t.Text is "{" or "[" or "(";
    private static bool IsDelimCloser(Token t) => t.Kind == TokenKind.Op && t.Text is "}" or "]" or ")";
    private static bool IsKeywordCloser(Token t) => t.Kind == TokenKind.Keyword && t.Text is "end" or "until";
    private static bool IsCloser(Token t) => IsDelimCloser(t) || IsKeywordCloser(t);
    private static bool IsMidBlockKeyword(Token t) => t.Kind == TokenKind.Keyword && t.Text is "else" or "elseif";
    private static bool IsContinuationLead(Token t) => t.Kind == TokenKind.Op && t.Text is "." or "?." or "|>";

    private static bool TryPopForCloser(
        Stack<(int CloseIndent, int BodyIndent, bool IsKeyword)> stack,
        Token closer,
        out (int CloseIndent, int BodyIndent, bool IsKeyword) popped)
    {
        if (stack.Count == 0)
        {
            popped = default;
            return false;
        }
        bool wantKeyword = IsKeywordCloser(closer);
        if (stack.Peek().IsKeyword == wantKeyword)
        {
            popped = stack.Pop();
            return true;
        }
        popped = stack.Pop();
        return true;
    }

    private static void MarkInteriorLines(int startPos, int endPos, List<int> lineStarts, bool[] preserveLine)
    {
        int idx = lineStarts.BinarySearch(startPos + 1);
        if (idx < 0) idx = ~idx;
        while (idx < lineStarts.Count && lineStarts[idx] < endPos)
        {
            preserveLine[idx] = true;
            idx++;
        }
    }

    private static void MarkBlockCommentsInGap(string src, int gapStart, int gapEnd, List<int> lineStarts, bool[] preserveLine)
    {
        int p = gapStart;
        while (p + 1 < gapEnd)
        {
            if (src[p] == '/' && src[p + 1] == '/')
            {
                p += 2;
                while (p < gapEnd && src[p] != '\n') p++;
            }
            else if (src[p] == '/' && src[p + 1] == '*')
            {
                int commentStart = p;
                p += 2;
                int depth = 1;
                while (p < gapEnd && depth > 0)
                {
                    if (p + 1 < gapEnd && src[p] == '/' && src[p + 1] == '*') { p += 2; depth++; }
                    else if (p + 1 < gapEnd && src[p] == '*' && src[p + 1] == '/') { p += 2; depth--; }
                    else p++;
                }
                MarkInteriorLines(commentStart, p, lineStarts, preserveLine);
            }
            else
            {
                p++;
            }
        }
    }
}
