using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Hedgehog;
using Hedgehog.Linq;
using Cameo.Runtime.Vm;
using Xunit;
using Gen = Hedgehog.Linq.Gen;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>
/// The string library: algebraic laws and string.format (Hedgehog), the pattern matcher against
/// System.Text.RegularExpressions on the common subset (all Hedgehog).
/// </summary>
public class StringLibProperties
{
    private readonly LuaHost _lua = new();

    /// <summary>Arbitrary byte strings (one byte per char), weighted towards ASCII.</summary>
    private static readonly Gen<string> GenBytes =
        Gen.Frequency(
            (5, Gen.Char(' ', '~')),
            (1, Gen.Char((char)0, (char)31)),
            (2, Gen.Char((char)127, (char)255)))
        .String(Range.LinearInt32(0, 20));

    private static readonly Gen<long> GenIndex = Gen.Int64(Range.LinearFromInt64(0, -25, 25));

    private string S(string fn, params Value[] args) => _lua.Call1(fn, args).AsString;

    // ------------------------------------------------------------------ algebraic laws

    private static string RefSub(string s, long i, long j)
    {
        long l = s.Length;
        if (i < 0) i = Math.Max(l + i + 1, 1); else if (i == 0) i = 1;
        if (j < 0) j = l + j + 1; else if (j > l) j = l;
        return i > j ? "" : s.Substring((int)i - 1, (int)(j - i + 1));
    }

    [Fact]
    public void Sub_matches_the_reference_and_splits_recombine()
    {
        Prop.Hh(from s in GenBytes from i in GenIndex from j in GenIndex select (s, i, j), x =>
        {
            var (s, i, j) = x;
            Assert.Equal(RefSub(s, i, j), S("string.sub", s, i, j));
            Assert.Equal(RefSub(s, i, -1), S("string.sub", s, i));
            // any split point recombines to the original
            long k = Math.Abs(i) % (s.Length + 1);
            Assert.Equal(s, S("string.sub", s, 1, k) + S("string.sub", s, k + 1));
            Assert.Equal(s.Length, _lua.Call1("string.len", s).I);
        }, 500);
    }

    [Fact]
    public void Byte_and_char_are_inverse()
    {
        Prop.Hh(from s in GenBytes from i in GenIndex from j in GenIndex select (s, i, j), x =>
        {
            var (s, i, j) = x;
            Value[] all = _lua.Call("string.byte", s, 1, -1);
            Assert.Equal(s.Select(c => (long)c), all.Select(v => v.I));
            Assert.Equal(s, _lua.Call1("string.char", all).AsString);
            // byte(s, i, j) is the codes of sub(s, i, j)
            Assert.Equal(RefSub(s, i, j).Select(c => (long)c), _lua.Call("string.byte", s, i, j).Select(v => v.I));
            // default is the first byte
            Value first = _lua.Call1("string.byte", s);
            Assert.Equal(s.Length == 0, first.IsNil);
        }, 400);
    }

    [Fact]
    public void Rep_reverse_and_case_laws()
    {
        Prop.Hh(from s in GenBytes from n in Gen.Int32(Range.LinearInt32(-1, 6)) from sep in GenBytes select (s, n, sep), x =>
        {
            var (s, n, sep) = x;
            string rep = S("string.rep", s, n);
            Assert.Equal(string.Concat(Enumerable.Repeat(s, Math.Max(n, 0))), rep);
            Assert.Equal(string.Join(sep, Enumerable.Repeat(s, Math.Max(n, 0))), S("string.rep", s, n, sep));
            string rev = S("string.reverse", s);
            Assert.Equal(new string(s.Reverse().ToArray()), rev);
            Assert.Equal(s, S("string.reverse", rev));
            // upper/lower touch ASCII letters only (C locale), and are idempotent
            string up = S("string.upper", s), lo = S("string.lower", s);
            Assert.Equal(new string(s.Select(c => c is >= 'a' and <= 'z' ? (char)(c - 32) : c).ToArray()), up);
            Assert.Equal(new string(s.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray()), lo);
            Assert.Equal(up, S("string.upper", up));
            Assert.Equal(lo, S("string.lower", up));
            // concatenation through the VM adds lengths
            Value cat = _lua.L.Concat2(s, sep);
            Assert.Equal(s + sep, cat.AsString);
        }, 400);
    }

    [Fact]
    public void Plain_find_agrees_with_IndexOf()
    {
        Prop.Hh(from s in GenBytes from a in GenIndex from b in GenIndex from init in GenIndex select (s, a, b, init), x =>
        {
            var (s, a, b, init) = x;
            string needle = RefSub(s, a, b);   // often a real substring, sometimes empty
            long start = init < 0 ? Math.Max(s.Length + init + 1, 1) : init == 0 ? 1 : init;
            Value[] r = _lua.Call("string.find", s, needle, init, true);
            if (start > s.Length + 1) { Assert.True(r[0].IsNil); return; }
            int at = s.IndexOf(needle, (int)start - 1, StringComparison.Ordinal);
            if (at < 0) Assert.True(r[0].IsNil);
            else
            {
                Assert.Equal(at + 1, r[0].I);
                Assert.Equal(at + needle.Length, r[1].I);
            }
        }, 400);
    }

    // ------------------------------------------------------------------ string.format

    [Fact]
    public void Format_integers()
    {
        var gen = from i in Gen.Frequency((3, Gen.Int64(Range.LinearFromInt64(0, -100000, 100000))), (1, Gen.Int64(Range.ConstantBoundedInt64())))
                  from w in Gen.Int32(Range.LinearInt32(1, 24))
                  select (i, w);
        Prop.Hh(gen, x =>
        {
            var (i, w) = x;
            string dec = i.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(dec, S("string.format", "%d", i));
            Assert.Equal(dec, S("string.format", "%i", i));
            Assert.Equal(dec.PadLeft(w), S("string.format", "%" + w + "d", i));
            Assert.Equal(dec.PadRight(w), S("string.format", "%-" + w + "d", i));
            string zero = i < 0 ? "-" + dec.Substring(1).PadLeft(w - 1, '0') : dec.PadLeft(w, '0');
            Assert.Equal(zero.Length < dec.Length ? dec : zero, S("string.format", "%0" + w + "d", i));
            Assert.Equal(i >= 0 ? "+" + dec : dec, S("string.format", "%+d", i));
            Assert.Equal(i >= 0 ? " " + dec : dec, S("string.format", "% d", i));
            // hex/octal print the two's complement bit pattern
            Assert.Equal(i.ToString("x"), S("string.format", "%x", i));
            Assert.Equal(i.ToString("X"), S("string.format", "%X", i));
            Assert.Equal(Convert.ToString(i, 8), S("string.format", "%o", i));
            Assert.Equal(i == 0 ? "0" : "0x" + i.ToString("x"), S("string.format", "%#x", i));
            // integer-valued floats are accepted by %d, others are not
            if (Math.Abs(i) < (1L << 52)) Assert.Equal(dec, S("string.format", "%d", (double)i));
            if (Math.Abs(i) < (1L << 50)) Assert.Contains("number has no integer representation", _lua.ErrorOf(() => _lua.Call("string.format", "%d", i + 0.5)));
            // tostring/tonumber round trip
            Assert.Equal(dec, S("tostring", i));
            Assert.Equal(i, _lua.Call1("tonumber", dec).I);
            if (Math.Abs(i) < (1L << 52)) Assert.Equal(i, _lua.Call1("math.tointeger", (double)i).I);
        }, 400);
    }

    [Fact]
    public void Format_floats()
    {
        var gen = from d in Gen.Frequency(
                      (3, Gen.Double(Range.LinearFromDouble(0, -1e6, 1e6))),
                      (2, Gen.Double(Range.ExponentialFromDouble(0, -1e300, 1e300))),
                      (1, Gen.Item(0.0, -0.0, 0.1, 1e15, 1e16, 123456.789, 0.5, 2.5, -1.5, 1e-5, 1e-4)))
                  from p in Gen.Int32(Range.LinearInt32(0, 17))
                  select (d, p);
        Prop.Hh(gen, x =>
        {
            var (d, p) = x;
            Assert.Equal(LuaNumber.FormatG(d, 6, false), S("string.format", "%g", d));
            Assert.Equal(LuaNumber.FormatG(d, p, false), S("string.format", "%." + p + "g", d));
            Assert.Equal(LuaNumber.FormatG(d, 14, false), S("string.format", "%.14g", d));
            // %f and %e agree with .NET's correctly rounded fixed/scientific formatting
            if (Math.Abs(d) < 1e15)
            {
                string f = S("string.format", "%." + p + "f", d);
                Assert.Equal(double.IsNegative(d) && !f.StartsWith('-') ? "-" + d.ToString("F" + p, CultureInfo.InvariantCulture) : d.ToString("F" + p, CultureInfo.InvariantCulture).TrimStart('-').Insert(0, double.IsNegative(d) ? "-" : ""), f);
            }
            string e = S("string.format", "%." + p + "e", d);
            Assert.Equal(d, double.Parse(e, CultureInfo.InvariantCulture), Math.Abs(d) * Math.Pow(10, -p) * 0.51 + double.Epsilon);
            Assert.Matches(@"^-?\d(\.\d+)?e[+-]\d\d+$", e);
            // %a is exact: it reads back as the same double
            string a = S("string.format", "%a", d);
            Assert.True(LuaNumber.TryParse(a, out Value back), a);
            Assert.Equal(d, back.AsDouble);
            // tostring is %.14g, with ".0" appended to integral values
            Assert.Equal(LuaNumber.FloatToString(d), S("tostring", d));
        }, 400);
    }

    [Fact]
    public void Format_strings_chars_and_quoting()
    {
        Prop.Hh(from s in GenBytes from w in Gen.Int32(Range.LinearInt32(1, 30)) from p in Gen.Int32(Range.LinearInt32(0, 30)) select (s, w, p), x =>
        {
            var (s, w, p) = x;
            if (!s.Contains('\0'))
            {
                Assert.Equal(s, S("string.format", "%s", s));
                Assert.Equal(s.PadLeft(w), S("string.format", "%" + w + "s", s));
                Assert.Equal(s.PadRight(w), S("string.format", "%-" + w + "s", s));
                Assert.Equal(s.Substring(0, Math.Min(p, s.Length)), S("string.format", "%." + p + "s", s));
            }
            Assert.Equal("%" + s.Replace("\0", "") + "%", S("string.format", "%%%s%%", s.Replace("\0", "")));
            // %c is string.char
            foreach (char c in s.Take(3)) Assert.Equal(c.ToString(), S("string.format", "%c", (long)c));
            // %q produces a literal that loads back to the same bytes
            string q = S("string.format", "%q", s);
            Value[] loaded = _lua.L.Call(_lua.Call1("load", "return " + q));
            Assert.Equal(s, loaded[0].AsString);
            // and %q of numbers/booleans/nil round-trips too
            Assert.Equal(w, _lua.L.Call(_lua.Call1("load", "return " + S("string.format", "%q", w)))[0].I);
            Assert.Equal(w + 0.5, _lua.L.Call(_lua.Call1("load", "return " + S("string.format", "%q", w + 0.5)))[0].N);
        }, 400);
    }

    [Fact]
    public void Format_errors()
    {
        Assert.Contains("bad argument #2 to 'string.format'", _lua.ErrorOf(() => _lua.Call("string.format", "%d")));
        Assert.Contains("invalid conversion", _lua.ErrorOf(() => _lua.Call("string.format", "%y", 1)));
        Assert.Contains("invalid conversion", _lua.ErrorOf(() => _lua.Call("string.format", "%", 1)));
        Assert.Contains("number expected", _lua.ErrorOf(() => _lua.Call("string.format", "%d", "x")));
        Assert.Equal("true nil", S("string.format", "%s %s", true, Value.Nil));
        Assert.Equal("5", S("string.format", "%d", "5"));
    }

    // ------------------------------------------------------------------ patterns vs regular expressions

    private sealed record Atom(string Lua, string Regex);

    private static readonly Gen<Atom> GenSingle = Gen.Frequency(
        (5, Gen.Item('a', 'b', 'c', '1', '2', ' ', '_').Select(c => new Atom(c.ToString(), Regex.Escape(c.ToString())))),
        (1, Gen.Item('.', '-', '%', '(', '+', '?', '*', '[', '^', '$').Select(c => new Atom("%" + c, Regex.Escape(c.ToString())))),
        (2, Gen.Constant(new Atom(".", "[\\s\\S]"))),
        (1, Gen.Constant(new Atom("%a", "[A-Za-z]"))),
        (1, Gen.Constant(new Atom("%d", "[0-9]"))),
        (1, Gen.Constant(new Atom("%s", "[ \\t\\n\\r\\f\\v]"))),
        (1, Gen.Constant(new Atom("%w", "[A-Za-z0-9]"))),
        (1, Gen.Constant(new Atom("%A", "[^A-Za-z]"))),
        (1, Gen.Constant(new Atom("%p", "[!-/:-@\\[-`{-~]"))),
        (1, Gen.Constant(new Atom("[ab]", "[ab]"))),
        (1, Gen.Constant(new Atom("[^ab]", "[^ab]"))),
        (1, Gen.Constant(new Atom("[a-c1]", "[a-c1]"))),
        (1, Gen.Constant(new Atom("[%d_]", "[0-9_]"))),
        (1, Gen.Constant(new Atom("[^%s%d]", "[^ \\t\\n\\r\\f\\v0-9]"))));

    private static readonly Gen<Atom> GenItem =
        from a in GenSingle
        from q in Gen.Frequency((5, Gen.Constant("")), (2, Gen.Constant("*")), (2, Gen.Constant("+")), (1, Gen.Constant("?")), (2, Gen.Constant("-")))
        select new Atom(a.Lua + q, a.Regex + (q == "-" ? "*?" : q));

    /// <summary>Patterns built from single-character items with quantifiers, optional anchors and captures.</summary>
    private static readonly Gen<Atom> GenPattern =
        from items in GenItem.List(Range.LinearInt32(1, 5))
        from capFrom in Gen.Int32(Range.LinearInt32(0, 5))
        from capLen in Gen.Int32(Range.LinearInt32(0, 3))
        from anchorStart in Gen.Frequency((4, Gen.Constant(false)), (1, Gen.Constant(true)))
        from anchorEnd in Gen.Frequency((4, Gen.Constant(false)), (1, Gen.Constant(true)))
        select Build(items, capFrom, capLen, anchorStart, anchorEnd);

    private static Atom Build(List<Atom> items, int capFrom, int capLen, bool anchorStart, bool anchorEnd)
    {
        var lua = new StringBuilder(anchorStart ? "^" : "");
        var re = new StringBuilder(anchorStart ? "\\G" : "");
        for (int i = 0; i < items.Count; i++)
        {
            if (capLen > 0 && i == capFrom) { lua.Append('('); re.Append('('); }
            lua.Append(items[i].Lua);
            re.Append(items[i].Regex);
            if (capLen > 0 && capFrom < items.Count && i == Math.Min(capFrom + capLen, items.Count) - 1) { lua.Append(')'); re.Append(')'); }
        }
        if (anchorEnd) { lua.Append('$'); re.Append("\\z"); }
        return new Atom(lua.ToString(), re.ToString());
    }

    private static readonly Gen<string> GenSubject =
        Gen.Item('a', 'b', 'c', '1', '2', ' ', '_', '.', '-', '\n', 'Z', '%').String(Range.LinearInt32(0, 14));

    [Fact]
    public void Pattern_find_and_match_agree_with_regex()
    {
        Prop.Hh(from p in GenPattern from s in GenSubject from init in Gen.Int32(Range.LinearInt32(1, 6)) select (p, s, init), x =>
        {
            var (p, s, init) = x;
            int start = Math.Min(init, s.Length + 1) - 1;
            var re = new Regex(p.Regex, RegexOptions.CultureInvariant);
            Match m = re.Match(s, start);
            // with a start-anchored pattern Lua only tries the initial position (\G does the same)
            Value[] found = _lua.Call("string.find", s, p.Lua, init);
            Value[] matched = _lua.Call("string.match", s, p.Lua, init);
            if (init > s.Length + 1 || !m.Success)
            {
                Assert.True(found[0].IsNil, $"pattern {p.Lua} on [{s}] from {init}: regex found nothing, Lua found {found[0]}");
                Assert.True(matched[0].IsNil);
                return;
            }
            Assert.False(found[0].IsNil, $"pattern {p.Lua} ({p.Regex}) on [{s}] from {init}: regex matched at {m.Index}, Lua found nothing");
            Assert.Equal(m.Index + 1, found[0].I);
            Assert.Equal(m.Index + m.Length, found[1].I);
            if (m.Groups.Count > 1)
            {
                Assert.Equal(m.Groups[1].Value, found[2].AsString);
                Assert.Equal(m.Groups[1].Value, matched[0].AsString);
            }
            else Assert.Equal(m.Value, matched[0].AsString);
        }, 1500);
    }

    [Fact]
    public void Gmatch_and_gsub_agree_with_regex_for_patterns_that_cannot_match_empty()
    {
        // items quantified with + or nothing never match the empty string
        var genItem = from a in GenSingle from plus in Gen.Item("", "+") select new Atom(a.Lua + plus, a.Regex + plus);
        var gen = from items in genItem.List(Range.LinearInt32(1, 3)) from s in GenSubject select (new Atom(string.Concat(items.Select(i => i.Lua)), string.Concat(items.Select(i => i.Regex))), s);
        Prop.Hh(gen, x =>
        {
            var (p, s) = x;
            var re = new Regex(p.Regex, RegexOptions.CultureInvariant);
            var expected = re.Matches(s).Select(m => m.Value).ToList();
            var got = new List<string>();
            Value[] it = _lua.Call("string.gmatch", s, p.Lua);
            while (true)
            {
                Value[] r = _lua.L.Call(it[0]);
                if (r.Length == 0 || r[0].IsNil) break;
                got.Add(r[0].AsString);
            }
            Assert.Equal(expected, got);
            Value[] sub = _lua.Call("string.gsub", s, p.Lua, "<%0>");
            Assert.Equal(re.Replace(s, "<$0>"), sub[0].AsString);
            Assert.Equal(expected.Count, sub[1].I);
            // limiting the number of substitutions
            Value[] one = _lua.Call("string.gsub", s, p.Lua, "", 1);
            Assert.Equal(re.Replace(s, "", 1), one[0].AsString);
            // a function replacement sees each match; returning false keeps the original text
            int calls = 0;
            Value fn = Value.FromFunction((ref CallContext c) => { calls++; return c.Return(Value.False); }, "keep");
            Assert.Equal(s, _lua.Call("string.gsub", s, p.Lua, fn)[0].AsString);
            Assert.Equal(expected.Count, calls);
        }, 800);
    }

    [Fact]
    public void Pattern_special_items_and_errors()
    {
        Assert.Equal("(b(c)d)", S("string.match", "a(b(c)d)e", "%b()"));
        Assert.Equal(new long[] { 6, 10 }, _lua.Call("string.find", "THE (quick) fox", "%f[%a]%a+", 2).Take(2).Select(v => v.I));
        Assert.Equal(new long[] { 1, 0 }, _lua.Call("string.find", "abc", "").Select(v => v.I));
        Assert.Equal(3, _lua.Call("string.find", "ab", "()", 3)[2].I);                      // position capture
        Assert.Equal("a", S("string.match", "xaay", "(a)%1"));                              // back-reference
        Assert.Equal("-a-b-", S("string.gsub", "ab", "", "-"));
        Assert.Equal("a.b", _lua.Call("string.gsub", "a b", " ", ".")[0].AsString);
        Assert.Contains("malformed pattern", _lua.ErrorOf(() => _lua.Call("string.find", "a", "[a")));
        Assert.Contains("malformed pattern", _lua.ErrorOf(() => _lua.Call("string.find", "a", "%")));
        Assert.Contains("unfinished capture", _lua.ErrorOf(() => _lua.Call("string.find", "a", "(a")));
        Assert.Contains("invalid capture index", _lua.ErrorOf(() => _lua.Call("string.gsub", "a", "a", "%2")));
        Assert.Contains("invalid use of '%' in replacement", _lua.ErrorOf(() => _lua.Call("string.gsub", "a", "a", "%x")));
    }
}
