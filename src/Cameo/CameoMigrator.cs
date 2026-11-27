using System.Text;

namespace Cameo;

/// <summary>
/// Lexical, structure-aware migrator that rewrites legacy Lua-surface Cameo source
/// (<c>--</c>, <c>--[[...]]</c>, <c>not</c>/<c>and</c>/<c>or</c>/<c>~=</c>, <c>^</c>/<c>~</c>,
/// <c>//</c>, <c>[[...]]</c>, <c>elseif</c>) to the clean Cameo surface syntax.
/// </summary>
internal static class CameoMigrator
{
    private enum PrevKind { None, Value, OpOrKeyword }

    public static string MigrateSource(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        var sb = new StringBuilder(source.Length + 32);
        int i = 0;
        int n = source.Length;
        var prev = PrevKind.None;

        // Preserve leading shebang verbatim.
        if (source.StartsWith("#!", StringComparison.Ordinal))
        {
            while (i < n && source[i] != '\n') sb.Append(source[i++]);
        }

        while (i < n)
        {
            char c = source[i];

            // Whitespace
            if (c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Legacy line / block comments: --[[...]] or --[=[...]=] or --...
            if (c == '-' && i + 1 < n && source[i + 1] == '-')
            {
                int level = LongBracketLevelAt(source, i + 2);
                if (level >= 0)
                {
                    int bodyStart = i + 2 + level + 2;
                    string close = "]" + new string('=', level) + "]";
                    int closeIdx = source.IndexOf(close, bodyStart, StringComparison.Ordinal);
                    if (closeIdx >= 0)
                    {
                        sb.Append("/*");
                        sb.Append(source, bodyStart, closeIdx - bodyStart);
                        sb.Append("*/");
                        i = closeIdx + close.Length;
                        continue;
                    }
                }
                sb.Append("//");
                i += 2;
                while (i < n && source[i] != '\n' && source[i] != '\r')
                    sb.Append(source[i++]);
                continue;
            }

            // Already-migrated // or /* ... */ comments: preserve verbatim.
            if (c == '/' && i + 1 < n && source[i + 1] == '*' && prev != PrevKind.Value)
            {
                sb.Append("/*");
                i += 2;
                int depth = 1;
                while (i < n && depth > 0)
                {
                    if (source[i] == '/' && i + 1 < n && source[i + 1] == '*')
                    {
                        sb.Append("/*"); i += 2; depth++;
                    }
                    else if (source[i] == '*' && i + 1 < n && source[i + 1] == '/')
                    {
                        sb.Append("*/"); i += 2; depth--;
                    }
                    else sb.Append(source[i++]);
                }
                continue;
            }

            // Triple-quoted strings """...""" or $"""...""" (already migrated)
            if (c == '$' && i + 3 < n && source[i + 1] == '"' && source[i + 2] == '"' && source[i + 3] == '"')
            {
                i = CopyInterpolatedDelimited(source, i + 4, "\"\"\"", "$", "\"\"\"", sb);
                prev = PrevKind.Value;
                continue;
            }
            if (c == '"' && i + 2 < n && source[i + 1] == '"' && source[i + 2] == '"')
            {
                int closeIdx = source.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                int end = closeIdx >= 0 ? closeIdx + 3 : n;
                sb.Append(source, i, end - i);
                i = end;
                prev = PrevKind.Value;
                continue;
            }

            // Interpolated long string $[[...]] / $[=[...]=] -> $"""..."""
            if (c == '$' && i + 1 < n && source[i + 1] == '[')
            {
                int level = LongBracketLevelAt(source, i + 1);
                if (level >= 0)
                {
                    string close = "]" + new string('=', level) + "]";
                    i = CopyInterpolatedDelimited(source, i + 1 + level + 2, close, "$", "\"\"\"", sb);
                    prev = PrevKind.Value;
                    continue;
                }
            }

            // Interpolated quoted string $"..." / $'...'
            if (c == '$' && i + 1 < n && (source[i + 1] == '"' || source[i + 1] == '\''))
            {
                char quote = source[i + 1];
                sb.Append('$').Append(quote);
                i = CopyInterpolatedQuoted(source, i + 2, quote, sb);
                prev = PrevKind.Value;
                continue;
            }

            // Ordinary quoted string "..." / '...'
            if (c == '"' || c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < n)
                {
                    char ch = source[i++];
                    sb.Append(ch);
                    if (ch == '\\' && i < n) { sb.Append(source[i++]); continue; }
                    if (ch == c || ch == '\n') break;
                }
                prev = PrevKind.Value;
                continue;
            }

            // Legacy long string [[...]] / [=[...]=] -> """..."""
            if (c == '[' && i + 1 < n && (source[i + 1] == '[' || source[i + 1] == '='))
            {
                int level = LongBracketLevelAt(source, i);
                if (level >= 0)
                {
                    int bodyStart = i + level + 2;
                    string close = "]" + new string('=', level) + "]";
                    int closeIdx = source.IndexOf(close, bodyStart, StringComparison.Ordinal);
                    if (closeIdx >= 0)
                    {
                        sb.Append("\"\"\"");
                        sb.Append(source, bodyStart, closeIdx - bodyStart);
                        sb.Append("\"\"\"");
                        i = closeIdx + close.Length;
                        prev = PrevKind.Value;
                        continue;
                    }
                }
            }

            // Regex literal /pattern/flags when `/` cannot be division
            if (c == '/' && prev != PrevKind.Value && i + 1 < n && source[i + 1] is not ('/' or '*' or '='))
            {
                int start = i;
                int p = i + 1;
                bool inClass = false;
                bool closed = false;
                while (p < n)
                {
                    char ch = source[p++];
                    if (ch == '\n' || ch == '\r') break;
                    if (ch == '\\') { if (p < n) p++; continue; }
                    if (ch == '[' && !inClass) { inClass = true; continue; }
                    if (ch == ']' && inClass) { inClass = false; continue; }
                    if (ch == '/' && !inClass) { closed = true; break; }
                }
                if (closed)
                {
                    while (p < n && char.IsAsciiLetter(source[p])) p++;
                    sb.Append(source, start, p - start);
                    i = p;
                    prev = PrevKind.Value;
                    continue;
                }
            }

            // Numbers
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(source[i + 1])))
            {
                int start = i;
                if (c == '0' && i + 1 < n && source[i + 1] is 'x' or 'X')
                {
                    i += 2;
                    while (i < n)
                    {
                        char ch = source[i];
                        if (char.IsAsciiHexDigit(ch) || ch == '.') i++;
                        else if (ch is 'p' or 'P') { i++; if (i < n && source[i] is '+' or '-') i++; }
                        else break;
                    }
                }
                else
                {
                    while (i < n)
                    {
                        char ch = source[i];
                        if (char.IsAsciiDigit(ch) || ch == '.') i++;
                        else if (ch is 'e' or 'E') { i++; if (i < n && source[i] is '+' or '-') i++; }
                        else break;
                    }
                }
                sb.Append(source, start, i - start);
                prev = PrevKind.Value;
                continue;
            }

            // Identifiers / keywords
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++;
                string word = source[start..i];
                // Do not rewrite property access after `.` or `?.` or `:` (e.g. `obj.and`, `obj.not`).
                bool afterMember = sb.Length > 0 && sb[^1] is '.' or ':';
                if (!afterMember)
                {
                    switch (word)
                    {
                        case "not":
                            sb.Append('!');
                            // Collapse single space between `not` and its operand (`not x` -> `!x`).
                            if (i < n && source[i] == ' ' && i + 1 < n && source[i + 1] is not (' ' or '\t' or '\r' or '\n'))
                                i++;
                            prev = PrevKind.OpOrKeyword;
                            continue;
                        case "and":
                            sb.Append("&&");
                            prev = PrevKind.OpOrKeyword;
                            continue;
                        case "or":
                            sb.Append("||");
                            prev = PrevKind.OpOrKeyword;
                            continue;
                        case "elseif":
                            sb.Append("else if");
                            prev = PrevKind.OpOrKeyword;
                            continue;
                        case "local":
                            sb.Append("var");
                            prev = PrevKind.OpOrKeyword;
                            continue;
                    }
                }
                sb.Append(word);
                prev = word is "true" or "false" or "nil" ? PrevKind.Value
                    : IsControlKeyword(word) ? PrevKind.OpOrKeyword : PrevKind.Value;
                continue;
            }

            // Operators (check longest first)
            if (c == '/' && i + 2 < n && source[i + 1] == '/' && source[i + 2] == '=')
            {
                sb.Append("~/=");
                i += 3;
                prev = PrevKind.OpOrKeyword;
                continue;
            }
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                // If `//` follows a value expression, it was Lua floor division -> `~/`.
                // Otherwise (e.g. start of line) it is already a Cameo line comment.
                if (prev == PrevKind.Value)
                {
                    sb.Append("~/");
                    i += 2;
                    prev = PrevKind.OpOrKeyword;
                }
                else
                {
                    sb.Append("//");
                    i += 2;
                    while (i < n && source[i] != '\n' && source[i] != '\r')
                        sb.Append(source[i++]);
                }
                continue;
            }
            if (c == '~' && i + 1 < n && source[i + 1] == '=')
            {
                sb.Append("!=");
                i += 2;
                prev = PrevKind.OpOrKeyword;
                continue;
            }
            if (c == '^' && i + 1 < n && source[i + 1] == '=')
            {
                sb.Append("**=");
                i += 2;
                prev = PrevKind.OpOrKeyword;
                continue;
            }
            if (c == '^')
            {
                sb.Append("**");
                i++;
                prev = PrevKind.OpOrKeyword;
                continue;
            }
            if (c == '~')
            {
                // Preserve `=~`, `!~`, and `~/`.
                bool afterMatchPrefix = sb.Length > 0 && sb[^1] is '=' or '!';
                if (!afterMatchPrefix && !(i + 1 < n && source[i + 1] == '/') && prev == PrevKind.Value)
                {
                    sb.Append('^');
                    i++;
                    prev = PrevKind.OpOrKeyword;
                    continue;
                }
                sb.Append('~');
                i++;
                prev = PrevKind.OpOrKeyword;
                continue;
            }

            // Copy multi-char punctuation intact so prev tracking stays accurate.
            if (c == '.' && i + 2 < n && source[i + 1] == '.' && source[i + 2] == '.')
            {
                sb.Append("...");
                i += 3;
                prev = PrevKind.Value;
                continue;
            }

            sb.Append(c);
            i++;
            prev = c is ')' or ']' or '}' ? PrevKind.Value : PrevKind.OpOrKeyword;
        }

        return sb.ToString();
    }

    private static bool IsControlKeyword(string word) => word is
        "break" or "do" or "else" or "elseif" or "end" or "for" or "function" or "goto" or
        "if" or "in" or "local" or "repeat" or "return" or "then" or "until" or "while" or
        "let" or "var" or "fn" or "match" or "try" or "catch" or "is" or "as";

    private static int LongBracketLevelAt(string src, int at)
    {
        if (at >= src.Length || src[at] != '[') return -1;
        int p = at + 1;
        int level = 0;
        while (p < src.Length && src[p] == '=') { p++; level++; }
        return p < src.Length && src[p] == '[' ? level : -1;
    }

    private static int CopyInterpolatedQuoted(string src, int i, char quote, StringBuilder sb)
    {
        int n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == quote) { sb.Append(c); return i + 1; }
            if (c == '\\')
            {
                sb.Append(c); i++;
                if (i < n) sb.Append(src[i++]);
                continue;
            }
            if (c == '}' && i + 1 < n && src[i + 1] == '}') { sb.Append("}}"); i += 2; continue; }
            if (c == '{' && i + 1 < n && src[i + 1] == '{') { sb.Append("{{"); i += 2; continue; }
            if (c == '{')
            {
                i = CopyMigratedInterpolationExpr(src, i, sb);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return i;
    }

    private static int CopyInterpolatedDelimited(string src, int i, string close, string prefix, string outDelim, StringBuilder sb)
    {
        sb.Append(prefix).Append(outDelim);
        int n = src.Length;
        while (i < n)
        {
            if (string.CompareOrdinal(src, i, close, 0, close.Length) == 0)
            {
                sb.Append(outDelim);
                return i + close.Length;
            }
            char c = src[i];
            if (c == '}' && i + 1 < n && src[i + 1] == '}') { sb.Append("}}"); i += 2; continue; }
            if (c == '{' && i + 1 < n && src[i + 1] == '{') { sb.Append("{{"); i += 2; continue; }
            if (c == '{')
            {
                i = CopyMigratedInterpolationExpr(src, i, sb);
                continue;
            }
            sb.Append(c);
            i++;
        }
        sb.Append(outDelim);
        return i;
    }

    private static int CopyMigratedInterpolationExpr(string src, int openBrace, StringBuilder sb)
    {
        int n = src.Length;
        int i = openBrace + 1;
        int start = i;
        int depth = 1;
        while (i < n && depth > 0)
        {
            char e = src[i];
            if (e == '"' || e == '\'')
            {
                i++;
                while (i < n && src[i] != e && src[i] != '\n' && src[i] != '\r')
                {
                    if (src[i] == '\\') i++;
                    i++;
                }
                if (i < n && src[i] == e) i++;
                continue;
            }
            if (e == '{') depth++;
            else if (e == '}' && --depth == 0) break;
            i++;
        }
        string inner = src[start..i];
        sb.Append('{').Append(MigrateSource(inner)).Append('}');
        return i < n ? i + 1 : n;
    }
}
