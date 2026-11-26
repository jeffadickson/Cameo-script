using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// Everyday string helpers that Lua leaves out, added to the <c>string</c> table (so they work
/// as methods: <c>s:split(",")</c>) and UTF-8-aware case mapping and slicing in <c>utf8</c>.
/// Separators and prefixes are literal text, not patterns.
/// </summary>
public static class StringExtLib
{
    public static void Open(LuaState L)
    {
        Table s = L.Globals.GetStr("string").AsTable;
        L.RegisterInstance<string>(s, "split", Split);
        L.RegisterInstance<string>(s, "lines", static (ref CallContext c) =>
        {
            string str = c.CheckString(0);
            var t = new Table();
            int pos = 0;
            while (pos < str.Length)
            {
                int nl = str.IndexOf('\n', pos);
                int end = nl < 0 ? str.Length : nl;
                int stop = end > pos && str[end - 1] == '\r' ? end - 1 : end;
                t.Append(Value.FromString(str.Substring(pos, stop - pos)));
                pos = end + 1;
            }
            return c.Return(Value.FromTable(t));
        });
        L.RegisterInstance<string>(s, "trim", static (ref CallContext c) => c.Return(Trim(c.CheckString(0), true, true)));
        L.RegisterInstance<string>(s, "ltrim", static (ref CallContext c) => c.Return(Trim(c.CheckString(0), true, false)));
        L.RegisterInstance<string>(s, "rtrim", static (ref CallContext c) => c.Return(Trim(c.CheckString(0), false, true)));
        L.RegisterInstance<string>(s, "startswith", static (ref CallContext c) => c.Return(c.CheckString(0).StartsWith(c.CheckString(1), StringComparison.Ordinal)));
        L.RegisterInstance<string>(s, "endswith", static (ref CallContext c) => c.Return(c.CheckString(0).EndsWith(c.CheckString(1), StringComparison.Ordinal)));
        L.RegisterInstance<string>(s, "contains", static (ref CallContext c) => c.Return(c.CheckString(0).Contains(c.CheckString(1), StringComparison.Ordinal)));
        L.RegisterInstance<string>(s, "join", Join);
        L.RegisterInstance<string>(s, "replace", Replace);

        Table u = L.Globals.GetStr("utf8").AsTable;
        L.Register(u, "upper", static (ref CallContext c) => c.Return(MapCase(c.CheckString(0), upper: true)));
        L.Register(u, "lower", static (ref CallContext c) => c.Return(MapCase(c.CheckString(0), upper: false)));
        L.Register(u, "sub", Utf8Sub);
    }

    private static bool IsSpace(char ch) => ch == ' ' || (ch >= '\t' && ch <= '\r');

    private static string Trim(string s, bool left, bool right)
    {
        int a = 0, b = s.Length;
        if (left) while (a < b && IsSpace(s[a])) a++;
        if (right) while (b > a && IsSpace(s[b - 1])) b--;
        return a == 0 && b == s.Length ? s : s.Substring(a, b - a);
    }

    // split(s [, sep [, max]]): pieces of s between occurrences of sep. Without sep, splits on
    // runs of whitespace and drops empty pieces. max bounds the number of splits.
    private static int Split(ref CallContext c)
    {
        string s = c.CheckString(0);
        long max = c.OptInt(2, long.MaxValue);
        var t = new Table();
        if (c.IsNoneOrNil(1))
        {
            int i = 0;
            while (true)
            {
                while (i < s.Length && IsSpace(s[i])) i++;
                if (i >= s.Length) break;
                if (max-- <= 0) { t.Append(Value.FromString(Trim(s.Substring(i), false, true))); break; }
                int start = i;
                while (i < s.Length && !IsSpace(s[i])) i++;
                t.Append(Value.FromString(s.Substring(start, i - start)));
            }
            return c.Return(Value.FromTable(t));
        }
        string sep = c.CheckString(1);
        if (sep.Length == 0) throw c.ArgError(1, "separator must not be empty");
        int pos = 0;
        while (max-- > 0)
        {
            int at = s.IndexOf(sep, pos, StringComparison.Ordinal);
            if (at < 0) break;
            t.Append(Value.FromString(s.Substring(pos, at - pos)));
            pos = at + sep.Length;
        }
        t.Append(Value.FromString(s.Substring(pos)));
        return c.Return(Value.FromTable(t));
    }

    // join(sep, list): the reverse of split; elements are strings or numbers.
    private static int Join(ref CallContext c)
    {
        string sep = c.CheckString(0);
        Table t = c.CheckTable(1);
        int n = t.Length;
        var sb = new StringBuilder();
        for (int i = 1; i <= n; i++)
        {
            Value v = t.GetInt(i);
            if (!v.IsString && !v.IsNumber)
                throw c.ArgError(1, $"element {i} is a {LuaState.TypeNameOf(v)}, not a string or number");
            if (i > 1) sb.Append(sep);
            sb.Append(v.ToString());
            c.L.CheckStringLength(sb.Length);
        }
        return c.Return(sb.ToString());
    }

    // replace(s, old, new [, max]): literal replacement; returns the string and the number of replacements.
    private static int Replace(ref CallContext c)
    {
        string s = c.CheckString(0), old = c.CheckString(1), rep = c.CheckString(2);
        long max = c.OptInt(3, long.MaxValue);
        if (old.Length == 0) throw c.ArgError(1, "text to replace must not be empty");
        var sb = new StringBuilder();
        int pos = 0;
        long count = 0;
        while (count < max)
        {
            int at = s.IndexOf(old, pos, StringComparison.Ordinal);
            if (at < 0) break;
            sb.Append(s, pos, at - pos).Append(rep);
            c.L.CheckStringLength(sb.Length);
            pos = at + old.Length;
            count++;
        }
        if (count == 0) return c.Return(s, 0);
        sb.Append(s, pos, s.Length - pos);
        c.L.CheckStringLength(sb.Length);
        return c.Return(sb.ToString(), count);
    }

    // Decodes the sequence at s[i] (bytes held one per char). Returns its length, or 0 when
    // the bytes there are not valid UTF-8.
    private static int DecodeRune(string s, int i, out Rune rune)
    {
        Span<byte> buf = stackalloc byte[4];
        int n = Math.Min(4, s.Length - i);
        for (int k = 0; k < n; k++) buf[k] = (byte)s[i + k];
        return Rune.DecodeFromUtf8(buf.Slice(0, n), out rune, out int used) == System.Buffers.OperationStatus.Done ? used : 0;
    }

    // Unicode case mapping of UTF-8 text. Bytes that are not valid UTF-8 pass through unchanged.
    private static string MapCase(string s, bool upper)
    {
        bool ascii = true;
        foreach (char ch in s) if (ch >= 0x80) { ascii = false; break; }
        var sb = new StringBuilder(s.Length);
        Span<byte> outBuf = stackalloc byte[4];
        for (int i = 0; i < s.Length;)
        {
            char ch = s[i];
            if (ascii || ch < 0x80)
            {
                sb.Append(upper ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
                i++;
                continue;
            }
            int len = DecodeRune(s, i, out Rune r);
            if (len == 0) { sb.Append(ch); i++; continue; }
            Rune mapped = upper ? Rune.ToUpperInvariant(r) : Rune.ToLowerInvariant(r);
            int n = mapped.EncodeToUtf8(outBuf);
            for (int k = 0; k < n; k++) sb.Append((char)outBuf[k]);
            i += len;
        }
        return sb.ToString();
    }

    // utf8.sub(s, i [, j]): like string.sub, counting characters instead of bytes. An invalid
    // byte counts as one character.
    private static int Utf8Sub(ref CallContext c)
    {
        string s = c.CheckString(0);
        long i = c.CheckInt(1), j = c.OptInt(2, -1);
        // byte offset of each character start
        var starts = new List<int>(s.Length);
        for (int p = 0; p < s.Length;)
        {
            starts.Add(p);
            int len = s[p] < 0x80 ? 1 : DecodeRune(s, p, out _);
            p += len == 0 ? 1 : len;
        }
        long n = starts.Count;
        if (i < 0) i = Math.Max(n + i + 1, 1); else if (i == 0) i = 1;
        if (j < 0) j = n + j + 1; else if (j > n) j = n;
        if (i > j) return c.Return("");
        int from = starts[(int)i - 1];
        int to = j == n ? s.Length : starts[(int)j];
        return c.Return(s.Substring(from, to - from));
    }
}
