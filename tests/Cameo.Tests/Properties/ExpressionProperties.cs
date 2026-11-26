using System.Globalization;
using System.Numerics;
using System.Text;
using Hedgehog;
using Hedgehog.Linq;
using Cameo.Runtime.Vm;
using Xunit;
using Gen = Hedgehog.Linq.Gen;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>
/// Differential testing of expression evaluation (Hedgehog): random well-typed integer, float,
/// boolean and string expressions are evaluated by a small reference evaluator written in C#
/// and by the VM (compiled from source, unchecked and type-checked), and must agree.
/// </summary>
public class ExpressionProperties
{
    private readonly LuaHost _unchecked = new(typeCheck: false);
    private readonly LuaHost _checked = new(typeCheck: true);

    // ------------------------------------------------------------------ expression trees

    /// <summary>A typed expression: its Teal source text and its expected value.</summary>
    private sealed record Ex(string Src, object Val);

    private static string Lit(long v) => v == long.MinValue ? "math.mininteger" : v < 0 ? "(" + v.ToString(CultureInfo.InvariantCulture) + ")" : v.ToString(CultureInfo.InvariantCulture);

    private static string Lit(double d)
    {
        if (double.IsNaN(d)) return "(0/0)";
        if (double.IsPositiveInfinity(d)) return "(1/0)";
        if (double.IsNegativeInfinity(d)) return "(-1/0)";
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E')) s += ".0";
        return d < 0 || double.IsNegative(d) ? "(" + s + ")" : s;
    }

    private static string Lit(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 32 || c > 126) sb.Append('\\').Append(((int)c).ToString("000"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // reference semantics, written independently of the VM's helpers
    private static readonly BigInteger Two64 = BigInteger.One << 64;
    private static long Wrap(BigInteger x) => (long)(ulong)(((x % Two64) + Two64) % Two64);

    private static long FloorDiv(long a, long b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
        if (r != 0 && r.Sign != ((BigInteger)b).Sign) q -= 1;
        return Wrap(q);
    }

    private static long FloorMod(long a, long b) => (long)(a - (BigInteger)b * (BigInteger.DivRem(a, b, out BigInteger r) - (r != 0 && r.Sign != ((BigInteger)b).Sign ? 1 : 0)));

    private static long Shl(long x, long n) =>
        n <= -64 || n >= 64 ? 0 : n >= 0 ? Wrap(new BigInteger((ulong)x) << (int)n) : (long)((ulong)x >> (int)-n);

    private static double FMod(double a, double b)
    {
        double r = Math.IEEERemainder(a, b);          // round-to-nearest remainder...
        if (double.IsNaN(r) || double.IsInfinity(b)) return double.IsInfinity(b) && !double.IsInfinity(a) && !double.IsNaN(a) ? ((a >= 0) == (b > 0) || a == 0 ? a : b) : r;
        if (r != 0 && (r < 0) != (b < 0)) r += b;     // ...moved into the divisor's sign
        return r;
    }

    private static string RefSub(string s, long i, long j)
    {
        long l = s.Length;
        if (i < 0) i = Math.Max(l + i + 1, 1); else if (i == 0) i = 1;
        if (j < 0) j = l + j + 1; else if (j > l) j = l;
        return i > j ? "" : s.Substring((int)i - 1, (int)(j - i + 1));
    }

    // ------------------------------------------------------------------ generators

    private static readonly Gen<long> GenIntLit = Gen.Frequency(
        (5, Gen.Int64(Range.LinearFromInt64(0, -50, 50))),
        (2, Gen.Int64(Range.ConstantBoundedInt64())),
        (1, Gen.Item(long.MaxValue, long.MinValue, 1L << 31, 1L << 53, -1L, 0L, 63L, 64L)));

    private static readonly Gen<string> GenStrLit = Gen.Frequency(
        (6, Gen.Char('a', 'e')), (2, Gen.Char(' ', '~')), (1, Gen.Char((char)0, (char)255))).String(Range.LinearInt32(0, 6));

    private static readonly Gen<double> GenNumLit = Gen.Frequency(
        (4, Gen.Double(Range.LinearFromDouble(0, -100, 100))),
        (2, Gen.Int32(Range.LinearFromInt32(0, -20, 20)).Select(i => (double)i)),
        (1, Gen.Item(0.5, -0.5, 1e15, 1e-3, 3.0, -7.0, 0.0, 2.0)));

    private static Ex Bin(Ex a, string op, Ex b, object val) => new("(" + a.Src + " " + op + " " + b.Src + ")", val);

    private static Gen<Ex> Int(int depth) => depth <= 0
        ? GenIntLit.Select(v => new Ex(Lit(v), v))
        : Gen.Frequency(
            (2, GenIntLit.Select(v => new Ex(Lit(v), v))),
            (6, from a in Int(depth - 1)
                from b in Int(depth - 1)
                from op in Gen.Item("+", "-", "*", "&", "|", "~")
                select Bin(a, op, b, op switch
                {
                    "+" => Wrap((BigInteger)(long)a.Val + (long)b.Val),
                    "-" => Wrap((BigInteger)(long)a.Val - (long)b.Val),
                    "*" => Wrap((BigInteger)(long)a.Val * (long)b.Val),
                    "&" => (long)a.Val & (long)b.Val,
                    "|" => (long)a.Val | (long)b.Val,
                    _ => (long)a.Val ^ (long)b.Val,
                })),
            (3, from a in Int(depth - 1)
                from d in GenIntLit.Where(v => v != 0)
                from op in Gen.Item("//", "%")
                select Bin(a, op, new Ex(Lit(d), d), op == "//" ? FloorDiv((long)a.Val, d) : FloorMod((long)a.Val, d))),
            (2, from a in Int(depth - 1)
                from n in Gen.Int64(Range.LinearFromInt64(0, -70, 70))
                from op in Gen.Item("<<", ">>")
                select Bin(a, op, new Ex(Lit(n), n), op == "<<" ? Shl((long)a.Val, n) : Shl((long)a.Val, -n))),
            (1, Int(depth - 1).Select(a => new Ex("(-" + a.Src + ")", Wrap(-(BigInteger)(long)a.Val)))),
            (1, Int(depth - 1).Select(a => new Ex("(~" + a.Src + ")", ~(long)a.Val))),
            (1, Str(depth - 1).Select(s => new Ex("#" + s.Src, (long)((string)s.Val).Length))),
            (2, from c in Bool(depth - 1) from a in Int(depth - 1) from b in Int(depth - 1)
                select new Ex("(" + c.Src + " and " + a.Src + " or " + b.Src + ")", (bool)c.Val ? a.Val : b.Val)));

    private static Gen<Ex> Num(int depth) => depth <= 0
        ? GenNumLit.Select(v => new Ex(Lit(v), v))
        : Gen.Frequency(
            (2, GenNumLit.Select(v => new Ex(Lit(v), v))),
            (6, from a in Num(depth - 1)
                from b in Num(depth - 1)
                from op in Gen.Item("+", "-", "*", "/", "//", "%", "^")
                select Bin(a, op, b, op switch
                {
                    "+" => (double)a.Val + (double)b.Val,
                    "-" => (double)a.Val - (double)b.Val,
                    "*" => (double)a.Val * (double)b.Val,
                    "/" => (double)a.Val / (double)b.Val,
                    "//" => Math.Floor((double)a.Val / (double)b.Val),
                    "%" => FMod((double)a.Val, (double)b.Val),
                    _ => Math.Pow((double)a.Val, (double)b.Val),
                })),
            (2, from a in Gen.Int64(Range.LinearFromInt64(0, -1000, 1000)) from b in Gen.Int64(Range.LinearFromInt64(1, -1000, 1000))
                select new Ex("(" + Lit(a) + " / " + Lit(b) + ")", (double)a / b)),
            (1, Num(depth - 1).Select(a => new Ex("(-" + a.Src + ")", -(double)a.Val))),
            (1, from c in Bool(depth - 1) from a in Num(depth - 1) from b in Num(depth - 1)
                select new Ex("(" + c.Src + " and " + a.Src + " or " + b.Src + ")", (bool)c.Val ? a.Val : b.Val)));

    private static Gen<Ex> Bool(int depth) => depth <= 0
        ? Gen.Item(false, true).Select(v => new Ex(v ? "true" : "false", v))
        : Gen.Frequency(
            (1, Gen.Item(false, true).Select(v => new Ex(v ? "true" : "false", v))),
            (5, from a in Int(depth - 1)
                from b in Gen.Frequency((3, Int(depth - 1)), (1, Gen.Constant(a)))
                from op in Gen.Item("==", "~=", "<", "<=", ">", ">=")
                select Bin(a, op, b, Compare(((long)a.Val).CompareTo((long)b.Val), op))),
            (3, from a in Num(depth - 1)
                from b in Num(depth - 1)
                from op in Gen.Item("==", "~=", "<", "<=", ">", ">=")
                select Bin(a, op, b, op switch
                {
                    "==" => (double)a.Val == (double)b.Val,
                    "~=" => (double)a.Val != (double)b.Val,
                    "<" => (double)a.Val < (double)b.Val,
                    "<=" => (double)a.Val <= (double)b.Val,
                    ">" => (double)a.Val > (double)b.Val,
                    _ => (double)a.Val >= (double)b.Val,
                })),
            (3, from a in Str(depth - 1)
                from b in Gen.Frequency((3, Str(depth - 1)), (1, Gen.Constant(a)))
                from op in Gen.Item("==", "~=", "<", "<=", ">", ">=")
                select Bin(a, op, b, Compare(string.CompareOrdinal((string)a.Val, (string)b.Val), op))),
            (3, from a in Bool(depth - 1) from b in Bool(depth - 1) from op in Gen.Item("and", "or", "==", "~=")
                select Bin(a, op, b, op switch
                {
                    "and" => (bool)a.Val && (bool)b.Val,
                    "or" => (bool)a.Val || (bool)b.Val,
                    "==" => (bool)a.Val == (bool)b.Val,
                    _ => (bool)a.Val != (bool)b.Val,
                })),
            (2, Bool(depth - 1).Select(a => new Ex("(not " + a.Src + ")", !(bool)a.Val))));

    private static bool Compare(int c, string op) => op switch
    {
        "==" => c == 0, "~=" => c != 0, "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0,
    };

    private static Gen<Ex> Str(int depth) => depth <= 0
        ? GenStrLit.Select(v => new Ex(Lit(v), v))
        : Gen.Frequency(
            (2, GenStrLit.Select(v => new Ex(Lit(v), v))),
            (4, from a in Str(depth - 1) from b in Str(depth - 1) select Bin(a, "..", b, (string)a.Val + (string)b.Val)),
            (2, from a in Str(depth - 1) from b in Int(depth - 1)
                select Bin(a, "..", b, (string)a.Val + ((long)b.Val).ToString(CultureInfo.InvariantCulture))),
            (1, from a in Str(depth - 1) from n in Gen.Int32(Range.LinearInt32(-1, 3))
                select new Ex("(" + a.Src + "):rep(" + n + ")", string.Concat(Enumerable.Repeat((string)a.Val, Math.Max(n, 0))))),
            (1, Str(depth - 1).Select(a => new Ex("(" + a.Src + "):upper()", new string(((string)a.Val).Select(c => c is >= 'a' and <= 'z' ? (char)(c - 32) : c).ToArray())))),
            (1, Str(depth - 1).Select(a => new Ex("(" + a.Src + "):reverse()", new string(((string)a.Val).Reverse().ToArray())))),
            (2, from a in Str(depth - 1) from i in Gen.Int64(Range.LinearFromInt64(1, -8, 8)) from j in Gen.Int64(Range.LinearFromInt64(-1, -8, 8))
                select new Ex("(" + a.Src + "):sub(" + Lit(i) + ", " + Lit(j) + ")", RefSub((string)a.Val, i, j))),
            (1, Int(depth - 1).Select(a => new Ex("tostring(" + a.Src + ")", ((long)a.Val).ToString(CultureInfo.InvariantCulture)))),
            (1, from c in Bool(depth - 1) from a in Str(depth - 1) from b in Str(depth - 1)
                select new Ex("(" + c.Src + " and " + a.Src + " or " + b.Src + ")", (bool)c.Val ? a.Val : b.Val)));

    private static readonly Gen<int> GenDepth = Gen.Int32(Range.LinearInt32(0, 4));

    private static readonly Gen<(Ex Ex, string Type)> GenAny =
        from depth in GenDepth
        from x in Gen.Choice(
            Int(depth).Select(e => (e, "integer")),
            Num(depth).Select(e => (e, "number")),
            Bool(depth).Select(e => (e, "boolean")),
            Str(depth).Select(e => (e, "string")))
        select x;

    // ------------------------------------------------------------------ properties

    private static void AssertSame(object expected, Value actual, string src)
    {
        switch (expected)
        {
            case long l:
                Assert.True(actual.IsInteger && actual.I == l, $"{src}\n  expected integer {l}, got {actual.Kind} {actual}");
                break;
            case double d:
                Assert.True(actual.IsFloat && actual.N.Equals(d), $"{src}\n  expected float {d:R}, got {actual.Kind} {actual}");
                break;
            case bool b:
                Assert.True(actual.IsBoolean && actual.IsTruthy == b, $"{src}\n  expected {b}, got {actual.Kind} {actual}");
                break;
            case string s:
                Assert.True(actual.IsString && actual.AsString == s, $"{src}\n  expected string [{s}], got {actual.Kind} [{actual}]");
                break;
        }
    }

    [Fact]
    public void Unchecked_evaluation_agrees_with_the_reference_evaluator()
    {
        Prop.Hh(GenAny, x =>
        {
            Value[] r = _unchecked.Run("return " + x.Ex.Src);
            Assert.Single(r);
            AssertSame(x.Ex.Val, r[0], x.Ex.Src);
        }, 1500);
    }

    [Fact]
    public void Type_checked_evaluation_agrees_with_the_reference_evaluator()
    {
        Prop.Hh(GenAny, x =>
        {
            // the declared type must be accepted by the checker, and the result must be the same
            Value[] r = _checked.Run($"local v: {x.Type} = {x.Ex.Src}\nreturn v");
            Assert.Single(r);
            AssertSame(x.Ex.Val, r[0], x.Ex.Src);
        }, 600);
    }

    [Fact]
    public void Expressions_evaluate_the_same_through_locals_tables_and_calls()
    {
        // the same value must come out whether the expression is used directly, stored in a
        // local, stored in a table field, passed through a function, or captured by a closure
        Prop.Hh(GenAny, x =>
        {
            string src = x.Ex.Src;
            Value[] r = _unchecked.Run($@"
                local a = {src}
                local t = {{ f = {src}, {src} }}
                local function id(...) return ... end
                local function mk() local c = {src} return function() return c end end
                return a, t.f, t[1], id({src}), mk()(), (select(2, pcall(id, {src})))");
            Assert.Equal(6, r.Length);
            foreach (Value v in r) AssertSame(x.Ex.Val, v, src);
        }, 400);
    }

    [Fact]
    public void Conditions_branch_like_their_value()
    {
        Prop.Hh(from d in GenDepth from c in Bool(d) select c, c =>
        {
            Value[] r = _unchecked.Run($@"
                local n = 0
                if {c.Src} then n = n + 1 end
                if not {c.Src} then n = n + 10 end
                while {c.Src} do n = n + 100 break end
                repeat n = n + 1000 until {c.Src} or true
                local v = {c.Src}
                return n, v, v and 1 or 2");
            bool b = (bool)c.Val;
            Assert.Equal(b ? 1101 : 1010, r[0].I);
            Assert.Equal(b, r[1].IsTruthy);
            Assert.Equal(b ? 1 : 2, r[2].I);
        }, 400);
    }
}
