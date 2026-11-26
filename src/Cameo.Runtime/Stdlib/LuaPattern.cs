using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// Port of the Lua 5.4 pattern matcher (lstrlib.c). Operates on UTF-16 code units;
/// character classes (%a, %d, ...) follow the C locale, i.e. only ASCII characters match them.
/// </summary>
internal sealed class LuaPattern
{
    private const int MaxCaptures = 32;
    private const int CapUnfinished = -1;
    private const int CapPosition = -2;
    private const int MaxRecursion = 200;

    public readonly string Src;
    public readonly string Pat;
    public int Level;
    private readonly int[] _capStart = new int[MaxCaptures];
    private readonly int[] _capLen = new int[MaxCaptures];
    private int _depth;
    private int _steps;
    /// <summary>Polled every few thousand match steps, so a host abort can stop a pattern that
    /// backtracks catastrophically.</summary>
    public Action? Poll;

    public LuaPattern(string src, string pat)
    {
        Src = src;
        Pat = pat;
    }

    /// <summary>Resets capture state before a new match attempt.</summary>
    public void Reset()
    {
        Level = 0;
        _depth = MaxRecursion;
    }

    /// <summary>Tries to match the pattern starting at pattern index p against the subject at s.
    /// Returns the end index of the match or -1.</summary>
    public int Match(int s, int p)
    {
        if (_depth-- == 0) throw new LuaException("pattern too complex");
        if ((++_steps & 0xFFF) == 0) Poll?.Invoke();
        try
        {
            string pat = Pat, src = Src;
            int plen = pat.Length, slen = src.Length;
            while (true)
            {
                if (p == plen) return s;
                char pc = pat[p];
                switch (pc)
                {
                    case '(':
                        if (p + 1 < plen && pat[p + 1] == ')') return StartCapture(s, p + 2, CapPosition);
                        return StartCapture(s, p + 1, CapUnfinished);
                    case ')':
                        return EndCapture(s, p + 1);
                    case '$':
                        if (p + 1 == plen) return s == slen ? s : -1;
                        break;
                    case '%':
                        if (p + 1 < plen)
                        {
                            char nx = pat[p + 1];
                            if (nx == 'b')
                            {
                                s = MatchBalance(s, p + 2);
                                if (s == -1) return -1;
                                p += 4;
                                continue;
                            }
                            if (nx == 'f')
                            {
                                p += 2;
                                if (p >= plen || pat[p] != '[') throw new LuaException("missing '[' after '%f' in pattern");
                                int fep = ClassEnd(p);
                                char prev = s == 0 ? '\0' : src[s - 1];
                                char cur = s < slen ? src[s] : '\0';
                                if (!MatchBracketClass(prev, p, fep - 1) && MatchBracketClass(cur, p, fep - 1))
                                {
                                    p = fep;
                                    continue;
                                }
                                return -1;
                            }
                            if (nx >= '0' && nx <= '9')
                            {
                                s = MatchCapture(s, nx);
                                if (s == -1) return -1;
                                p += 2;
                                continue;
                            }
                        }
                        break;
                }
                // default: single character class followed by an optional repetition suffix
                int ep = ClassEnd(p);
                bool m = s < slen && SingleMatch(s, p, ep);
                if (ep < plen)
                {
                    char epc = pat[ep];
                    if (epc == '?')
                    {
                        if (m)
                        {
                            int res = Match(s + 1, ep + 1);
                            if (res != -1) return res;
                        }
                        p = ep + 1;
                        continue;
                    }
                    if (epc == '+') return m ? MaxExpand(s + 1, p, ep) : -1;
                    if (epc == '*') return MaxExpand(s, p, ep);
                    if (epc == '-') return MinExpand(s, p, ep);
                }
                if (!m) return -1;
                s++;
                p = ep;
            }
        }
        finally
        {
            _depth++;
        }
    }

    private int ClassEnd(int p)
    {
        string pat = Pat;
        int plen = pat.Length;
        char c = pat[p++];
        if (c == '%')
        {
            if (p == plen) throw new LuaException("malformed pattern (ends with '%')");
            return p + 1;
        }
        if (c == '[')
        {
            if (p < plen && pat[p] == '^') p++;
            do
            {
                if (p == plen) throw new LuaException("malformed pattern (missing ']')");
                if (pat[p++] == '%' && p < plen) p++;
            } while (p >= plen || pat[p] != ']');
            return p + 1;
        }
        return p;
    }

    private static bool MatchClass(char c, char cl)
    {
        bool res;
        switch (char.ToLowerInvariant(cl))
        {
            case 'a': res = char.IsAsciiLetter(c); break;
            case 'c': res = c < 32 || c == 127; break;
            case 'd': res = char.IsAsciiDigit(c); break;
            case 'g': res = c > 32 && c < 127; break;
            case 'l': res = char.IsAsciiLetterLower(c); break;
            case 'p': res = c > 32 && c < 127 && !char.IsAsciiLetterOrDigit(c); break;
            case 's': res = c == ' ' || (c >= '\t' && c <= '\r'); break;
            case 'u': res = char.IsAsciiLetterUpper(c); break;
            case 'w': res = char.IsAsciiLetterOrDigit(c); break;
            case 'x': res = char.IsAsciiHexDigit(c); break;
            default: return cl == c;
        }
        return char.IsAsciiLetterUpper(cl) ? !res : res;
    }

    // p points at '[', ec at the closing ']'
    private bool MatchBracketClass(char c, int p, int ec)
    {
        string pat = Pat;
        bool sig = true;
        if (pat[p + 1] == '^')
        {
            sig = false;
            p++;
        }
        while (++p < ec)
        {
            if (pat[p] == '%')
            {
                p++;
                if (MatchClass(c, pat[p])) return sig;
            }
            else if (pat[p + 1] == '-' && p + 2 < ec)
            {
                p += 2;
                if (pat[p - 2] <= c && c <= pat[p]) return sig;
            }
            else if (pat[p] == c) return sig;
        }
        return !sig;
    }

    private bool SingleMatch(int s, int p, int ep)
    {
        char c = Src[s];
        switch (Pat[p])
        {
            case '.': return true;
            case '%': return MatchClass(c, Pat[p + 1]);
            case '[': return MatchBracketClass(c, p, ep - 1);
            default: return Pat[p] == c;
        }
    }

    private int MatchBalance(int s, int p)
    {
        if (p + 1 >= Pat.Length) throw new LuaException("malformed pattern (missing arguments to '%b')");
        string src = Src;
        if (s >= src.Length || src[s] != Pat[p]) return -1;
        char b = Pat[p], e = Pat[p + 1];
        int cont = 1;
        for (int i = s + 1; i < src.Length; i++)
        {
            char c = src[i];
            if (c == e)
            {
                if (--cont == 0) return i + 1;
            }
            else if (c == b) cont++;
        }
        return -1;
    }

    private int MaxExpand(int s, int p, int ep)
    {
        int i = 0;
        int slen = Src.Length;
        while (s + i < slen && SingleMatch(s + i, p, ep)) i++;
        while (i >= 0)
        {
            int res = Match(s + i, ep + 1);
            if (res != -1) return res;
            i--;
        }
        return -1;
    }

    private int MinExpand(int s, int p, int ep)
    {
        while (true)
        {
            int res = Match(s, ep + 1);
            if (res != -1) return res;
            if (s < Src.Length && SingleMatch(s, p, ep)) s++;
            else return -1;
        }
    }

    private int StartCapture(int s, int p, int what)
    {
        if (Level >= MaxCaptures) throw new LuaException("too many captures");
        _capStart[Level] = s;
        _capLen[Level] = what;
        Level++;
        int res = Match(s, p);
        if (res == -1) Level--;
        return res;
    }

    private int EndCapture(int s, int p)
    {
        int l = CaptureToClose();
        _capLen[l] = s - _capStart[l];
        int res = Match(s, p);
        if (res == -1) _capLen[l] = CapUnfinished;
        return res;
    }

    private int CaptureToClose()
    {
        for (int level = Level - 1; level >= 0; level--)
            if (_capLen[level] == CapUnfinished) return level;
        throw new LuaException("invalid pattern capture");
    }

    private int MatchCapture(int s, char l)
    {
        int idx = l - '1';
        if (idx < 0 || idx >= Level || _capLen[idx] == CapUnfinished)
            throw new LuaException($"invalid capture index %{idx + 1}");
        int len = _capLen[idx];
        if (Src.Length - s >= len && string.CompareOrdinal(Src, _capStart[idx], Src, s, len) == 0) return s + len;
        return -1;
    }

    /// <summary>Value of capture i for a match spanning [s, e). Capture 0 with no explicit
    /// captures is the whole match.</summary>
    public Value GetCapture(int i, int s, int e)
    {
        if (i >= Level)
        {
            if (i != 0) throw new LuaException($"invalid capture index %{i + 1}");
            return Value.FromString(Src.Substring(s, e - s));
        }
        int l = _capLen[i];
        if (l == CapUnfinished) throw new LuaException("unfinished capture");
        if (l == CapPosition) return Value.FromInt(_capStart[i] + 1);
        return Value.FromString(Src.Substring(_capStart[i], l));
    }

    /// <summary>Number of values produced for a match (whole match counts when there are no captures).</summary>
    public int CaptureCount(bool wholeIfNone) => Level == 0 && wholeIfNone ? 1 : Level;

    public static bool HasSpecials(string pat)
    {
        foreach (char c in pat)
            if ("^$*+?.([%-".IndexOf(c) >= 0) return true;
        return false;
    }
}
