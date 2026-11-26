using System.Globalization;
using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// The byte-string library. VM strings use one .NET <see cref="char"/> per byte;
/// positions and lengths count bytes, and <c>byte</c>/<c>char</c> operate on those bytes.
/// Unicode text is UTF-8 encoded at the boundary; <c>utf16</c> is a separate value type.
/// </summary>
public static class StringLib
{
    public static void Open(LuaState L)
    {
        var s = new Table();
        L.RegisterInstance<string>(s, "len", Len);
        L.RegisterInstance<string>(s, "sub", Sub);
        L.RegisterInstance<string>(s, "upper", Upper);
        L.RegisterInstance<string>(s, "lower", Lower);
        // functions are not serialised here: string.dump always fails, with Lua's message for
        // a function that cannot be dumped
        L.Register(s, "dump", static (ref CallContext c) =>
        {
            c.CheckFunction(0);
            throw new LuaException("unable to dump given function");
        });
        L.RegisterInstance<string>(s, "rep", Rep);
        L.RegisterInstance<string>(s, "reverse", Reverse);
        L.RegisterInstance<string>(s, "byte", Byte);
        L.Register(s, "char", Char);
        L.RegisterInstance<string>(s, "format", Format);
        L.RegisterInstance<string>(s, "find", Find);
        L.RegisterInstance<string>(s, "match", Match);
        L.RegisterInstance<string>(s, "gmatch", GMatch);
        L.RegisterInstance<string>(s, "gsub", GSub);
        StringPack.Register(L, s);
        L.Globals.SetStr("string", s);
        L.LoadedModules.SetStr("string", s);
        L.StringMeta.SetStr("__index", s);
    }

    // ------------------------------------------------------------------ position helpers (Lua 5.4)

    // Verified: verification/StringPos.dfy, method PosRelatI
    private static long PosRelatI(long pos, int len)
    {
        if (pos > 0) return pos;
        if (pos == 0) return 1;
        if (pos < -(long)len) return 1;
        return len + pos + 1;
    }

    // Verified: verification/StringPos.dfy, methods GetEndPos and Sub
    private static long GetEndPos(ref CallContext c, int arg, long def, int len)
    {
        long pos = c.OptInt(arg, def);
        if (pos > len) return len;
        if (pos >= 0) return pos;
        if (pos < -(long)len) return 0;
        return len + pos + 1;
    }

    // ------------------------------------------------------------------ simple functions

    private static int Len(ref CallContext c) => c.Return(Value.FromInt(c.CheckString(0).Length));

    private static int Sub(ref CallContext c)
    {
        string s = c.CheckString(0);
        int l = s.Length;
        long i = PosRelatI(c.CheckInt(1), l);
        long j = GetEndPos(ref c, 2, -1, l);
        if (i > j) return c.Return("");
        return c.Return(s.Substring((int)i - 1, (int)(j - i + 1)));
    }

    private static int Upper(ref CallContext c) => c.Return(ChangeCase(c.CheckString(0), upper: true));

    private static int Lower(ref CallContext c) => c.Return(ChangeCase(c.CheckString(0), upper: false));

    // ASCII-only case mapping (the C locale), other bytes unchanged. Runs of ASCII go through
    // the vectorised System.Text.Ascii helpers; profiling showed the per-char loop at ~10% of
    // a file-uppercasing workload.
    private static string ChangeCase(string s, bool upper) =>
        string.Create(s.Length, (s, upper), static (dst, state) =>
        {
            ReadOnlySpan<char> src = state.s;
            int pos = 0;
            while (pos < src.Length)
            {
                int done;
                if (state.upper) System.Text.Ascii.ToUpper(src.Slice(pos), dst.Slice(pos), out done);
                else System.Text.Ascii.ToLower(src.Slice(pos), dst.Slice(pos), out done);
                pos += done;
                // a non-ASCII byte stopped the fast path: copy such bytes through unchanged
                while (pos < src.Length && src[pos] >= 0x80) { dst[pos] = src[pos]; pos++; }
            }
        });

    private static int Rep(ref CallContext c)
    {
        string s = c.CheckString(0);
        long n = c.CheckInt(1);
        string sep = c.OptString(2, "");
        if (n <= 0) return c.Return("");
        // nothing to repeat: answer at once (PUC Lua loops n times here, which never ends for huge n)
        if (s.Length == 0 && sep.Length == 0) return c.Return("");
        // Verified: verification/StringPos.dfy, method RepGuard (no wrap-around when the guard passes)
        long total = s.Length * n + sep.Length * (n - 1);
        if (total > c.L.Limits.MaxStringLength || (s.Length + sep.Length) > 0 && n > 0x3FFFFFFF) throw new LuaException("resulting string too large");
        var sb = new StringBuilder((int)total);
        for (long i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(s);
        }
        return c.Return(sb.ToString());
    }

    private static int Reverse(ref CallContext c)
    {
        string s = c.CheckString(0);
        char[] a = s.ToCharArray();
        Array.Reverse(a);
        return c.Return(new string(a));
    }

    private static int Byte(ref CallContext c)
    {
        string s = c.CheckString(0);
        int l = s.Length;
        long rawPi = c.OptInt(1, 1);
        long pi = PosRelatI(rawPi, l);
        long pose = GetEndPos(ref c, 2, rawPi, l);
        if (pi > pose) return 0;
        int n = (int)(pose - pi + 1);
        if (n == 1) return c.Return(Value.FromInt(s[(int)pi - 1]));
        var r = new Value[n];
        for (int i = 0; i < n; i++) r[i] = Value.FromInt(s[(int)pi - 1 + i]);
        return c.Return(r);
    }

    private static int Char(ref CallContext c)
    {
        int n = c.Count;
        var a = new char[n];
        for (int i = 0; i < n; i++)
        {
            long v = c.CheckInt(i);
            if (v < 0 || v > 255) throw c.ArgError(i, "value out of range");
            a[i] = (char)v;
        }
        return c.Return(new string(a));
    }

    // ------------------------------------------------------------------ pattern matching

    private static int PushCaptures(ref CallContext c, LuaPattern ms, int s, int e, Value[]? prefix)
    {
        int n = ms.CaptureCount(true);
        int off = prefix?.Length ?? 0;
        if (off == 0 && n == 1) return c.Return(ms.GetCapture(0, s, e));
        var r = new Value[off + n];
        prefix?.CopyTo(r, 0);
        for (int i = 0; i < n; i++) r[off + i] = ms.GetCapture(i, s, e);
        return c.Return(r);
    }

    private static int FindAux(ref CallContext c, bool find)
    {
        string s = c.CheckString(0);
        string p = c.CheckString(1);
        int ls = s.Length;
        long init = PosRelatI(c.OptInt(2, 1), ls) - 1;
        if (init > ls) return c.Return(Value.Nil);
        if (find && (c.ToBoolean(3) || !LuaPattern.HasSpecials(p)))
        {
            int idx = s.IndexOf(p, (int)init, StringComparison.Ordinal);
            if (idx < 0) return c.Return(Value.Nil);
            return c.Return(Value.FromInt(idx + 1), Value.FromInt(idx + p.Length));
        }
        var ms = new LuaPattern(s, p) { Poll = c.L.CheckAbort };
        bool anchor = p.Length > 0 && p[0] == '^';
        int pstart = anchor ? 1 : 0;
        int s1 = (int)init;
        do
        {
            ms.Reset();
            int e = ms.Match(s1, pstart);
            if (e != -1)
            {
                if (find)
                {
                    if (ms.Level == 0) return c.Return(Value.FromInt(s1 + 1), Value.FromInt(e));
                    var r = new Value[2 + ms.Level];
                    r[0] = Value.FromInt(s1 + 1);
                    r[1] = Value.FromInt(e);
                    for (int i = 0; i < ms.Level; i++) r[2 + i] = ms.GetCapture(i, s1, e);
                    return c.Return(r);
                }
                return PushCaptures(ref c, ms, s1, e, null);
            }
        } while (s1++ < ls && !anchor);
        return c.Return(Value.Nil);
    }

    private static int Find(ref CallContext c) => FindAux(ref c, true);
    private static int Match(ref CallContext c) => FindAux(ref c, false);

    private static int GMatch(ref CallContext c)
    {
        string s = c.CheckString(0);
        string p = c.CheckString(1);
        int ls = s.Length;
        long init = PosRelatI(c.OptInt(2, 1), ls) - 1;
        if (init > ls) init = ls + 1;
        var ms = new LuaPattern(s, p) { Poll = c.L.CheckAbort };
        int pos = (int)init;
        int lastMatch = -1;
        return c.Return(Value.FromFunction(new NativeFunction("gmatch_iter", (ref CallContext cc) =>
        {
            for (int src = pos; src <= ls; src++)
            {
                ms.Reset();
                int e = ms.Match(src, 0);
                if (e != -1 && e != lastMatch)
                {
                    pos = lastMatch = e;
                    return PushCaptures(ref cc, ms, src, e, null);
                }
            }
            pos = ls + 1;
            return cc.Return(Value.Nil);
        })));
    }

    private static int GSub(ref CallContext c)
    {
        string src = c.CheckString(0);
        string p = c.CheckString(1);
        Value repl = c[2];
        if (!(repl.IsNumber || repl.IsString || repl.IsTable || repl.IsFunction))
            throw c.TypeError(2, "string/function/table");
        string? replStr = repl.IsString ? repl.AsString : repl.IsNumber ? repl.ToString() : null;
        int ls = src.Length;
        long maxS = c.OptInt(3, (long)ls + 1);
        bool anchor = p.Length > 0 && p[0] == '^';
        int pstart = anchor ? 1 : 0;
        var ms = new LuaPattern(src, p) { Poll = c.L.CheckAbort };
        var sb = new StringBuilder();
        LuaState L = c.L;
        int s = 0;
        int lastMatch = -1;
        long n = 0;
        while (n < maxS)
        {
            ms.Reset();
            int e = ms.Match(s, pstart);
            if (e != -1 && e != lastMatch)
            {
                n++;
                AddValue(L, ms, sb, s, e, repl, replStr);
                s = lastMatch = e;
            }
            else if (s < ls) sb.Append(src[s++]);
            else break;
            if (anchor) break;
        }
        if (s < ls) sb.Append(src, s, ls - s);
        return c.Return(sb.ToString(), Value.FromInt(n));
    }

    private static void AddValue(LuaState L, LuaPattern ms, StringBuilder sb, int s, int e, Value repl, string? replStr)
    {
        Value v;
        if (replStr != null)
        {
            for (int i = 0; i < replStr.Length; i++)
            {
                char ch = replStr[i];
                if (ch != '%') { sb.Append(ch); continue; }
                i++;
                if (i >= replStr.Length) throw new LuaException("invalid use of '%' in replacement string");
                char d = replStr[i];
                if (d == '%') sb.Append('%');
                else if (d == '0') sb.Append(ms.Src, s, e - s);
                else if (char.IsAsciiDigit(d))
                {
                    Value cap = ms.GetCapture(d - '1', s, e);
                    sb.Append(cap.IsString ? cap.AsString : cap.ToString());
                }
                else throw new LuaException("invalid use of '%' in replacement string");
            }
            return;
        }
        if (repl.IsTable)
        {
            v = L.Index(repl, ms.GetCapture(0, s, e));
        }
        else
        {
            int n = ms.CaptureCount(true);
            if (n == 1) v = L.Call1(repl, ms.GetCapture(0, s, e));
            else
            {
                var args = new Value[n];
                for (int i = 0; i < n; i++) args[i] = ms.GetCapture(i, s, e);
                Value[] r = L.Call(repl, args);
                v = r.Length > 0 ? r[0] : Value.Nil;
            }
        }
        if (!v.IsTruthy) sb.Append(ms.Src, s, e - s);
        else if (v.IsString) sb.Append(v.AsString);
        else if (v.IsNumber) sb.Append(v.ToString());
        else throw new LuaException($"invalid replacement value (a {LuaState.TypeNameOf(v)})");
    }

    // ------------------------------------------------------------------ format

    private static int Format(ref CallContext c)
    {
        string fmt = c.CheckString(0);
        var sb = new StringBuilder();
        int arg = 0;
        int n = fmt.Length;
        for (int i = 0; i < n; i++)
        {
            char ch = fmt[i];
            if (ch != '%') { sb.Append(ch); continue; }
            if (++i >= n)
            {
                if (arg + 1 >= c.Count) throw c.ArgError(arg + 1, "no value");
                throw new LuaException("invalid conversion '%' to 'format'");
            }
            if (fmt[i] == '%') { sb.Append('%'); continue; }
            // The argument is checked before the specification, as in Lua.
            if (++arg >= c.Count) throw c.ArgError(arg, "no value");
            // spec = flags, width and precision, followed by the conversion character
            int specStart = i;
            while (i < n && "-+ #0123456789.".IndexOf(fmt[i]) >= 0) i++;
            if (i - specStart + 1 >= 22) throw new LuaException("invalid format (too long)");
            char conv = i < n ? fmt[i] : '\0';
            string spec = fmt.Substring(specStart, Math.Min(i + 1, n) - specStart);
            switch (conv)
            {
                case 'c': case 'p': CheckFormat(spec, "-", false); break;
                case 'd': case 'i': CheckFormat(spec, "-+0 ", true); break;
                case 'u': CheckFormat(spec, "-0", true); break;
                case 'o': case 'x': case 'X': CheckFormat(spec, "-#0", true); break;
                case 'a': case 'A': case 'e': case 'E': case 'f': case 'F': case 'g': case 'G':
                    CheckFormat(spec, "-+ #0", true); break;
                case 's': CheckFormat(spec, "-", true); break;
                case 'q':
                    if (spec.Length != 1) throw new LuaException("specifier '%q' cannot have modifiers");
                    break;
                default:
                    throw new LuaException($"invalid conversion '%{spec}' to 'format'");
            }
            bool left = false, plus = false, space = false, alt = false, zero = false;
            int sp = 0;
            for (; sp < spec.Length; sp++)
            {
                char f = spec[sp];
                if (f == '-') left = true;
                else if (f == '+') plus = true;
                else if (f == ' ') space = true;
                else if (f == '#') alt = true;
                else if (f == '0') zero = true;
                else break;
            }
            int width = 0;
            while (char.IsAsciiDigit(spec[sp])) width = width * 10 + (spec[sp++] - '0');
            int prec = -1;
            if (spec[sp] == '.')
            {
                sp++;
                prec = 0;
                while (char.IsAsciiDigit(spec[sp])) prec = prec * 10 + (spec[sp++] - '0');
            }
            string body;
            switch (conv)
            {
                case 'c':
                    body = ((char)c.CheckInt(arg)).ToString();
                    sb.Append(Pad(body, width, left, false));
                    continue;
                case 'd':
                case 'i':
                    {
                        long v = c.CheckInt(arg);
                        string digits = v < 0 ? ((ulong)(-(v + 1)) + 1UL).ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
                        body = FinishInt(digits, v < 0, "", plus, space, zero, left, width, prec);
                        sb.Append(body);
                        continue;
                    }
                case 'u':
                    {
                        ulong v = (ulong)c.CheckInt(arg);
                        sb.Append(FinishInt(v.ToString(CultureInfo.InvariantCulture), false, "", false, false, zero, left, width, prec));
                        continue;
                    }
                case 'o':
                case 'x':
                case 'X':
                    {
                        ulong v = (ulong)c.CheckInt(arg);
                        string digits = conv == 'o' ? Convert.ToString((long)v, 8) : conv == 'x' ? v.ToString("x") : v.ToString("X");
                        string prefix = "";
                        if (alt && v != 0) prefix = conv == 'o' ? "0" : conv == 'x' ? "0x" : "0X";
                        if (v == 0 && prec == 0) digits = "";
                        sb.Append(FinishInt(digits, false, prefix, false, false, zero, left, width, prec));
                        continue;
                    }
                case 'e':
                case 'E':
                case 'f':
                case 'F':
                case 'g':
                case 'G':
                case 'a':
                case 'A':
                    {
                        double d = c.CheckNumber(arg);
                        sb.Append(FormatFloat(d, conv, prec, plus, space, alt, zero, left, width));
                        continue;
                    }
                case 'p':
                    {
                        Value v = c[arg];
                        c.CheckAny(arg);
                        body = v.O != null && !v.IsString
                            ? "0x" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v.O).ToString("x14")
                            : "(null)";
                        sb.Append(Pad(body, width, left, false));
                        continue;
                    }
                case 'q':
                    c.CheckAny(arg);
                    AddQuoted(sb, c[arg]);
                    continue;
                case 's':
                    {
                        c.CheckAny(arg);
                        body = c.L.ToStringMeta(c[arg]);
                        if (prec >= 0 && body.Length > prec) body = body.Substring(0, prec);
                        sb.Append(Pad(body, width, left, false));
                        continue;
                    }
                default:
                    throw new LuaException($"invalid conversion '%{spec}' to 'format'");
            }
        }
        return c.Return(sb.ToString());
    }

    // Mirrors lstrlib's checkformat: flags, up to two width digits, optional precision of up to two digits.
    private static void CheckFormat(string spec, string flags, bool precision)
    {
        int p = 0;
        while (p < spec.Length && flags.IndexOf(spec[p]) >= 0) p++;
        if (p < spec.Length && spec[p] != '0')
        {
            for (int k = 0; k < 2 && p < spec.Length && char.IsAsciiDigit(spec[p]); k++) p++;
            if (p < spec.Length && spec[p] == '.' && precision)
            {
                p++;
                for (int k = 0; k < 2 && p < spec.Length && char.IsAsciiDigit(spec[p]); k++) p++;
            }
        }
        if (p != spec.Length - 1 || !char.IsAsciiLetter(spec[p]))
            throw new LuaException($"invalid conversion specification: '%{spec}'");
    }

    private static string Pad(string body, int width, bool left, bool zero)
    {
        if (body.Length >= width) return body;
        if (left) return body.PadRight(width);
        return zero ? body.PadLeft(width, '0') : body.PadLeft(width);
    }

    private static string FinishInt(string digits, bool neg, string prefix, bool plus, bool space, bool zero, bool left, int width, int prec)
    {
        if (prec >= 0)
        {
            if (digits.Length < prec) digits = new string('0', prec - digits.Length) + digits;
            if (prec == 0 && digits == "0") digits = "";
            zero = false;
        }
        string sign = neg ? "-" : plus ? "+" : space ? " " : "";
        int len = sign.Length + prefix.Length + digits.Length;
        if (len >= width) return sign + prefix + digits;
        if (left) return (sign + prefix + digits).PadRight(width);
        if (zero) return sign + prefix + new string('0', width - len) + digits;
        return (sign + prefix + digits).PadLeft(width);
    }

    internal static string FormatFloat(double d, char conv, int prec, bool plus, bool space, bool alt, bool zero, bool left, int width)
    {
        bool upper = char.IsAsciiLetterUpper(conv);
        bool neg = double.IsNegative(d);
        string sign = neg ? "-" : plus ? "+" : space ? " " : "";
        string body;
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            body = double.IsNaN(d) ? "nan" : "inf";
            if (upper) body = body.ToUpperInvariant();
            zero = false;
        }
        else
        {
            double a = Math.Abs(d);
            switch (char.ToLowerInvariant(conv))
            {
                case 'f':
                    {
                        int p = prec < 0 ? 6 : prec;
                        body = a.ToString("F" + p.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                        if (alt && p == 0) body += ".";
                        break;
                    }
                case 'e':
                    {
                        int p = prec < 0 ? 6 : prec;
                        string e = a.ToString("E" + p.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                        int ePos = e.IndexOf('E');
                        int exp = int.Parse(e.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                        string mant = e.Substring(0, ePos);
                        if (alt && p == 0) mant += ".";
                        body = mant + (upper ? "E" : "e") + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
                        break;
                    }
                case 'g':
                    body = LuaNumber.FormatG(a, prec < 0 ? 6 : prec, alt, upper);
                    break;
                default: // 'a'
                    body = HexFloat(a, prec, upper);
                    break;
            }
        }
        int len = sign.Length + body.Length;
        if (len >= width) return sign + body;
        if (left) return (sign + body).PadRight(width);
        if (zero)
        {
            // zero padding goes after the sign (and after "0x" for hex floats)
            if (body.StartsWith("0x") || body.StartsWith("0X"))
                return sign + body.Substring(0, 2) + new string('0', width - len) + body.Substring(2);
            return sign + new string('0', width - len) + body;
        }
        return (sign + body).PadLeft(width);
    }

    /// <summary>C-style %a for a non-negative finite double.</summary>
    internal static string HexFloat(double a, int prec, bool upper)
    {
        long bits = BitConverter.DoubleToInt64Bits(a);
        int expBits = (int)((bits >> 52) & 0x7FF);
        ulong mant = (ulong)bits & 0xFFFFFFFFFFFFFUL;
        int exp;
        ulong lead;
        if (expBits == 0)
        {
            if (mant == 0) { lead = 0; exp = 0; }
            else { lead = 0; exp = -1022; }
        }
        else { lead = 1; exp = expBits - 1023; }
        string frac;
        if (prec >= 0 && prec < 13)
        {
            // round the 52-bit mantissa to prec hex digits (round half to even)
            int drop = (13 - prec) * 4;
            ulong full = (lead << 52) | mant;
            ulong half = 1UL << (drop - 1);
            ulong rem = full & ((1UL << drop) - 1);
            full >>= drop;
            if (rem > half || (rem == half && (full & 1) == 1)) full++;
            lead = full >> (prec * 4);
            ulong fm = prec == 0 ? 0 : full & ((1UL << (prec * 4)) - 1);
            frac = prec == 0 ? "" : fm.ToString("x" + prec);
        }
        else
        {
            frac = mant.ToString("x13").TrimEnd('0');
            if (prec > 13) frac = frac.PadRight(prec, '0');
            else if (prec >= 0) frac = frac.PadRight(prec, '0');
        }
        string s = "0x" + lead + (frac.Length > 0 ? "." + frac : "") + "p" + (exp < 0 ? "-" : "+") + Math.Abs(exp);
        return upper ? s.ToUpperInvariant() : s;
    }

    private static void AddQuoted(StringBuilder sb, Value v)
    {
        switch (v.Kind)
        {
            case ValueKind.String:
                {
                    string s = v.AsString;
                    sb.Append('"');
                    for (int i = 0; i < s.Length; i++)
                    {
                        char ch = s[i];
                        if (ch == '"' || ch == '\\' || ch == '\n')
                        {
                            sb.Append('\\').Append(ch);
                        }
                        else if (ch < 32 || ch == 127)
                        {
                            bool digitNext = i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]);
                            sb.Append('\\').Append(digitNext ? ((int)ch).ToString("000") : ((int)ch).ToString());
                        }
                        else sb.Append(ch);
                    }
                    sb.Append('"');
                    break;
                }
            case ValueKind.Integer:
                if (v.I == long.MinValue) sb.Append("0x8000000000000000");
                else sb.Append(v.I.ToString(CultureInfo.InvariantCulture));
                break;
            case ValueKind.Float:
                {
                    double d = v.N;
                    if (double.IsPositiveInfinity(d)) sb.Append("1e9999");
                    else if (double.IsNegativeInfinity(d)) sb.Append("-1e9999");
                    else if (double.IsNaN(d)) sb.Append("(0/0)");
                    else sb.Append((double.IsNegative(d) ? "-" : "") + HexFloat(Math.Abs(d), -1, false));
                    break;
                }
            case ValueKind.Nil:
            case ValueKind.Boolean:
                sb.Append(v.ToString());
                break;
            default:
                throw new LuaException("value has no literal form");
        }
    }
}
