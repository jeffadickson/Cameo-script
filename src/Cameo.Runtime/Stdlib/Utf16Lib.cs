using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// The <c>utf16</c> type and library: immutable UTF-16 strings backed directly by .NET strings.
/// Ordinary strings are bytes (UTF-8); <c>utf16.new(s)</c> converts one, <c>tostring(u)</c>
/// converts back. Positions and lengths of a utf16 value count UTF-16 code units.
/// </summary>
public static class Utf16Lib
{
    public static void Open(LuaState L)
    {
        var u = new Table();
        L.Register(u, "new", New);
        L.Register(u, "char", Char);
        L.RegisterInstance<Utf16String>(u, "len", Len);
        L.RegisterInstance<Utf16String>(u, "sub", Sub);
        L.RegisterInstance<Utf16String>(u, "upper", static (ref CallContext c) => Ret(ref c, Arg(ref c, 0).ToUpperInvariant()));
        L.RegisterInstance<Utf16String>(u, "lower", static (ref CallContext c) => Ret(ref c, Arg(ref c, 0).ToLowerInvariant()));
        L.RegisterInstance<Utf16String>(u, "rep", Rep);
        L.RegisterInstance<Utf16String>(u, "reverse", Reverse);
        L.RegisterInstance<Utf16String>(u, "unit", Unit);
        L.RegisterInstance<Utf16String>(u, "codepoint", Codepoint);
        L.RegisterInstance<Utf16String>(u, "find", Find);
        L.RegisterInstance<Utf16String>(u, "tostring", ToUtf8);
        L.Globals.SetStr("utf16", u);
        L.LoadedModules.SetStr("utf16", u);

        Table mt = L.Utf16Meta;
        mt.SetStr("__index", u);
        mt.SetStr("__name", "utf16");
        L.Register(mt, "__tostring", ToUtf8);
        L.Register(mt, "__len", Len);
        L.Register(mt, "__concat", static (ref CallContext c) => Ret(ref c, Arg(ref c, 0) + Arg(ref c, 1)));
        L.Register(mt, "__eq", static (ref CallContext c) => c.Return(c[0].O is Utf16String a && c[1].O is Utf16String b && a.Text == b.Text));
        L.Register(mt, "__lt", static (ref CallContext c) => c.Return(string.CompareOrdinal(Arg(ref c, 0), Arg(ref c, 1)) < 0));
        L.Register(mt, "__le", static (ref CallContext c) => c.Return(string.CompareOrdinal(Arg(ref c, 0), Arg(ref c, 1)) <= 0));
    }

    // A utf16 value, or a string/number converted from UTF-8.
    private static string Arg(ref CallContext c, int i)
    {
        Value v = c[i];
        if (v.O is Utf16String u) return u.Text;
        if (v.IsString || v.IsNumber) return LuaText.Decode(c.CheckString(i));
        throw c.TypeError(i, "utf16");
    }

    private static int Ret(ref CallContext c, string text) => c.Return(Value.FromUtf16(c.L, text));

    private static int New(ref CallContext c) => Ret(ref c, c.Count == 0 ? "" : Arg(ref c, 0));

    private static int ToUtf8(ref CallContext c) => c.Return(LuaText.Encode(Arg(ref c, 0)));

    private static int Char(ref CallContext c)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < c.Count; i++)
        {
            long cp = c.CheckInt(i);
            if (cp < 0 || cp > 0x10FFFF) throw c.ArgError(i, "value out of range");
            if (cp <= 0xFFFF) sb.Append((char)cp); // lone surrogates are allowed as code units
            else sb.Append(char.ConvertFromUtf32((int)cp));
        }
        return Ret(ref c, sb.ToString());
    }

    private static int Len(ref CallContext c) => c.Return(Value.FromInt(Arg(ref c, 0).Length));

    private static (int Start, int End) Range(ref CallContext c, int len, int argI, long defI, long defJ)
    {
        long i = c.OptInt(argI, defI), j = c.OptInt(argI + 1, defJ);
        if (i < 0) i = Math.Max(len + i + 1, 1); else if (i == 0) i = 1;
        // Clamp before the casts: a hugely negative j must not wrap to a positive int.
        if (j < 0) j = Math.Max(len + j + 1, 0); else if (j > len) j = len;
        return ((int)Math.Min(i, (long)len + 1), (int)j); // verified: verification/StringPos.dfy, Utf16Range
    }

    private static int Sub(ref CallContext c)
    {
        string s = Arg(ref c, 0);
        var (i, j) = Range(ref c, s.Length, 1, 1, -1);
        return Ret(ref c, i > j ? "" : s.Substring(i - 1, j - i + 1));
    }

    private static int Rep(ref CallContext c)
    {
        string s = Arg(ref c, 0);
        long n = c.CheckInt(1);
        if (n <= 0 || s.Length == 0) return Ret(ref c, "");
        if (s.Length * n > int.MaxValue / 2) throw new LuaException("resulting string too large");
        return Ret(ref c, new StringBuilder(s.Length * (int)n).Insert(0, s, (int)n).ToString());
    }

    private static int Reverse(ref CallContext c)
    {
        // reverse by code point so surrogate pairs stay intact
        string s = Arg(ref c, 0);
        var sb = new StringBuilder(s.Length);
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1])) { sb.Append(s[i - 1]).Append(s[i]); i--; }
            else sb.Append(s[i]);
        }
        return Ret(ref c, sb.ToString());
    }

    private static int Unit(ref CallContext c)
    {
        string s = Arg(ref c, 0);
        long first = c.OptInt(1, 1);
        var (i, j) = Range(ref c, s.Length, 1, 1, first);
        if (i > j) return 0;
        var r = new Value[j - i + 1];
        for (int k = 0; k < r.Length; k++) r[k] = Value.FromInt(s[i - 1 + k]);
        return c.Return(r);
    }

    private static int Codepoint(ref CallContext c)
    {
        string s = Arg(ref c, 0);
        long first = c.OptInt(1, 1);
        var (i, j) = Range(ref c, s.Length, 1, 1, first);
        var r = new List<Value>();
        for (int k = i - 1; k < j; k++)
        {
            if (char.IsHighSurrogate(s[k]) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1]))
            {
                r.Add(Value.FromInt(char.ConvertToUtf32(s[k], s[k + 1])));
                k++;
            }
            else r.Add(Value.FromInt(s[k]));
        }
        return c.Return(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(r));
    }

    // Plain (non-pattern) search; returns start and end positions or nil.
    private static int Find(ref CallContext c)
    {
        string s = Arg(ref c, 0), needle = Arg(ref c, 1);
        long init = c.OptInt(2, 1);
        if (init < 0) init = Math.Max(s.Length + init + 1, 1); else if (init == 0) init = 1;
        if (init > s.Length + 1) return c.Return(Value.Nil);
        int at = s.IndexOf(needle, (int)init - 1, StringComparison.Ordinal);
        if (at < 0) return c.Return(Value.Nil);
        return c.Return(Value.FromInt(at + 1), Value.FromInt(at + needle.Length));
    }
}
