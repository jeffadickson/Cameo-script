using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// The <c>utf8</c> library (a port of Lua 5.4's lutf8lib). VM strings are byte strings, so
/// positions and offsets are byte positions, exactly as in Lua.
/// </summary>
public static class Utf8Lib
{
    private const long MaxUnicode = 0x10FFFF;
    private const long MaxUtf = 0x7FFFFFFF;

    public static void Open(LuaState L)
    {
        var u = new Table();
        L.Register(u, "char", Char);
        L.Register(u, "codepoint", Codepoint);
        L.Register(u, "len", Len);
        L.Register(u, "offset", Offset);
        L.Register(u, "codes", Codes);
        u.SetStr("charpattern", "[\0-\x7F\xC2-\xFD][\x80-\xBF]*");
        L.Globals.SetStr("utf8", u);
        L.LoadedModules.SetStr("utf8", u);
    }

    private static bool IsCont(string s, int i) => i < s.Length && (s[i] & 0xC0) == 0x80;

    private static long UPos(long pos, int len) => pos >= 0 ? pos : (-pos > len ? 0 : len + pos + 1);

    private static readonly long[] Limits = { long.MaxValue, 0x80, 0x800, 0x10000, 0x200000, 0x4000000 };

    /// <summary>Decodes one sequence at byte index i (0-based). Returns its length, or 0 if invalid.</summary>
    // Verified: verification/Utf8.dfy, lemmas RoundTrip and NoOverlong
    private static int Decode(string s, int i, bool strict, out long code)
    {
        code = 0;
        if (i >= s.Length) return 0;
        int c = s[i];
        long res = 0;
        if (c < 0x80) { code = c; return 1; }
        int count = 0;
        for (; (c & 0x40) != 0; c <<= 1)
        {
            int idx = i + ++count;
            if (idx >= s.Length) return 0;
            int cc = s[idx];
            if ((cc & 0xC0) != 0x80) return 0;
            res = (res << 6) | (long)(cc & 0x3F);
        }
        res |= (long)(c & 0x7F) << (count * 5);
        if (count > 5 || res > MaxUtf || res < Limits[count]) return 0;
        if (strict && (res > MaxUnicode || (res >= 0xD800 && res <= 0xDFFF))) return 0;
        code = res;
        return count + 1;
    }

    private static int Char(ref CallContext c)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < c.Count; i++)
        {
            long cp = c.CheckInt(i);
            if ((ulong)cp > (ulong)MaxUtf) throw c.ArgError(i, "value out of range");
            LuaText.AppendUtf8(sb, cp);
        }
        return c.Return(sb.ToString());
    }

    private static int Len(ref CallContext c)
    {
        string s = c.CheckString(0);
        long posi = UPos(c.OptInt(1, 1), s.Length);
        long posj = UPos(c.OptInt(2, -1), s.Length);
        bool lax = c.ToBoolean(3);
        if (!(1 <= posi && --posi <= s.Length)) throw c.ArgError(1, "initial position out of bounds");
        if (!(--posj < s.Length)) throw c.ArgError(2, "final position out of bounds");
        long n = 0;
        while (posi <= posj)
        {
            int len = Decode(s, (int)posi, !lax, out _);
            if (len == 0) return c.Return(Value.Nil, Value.FromInt(posi + 1));
            posi += len;
            n++;
        }
        return c.Return(Value.FromInt(n));
    }

    private static int Codepoint(ref CallContext c)
    {
        string s = c.CheckString(0);
        long posi = UPos(c.OptInt(1, 1), s.Length);
        long pose = UPos(c.OptInt(2, posi), s.Length);
        bool lax = c.ToBoolean(3);
        if (posi < 1) throw c.ArgError(1, "out of bounds");
        if (pose > s.Length) throw c.ArgError(2, "out of bounds");
        if (posi > pose) return 0;
        var res = new List<Value>();
        int i = (int)posi - 1;
        while (i < pose)
        {
            int len = Decode(s, i, !lax, out long code);
            if (len == 0) throw new LuaException("invalid UTF-8 code");
            res.Add(Value.FromInt(code));
            i += len;
        }
        return c.Return(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(res));
    }

    private static int Offset(ref CallContext c)
    {
        string s = c.CheckString(0);
        long n = c.CheckInt(1);
        long posi = n >= 0 ? 1 : s.Length + 1;
        posi = UPos(c.OptInt(2, posi), s.Length);
        if (!(1 <= posi && --posi <= s.Length)) throw c.ArgError(2, "position out of bounds");
        int p = (int)posi;
        if (n == 0)
        {
            while (p > 0 && IsCont(s, p)) p--;
        }
        else
        {
            if (IsCont(s, p)) throw new LuaException("initial position is a continuation byte");
            if (n < 0)
            {
                while (n < 0 && p > 0)
                {
                    do { p--; } while (p > 0 && IsCont(s, p));
                    n++;
                }
            }
            else
            {
                n--;
                while (n > 0 && p < s.Length)
                {
                    do { p++; } while (IsCont(s, p));
                    n--;
                }
            }
        }
        return n == 0 ? c.Return(Value.FromInt(p + 1)) : c.Return(Value.Nil);
    }

    private static int Codes(ref CallContext c)
    {
        string s0 = c.CheckString(0);
        bool lax = c.ToBoolean(1);
        if (IsCont(s0, 0)) throw c.ArgError(0, "invalid UTF-8 code");
        var iter = new NativeFunction("codes_iter", (ref CallContext cc) =>
        {
            string s = cc.CheckString(0);
            long i = cc.CheckInt(1);
            int n = (int)i;
            // skip the current byte and its continuation bytes
            while (IsCont(s, n)) n++;
            if (n >= s.Length) return 0;
            int len = Decode(s, n, !lax, out long code);
            if (len == 0 || IsCont(s, n + len)) throw new LuaException("invalid UTF-8 code");
            return cc.Return(Value.FromInt(n + 1), Value.FromInt(code));
        });
        return c.Return(Value.FromFunction(iter), c[0], Value.FromInt(0));
    }
}
