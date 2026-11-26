using System.Globalization;
using System.Numerics;
using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>
/// Number semantics (CsCheck): integer arithmetic against a BigInteger model, exact
/// integer/float comparison, value equality and hashing, numeral parsing and formatting.
/// </summary>
public class NumberProperties
{
    private static readonly BigInteger Two64 = BigInteger.One << 64;

    /// <summary>Wraps a mathematical integer to a signed 64-bit integer (two's complement).</summary>
    private static long Wrap(BigInteger x)
    {
        BigInteger m = ((x % Two64) + Two64) % Two64;
        return (long)(ulong)m;
    }

    private static BigInteger FloorDiv(BigInteger a, BigInteger b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
        if (r != 0 && (r.Sign != b.Sign)) q -= 1;
        return q;
    }

    // Integers concentrated around the interesting boundaries.
    private static readonly Gen<long> GenLong = Gen.Frequency(
        (4, Gen.Long),
        (4, Gen.Long[-20, 20]),
        (2, Gen.Select(Gen.OneOf(long.MaxValue, long.MinValue, 1L << 53, -(1L << 53), 1L << 62, 1L << 32, 0L), Gen.Long[-3, 3], (a, d) => unchecked(a + d))));

    private static readonly Gen<double> GenDouble = Gen.Frequency(
        (4, Gen.Double[-1e6, 1e6]),
        (3, GenLong.Select(l => (double)l)),
        (2, Gen.Select(GenLong, Gen.Double[-2, 2], (l, d) => l + d)),
        (1, Gen.Double),
        (1, Gen.OneOf(0.0, -0.0, 0.5, -0.5, 9007199254740992.0, 9223372036854775808.0, -9223372036854775808.0, double.MaxValue, double.Epsilon)),
        (1, Gen.OneOf(double.PositiveInfinity, double.NegativeInfinity, double.NaN)));

    // ------------------------------------------------------------------ integer arithmetic

    [Fact]
    public void Integer_floor_division_and_modulo_match_the_mathematical_definition()
    {
        Prop.Cs(Gen.Select(GenLong, GenLong), p =>
        {
            var (x, y) = p;
            if (y == 0)
            {
                Assert.Throws<LuaException>(() => LuaState.IntFloorDiv(x, y));
                Assert.Throws<LuaException>(() => LuaState.IntMod(x, y));
                return;
            }
            long q = LuaState.IntFloorDiv(x, y);
            long r = LuaState.IntMod(x, y);
            Assert.Equal(Wrap(FloorDiv(x, y)), q);
            Assert.Equal((long)(x - FloorDiv(x, y) * (BigInteger)y), r);
            // the remainder takes the sign of the divisor and is smaller in magnitude
            Assert.True(r == 0 || Math.Sign(r) == Math.Sign(y));
            Assert.True(BigInteger.Abs(r) < BigInteger.Abs(y));
            // x == q * y + r in wrap-around arithmetic
            Assert.Equal(x, unchecked(q * y + r));
        }, iter: 5000);
    }

    [Fact]
    public void Shifts_are_logical_and_saturate_at_64_bits()
    {
        Prop.Cs(Gen.Select(GenLong, Gen.OneOf(Gen.Long[-70, 70], GenLong)), p =>
        {
            var (x, n) = p;
            BigInteger ux = (ulong)x;
            long expected;
            if (n <= -64 || n >= 64) expected = 0;
            else if (n >= 0) expected = Wrap(ux << (int)n);
            else expected = Wrap(ux >> (int)-n);
            Assert.Equal(expected, LuaState.ShiftLeft(x, n));
        }, iter: 5000);
    }

    [Fact]
    public void Integer_add_sub_mul_wrap_around_and_bitwise_ops_match()
    {
        var L = new LuaState();
        Prop.Cs(Gen.Select(GenLong, GenLong), p =>
        {
            var (x, y) = p;
            Value a = Value.FromInt(x), b = Value.FromInt(y);
            Assert.Equal(Wrap((BigInteger)x + y), L.Arith(OpCode.Add, a, b).I);
            Assert.Equal(Wrap((BigInteger)x - y), L.Arith(OpCode.Sub, a, b).I);
            Assert.Equal(Wrap((BigInteger)x * y), L.Arith(OpCode.Mul, a, b).I);
            Assert.Equal(x & y, L.Arith(OpCode.BAnd, a, b).I);
            Assert.Equal(x | y, L.Arith(OpCode.BOr, a, b).I);
            Assert.Equal(x ^ y, L.Arith(OpCode.BXor, a, b).I);
            Assert.Equal(LuaState.ShiftLeft(x, y), L.Arith(OpCode.Shl, a, b).I);
            Assert.Equal(LuaState.ShiftLeft(x, unchecked(-y)), L.Arith(OpCode.Shr, a, b).I);
            Assert.Equal(Wrap(-(BigInteger)x), L.Unm(a).I);
            Assert.Equal(~x, L.BNot(a).I);
            // division and exponentiation always produce floats
            Assert.True(L.Arith(OpCode.Div, a, b).IsFloat);
            Assert.True(L.Arith(OpCode.Pow, a, b).IsFloat);
            Assert.Equal((double)x / y, L.Arith(OpCode.Div, a, b).N);
            // integer-valued floats are accepted by bitwise operators; others are not
            Assert.Equal(x & y, L.Arith(OpCode.BAnd, a, FloatIfExact(y)).I);
            Assert.Throws<LuaException>(() => L.Arith(OpCode.BAnd, a, Value.FromFloat(0.5)));
        }, iter: 3000);
    }

    private static Value FloatIfExact(long y) => y > -(1L << 62) && y < (1L << 62) && (long)(double)y == y ? Value.FromFloat(y) : Value.FromInt(y);

    /// <summary>Exact value of a finite double as mantissa * 2^exponent.</summary>
    private static (BigInteger Mant, int Exp) Decompose(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        int e = (int)((bits >> 52) & 0x7FF);
        long m = bits & 0xFFFFFFFFFFFFFL;
        if (e == 0) e++; else m |= 1L << 52;
        return (bits < 0 ? -m : m, e - 1075);
    }

    [Fact]
    public void Float_modulo_matches_the_exact_remainder()
    {
        Prop.Cs(Gen.Select(Gen.Double[-1e9, 1e9], Gen.OneOf(Gen.Double[-1e4, 1e4], Gen.Int[-9, 9].Select(i => (double)i))), p =>
        {
            var (a, b) = p;
            if (b == 0) return;
            // exact truncated remainder (fmod is exact in IEEE arithmetic)
            var (ma, ea) = Decompose(a);
            var (mb, eb) = Decompose(b);
            int e = Math.Min(ea, eb);
            BigInteger A = ma << (ea - e), B = mb << (eb - e);
            BigInteger rem = BigInteger.Remainder(A, B);
            double fmod = Math.ScaleB((double)rem, e);
            // Lua: the result takes the sign of the divisor
            double expected = fmod != 0 && (fmod < 0) != (b < 0) ? fmod + b : fmod;
            double actual = LuaState.FloatMod(a, b);
            Assert.Equal(expected, actual);
            Assert.True(actual == 0 || (actual < 0) == (b < 0));
            Assert.True(Math.Abs(actual) <= Math.Abs(b));
        }, iter: 5000);
    }

    [Fact]
    public void Float_modulo_special_cases()
    {
        // regression (found by Float_modulo_matches_the_exact_remainder): with both operands
        // negative the divisor was added once too often, e.g. -7.0 % -3.0 gave -4.0
        Assert.Equal(-1.0, LuaState.FloatMod(-7, -3));
        Assert.Equal(-0.25, LuaState.FloatMod(-1, -0.75));
        Assert.True(double.IsNaN(LuaState.FloatMod(1, 0)));
        Assert.True(double.IsNaN(LuaState.FloatMod(double.PositiveInfinity, 2)));
        Assert.Equal(5.0, LuaState.FloatMod(5, double.PositiveInfinity));
        Assert.Equal(double.PositiveInfinity, LuaState.FloatMod(-5, double.PositiveInfinity)); // as PUC Lua
        Assert.Equal(double.NegativeInfinity, LuaState.FloatMod(5, double.NegativeInfinity));
        Assert.Equal(-5.0, LuaState.FloatMod(-5, double.NegativeInfinity));
        Assert.Equal(1.5, LuaState.FloatMod(-6.5, 2));
        Assert.Equal(-0.5, LuaState.FloatMod(5.5, -2));
    }

    // ------------------------------------------------------------------ comparison

    /// <summary>Exact comparison of an integer with a finite double.</summary>
    private static int ExactCompare(long i, double f)
    {
        var (m, e) = Decompose(f);
        // compare i * 2^-e with m (e may be negative or positive)
        BigInteger left = i, right = m;
        if (e >= 0) right <<= e; else left <<= -e;
        return left.CompareTo(right);
    }

    [Fact]
    public void Integer_float_comparisons_are_exact()
    {
        var L = new LuaState();
        Prop.Cs(Gen.Select(GenLong, GenDouble), p =>
        {
            var (i, f) = p;
            Value vi = Value.FromInt(i), vf = Value.FromFloat(f);
            if (double.IsNaN(f))
            {
                Assert.False(L.LessThan(vi, vf)); Assert.False(L.LessThan(vf, vi));
                Assert.False(L.LessEqual(vi, vf)); Assert.False(L.LessEqual(vf, vi));
                Assert.False(L.Equals(vi, vf)); Assert.False(vi.RawEquals(vf));
                return;
            }
            int c = double.IsPositiveInfinity(f) ? -1 : double.IsNegativeInfinity(f) ? 1 : ExactCompare(i, f);
            Assert.Equal(c < 0, L.LessThan(vi, vf));
            Assert.Equal(c <= 0, L.LessEqual(vi, vf));
            Assert.Equal(c > 0, L.LessThan(vf, vi));
            Assert.Equal(c >= 0, L.LessEqual(vf, vi));
            Assert.Equal(c == 0, L.Equals(vi, vf));
            Assert.Equal(c == 0, vf.RawEquals(vi));
            if (c == 0) Assert.Equal(vi.GetHashCode(), vf.GetHashCode());
        }, iter: 20000);
    }

    [Fact]
    public void Same_kind_comparisons_agree_with_dotnet()
    {
        var L = new LuaState();
        Prop.Cs(Gen.Select(GenLong, GenLong, GenDouble, GenDouble), p =>
        {
            var (a, b, x, y) = p;
            Assert.Equal(a < b, L.LessThan(Value.FromInt(a), Value.FromInt(b)));
            Assert.Equal(a <= b, L.LessEqual(Value.FromInt(a), Value.FromInt(b)));
            Assert.Equal(a == b, L.Equals(Value.FromInt(a), Value.FromInt(b)));
            Assert.Equal(x < y, L.LessThan(Value.FromFloat(x), Value.FromFloat(y)));
            Assert.Equal(x <= y, L.LessEqual(Value.FromFloat(x), Value.FromFloat(y)));
            Assert.Equal(x == y, L.Equals(Value.FromFloat(x), Value.FromFloat(y)));
        }, iter: 3000);
    }

    [Fact]
    public void String_comparison_is_bytewise_and_mixed_comparisons_fail()
    {
        var L = new LuaState();
        Prop.Cs(Gen.Select(Gen.String[Gen.Char[(char)0, (char)255], 0, 6], Gen.String[Gen.Char[(char)0, (char)255], 0, 6]), p =>
        {
            var (a, b) = p;
            int c = string.CompareOrdinal(a, b);
            Assert.Equal(c < 0, L.LessThan(a, b));
            Assert.Equal(c <= 0, L.LessEqual(a, b));
            Assert.Equal(c == 0, L.Equals(a, b));
            Assert.Throws<LuaException>(() => L.LessThan(a, Value.FromInt(1)));
            Assert.Throws<LuaException>(() => L.LessEqual(Value.FromInt(1), a));
            Assert.False(L.Equals(a, Value.FromInt(1)));
        });
    }

    // ------------------------------------------------------------------ values as keys

    private static readonly Gen<Value> GenValue = Gen.OneOf(
        GenLong.Select(l => Value.FromInt(l)),
        GenDouble.Select(d => Value.FromFloat(d)),
        Gen.String[Gen.Char['a', 'c'], 0, 3].Select(s => Value.FromString(s)),
        Gen.Bool.Select(b => Value.FromBool(b)),
        Gen.Const(Value.Nil));

    [Fact]
    public void Equal_values_have_equal_hashes_and_equality_is_an_equivalence()
    {
        Prop.Cs(Gen.Select(GenValue, GenValue, GenValue), p =>
        {
            var (a, b, c) = p;
            bool nan = a.IsFloat && double.IsNaN(a.N);
            Assert.Equal(!nan, a.RawEquals(a));
            Assert.Equal(a.RawEquals(b), b.RawEquals(a));
            Assert.Equal(a.RawEquals(b), a.Equals(b));
            Assert.Equal(a.RawEquals(b), a.Equals((object)b));
            if (a.RawEquals(b)) Assert.Equal(a.GetHashCode(), b.GetHashCode());
            if (a.RawEquals(b) && b.RawEquals(c)) Assert.True(a.RawEquals(c));
            if (a.Kind != b.Kind && !(a.IsNumber && b.IsNumber)) Assert.False(a.RawEquals(b));
            // truthiness: only nil and false are falsy
            Assert.Equal(!(a.IsNil || (a.IsBoolean && a.I == 0)), a.IsTruthy);
            Assert.Equal(!a.IsTruthy, a.IsFalsy);
        }, iter: 5000);
    }

    [Fact]
    public void FloatToInteger_is_exact()
    {
        Prop.Cs(GenDouble, d =>
        {
            bool ok = LuaNumber.FloatToInteger(d, out long i);
            bool expected = !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d
                            && new BigInteger(d) >= long.MinValue && new BigInteger(d) <= long.MaxValue;
            Assert.Equal(expected, ok);
            if (ok) Assert.Equal(new BigInteger(d), i);
            // TryToInteger / TryToDouble on values agree
            Assert.Equal(expected, Value.FromFloat(d).TryToInteger(out long j));
            if (expected) Assert.Equal(i, j);
            Assert.True(Value.FromFloat(d).TryToDouble(out double back));
            Assert.Equal(d, back);
        }, iter: 5000);
    }

    // ------------------------------------------------------------------ parsing and formatting

    [Fact]
    public void Integers_round_trip_through_text()
    {
        Prop.Cs(GenLong, i =>
        {
            string s = Value.FromInt(i).ToString();
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), s);
            Assert.True(LuaNumber.TryParse(s, out Value v));
            Assert.True(v.IsInteger);
            Assert.Equal(i, v.I);
            // surrounding whitespace and an explicit sign are accepted
            Assert.True(LuaNumber.TryParse("  " + s + "\t\n", out v) && v.IsInteger && v.I == i);
            if (i >= 0) Assert.True(LuaNumber.TryParse("+" + s, out v) && v.I == i);
            // hexadecimal integers wrap around modulo 2^64
            Assert.True(LuaNumber.TryParse("0x" + i.ToString("x"), out v) && v.IsInteger && v.I == i);
            Assert.True(LuaNumber.TryParse("0X" + i.ToString("X"), out v) && v.I == i);
            Assert.True(LuaNumber.TryParse("-0x" + i.ToString("x"), out v) && v.I == unchecked(-i));
            // and string -> integer coercion on values goes the same way
            Assert.True(Value.FromString(s).TryToInteger(out long back) && back == i);
        }, iter: 3000);
    }

    [Fact]
    public void Decimal_integers_beyond_64_bits_become_floats()
    {
        Prop.Cs(Gen.Select(GenLong, Gen.Int[0, 999]), p =>
        {
            BigInteger big = (BigInteger)p.Item1 * 1000 + (p.Item1 < 0 ? -p.Item2 : p.Item2);
            Assert.True(LuaNumber.TryParse(big.ToString(), out Value v));
            if (big >= long.MinValue && big <= long.MaxValue)
            {
                Assert.True(v.IsInteger);
                Assert.Equal((long)big, v.I);
            }
            else
            {
                Assert.True(v.IsFloat);
                Assert.Equal(double.Parse(big.ToString(), CultureInfo.InvariantCulture), v.N);
            }
        }, iter: 3000);
    }

    [Fact]
    public void Floats_round_trip_through_text()
    {
        Prop.Cs(GenDouble.Where(d => !double.IsNaN(d) && !double.IsInfinity(d)), d =>
        {
            // shortest round-trip text parses back exactly, as a float
            string r = d.ToString("R", CultureInfo.InvariantCulture);
            Assert.True(LuaNumber.TryParse(r, out Value v), r);
            if (r.Contains('.') || r.Contains('E'))
            {
                Assert.True(v.IsFloat, r);
                Assert.Equal(d, v.N);
            }
            // %.17g always round-trips
            Assert.True(LuaNumber.TryParse(LuaNumber.FormatG(d, 17, false), out v));
            Assert.Equal(d, v.AsDouble);
            // tostring() uses %.14g and always reads back as a float within that precision
            string s = LuaNumber.FloatToString(d);
            Assert.True(LuaNumber.TryParse(s, out v), s);
            Assert.True(v.IsFloat, s);
            Assert.Equal(double.Parse(d.ToString("E13", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), v.N);
            Assert.Equal(s, Value.FromFloat(d).ToString());
        }, iter: 5000);
    }

    [Fact]
    public void FormatG_matches_C_printf_shape()
    {
        Prop.Cs(Gen.Select(GenDouble.Where(d => !double.IsNaN(d) && !double.IsInfinity(d)), Gen.Int[1, 17]), p =>
        {
            var (d, prec) = p;
            string s = LuaNumber.FormatG(d, prec, false);
            // value is preserved to the requested number of significant digits
            double expected = double.Parse(d.ToString("E" + (prec - 1), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            Assert.Equal(expected, double.Parse(s, CultureInfo.InvariantCulture));
            // %g never leaves trailing zeros in the fraction, and uses an exponent of at least two digits
            int e = s.IndexOf('e');
            string mant = e < 0 ? s : s.Substring(0, e);
            if (mant.Contains('.')) Assert.False(mant.EndsWith('0') || mant.EndsWith('.'), s);
            if (e >= 0) Assert.Matches(@"e[+-]\d\d+$", s);
            // scientific notation is used exactly when the exponent is < -4 or >= precision
            if (d != 0)
            {
                int exp10 = int.Parse(d.ToString("E" + (prec - 1), CultureInfo.InvariantCulture).Split('E')[1], CultureInfo.InvariantCulture);
                Assert.Equal(exp10 < -4 || exp10 >= prec, e >= 0);
            }
            // the alternate form keeps exactly `prec` significant digits
            string alt = LuaNumber.FormatG(d, prec, true);
            Assert.Equal(expected, double.Parse(alt, CultureInfo.InvariantCulture));
            string altMant = alt.Split('e')[0].TrimStart('-');
            if (prec > 1 && d != 0) Assert.Equal(prec, altMant.Count(char.IsAsciiDigit) - (altMant.StartsWith("0.") ? altMant.Substring(2).TakeWhile(ch => ch == '0').Count() + 1 : 0));
            // upper-case variant only changes the exponent marker
            Assert.Equal(s.ToUpperInvariant(), LuaNumber.FormatG(d, prec, false, upper: true));
        }, iter: 5000);
    }

    [Fact]
    public void Special_floats_format_like_Lua()
    {
        Assert.Equal("inf", LuaNumber.FloatToString(double.PositiveInfinity));
        Assert.Equal("-inf", LuaNumber.FloatToString(double.NegativeInfinity));
        Assert.Contains("nan", LuaNumber.FloatToString(double.NaN));
        Assert.Equal("1.0", LuaNumber.FloatToString(1));
        Assert.Equal("-0.0", LuaNumber.FloatToString(-0.0));
        Assert.Equal("1e+15", LuaNumber.FloatToString(1e15));
        Assert.Equal("1e+100", LuaNumber.FloatToString(1e100));
        Assert.Equal("0.1", LuaNumber.FloatToString(0.1));
        Assert.Equal("9.007199254741e+15", LuaNumber.FloatToString(9007199254740992.0));
        Assert.Equal("0", LuaNumber.FormatG(0, 6, false));
        Assert.Equal("-0", LuaNumber.FormatG(-0.0, 6, false));
        Assert.Equal("0.00000", LuaNumber.FormatG(0, 6, true));
        Assert.Equal("INF", LuaNumber.FormatG(double.PositiveInfinity, 6, false, true));
        Assert.Equal("-inf", LuaNumber.FormatG(double.NegativeInfinity, 6, false));
        Assert.Equal("nan", LuaNumber.FormatG(double.NaN, 6, false));
        Assert.Equal("1e+02", LuaNumber.FormatG(100, 0, false)); // precision 0 means 1
    }

    [Fact]
    public void Malformed_numerals_are_rejected()
    {
        foreach (string s in new[] { "", " ", "-", "+", "0x", "0xg", "1e", "1e+", "e5", ".", "1..2", "1 2", "0x1p", "--1", "1f", "abc", "0x.p1", "1e5e5", "١" })
            Assert.False(LuaNumber.TryParse(s, out _), s);
        Assert.True(LuaNumber.TryParse("0x.8p1", out Value v) && v.IsFloat && v.N == 1.0);
        Assert.True(LuaNumber.TryParse("0x10p-1", out v) && v.N == 8.0);
        Assert.True(LuaNumber.TryParse("0xA.8", out v) && v.N == 10.5);
        Assert.True(LuaNumber.TryParse("5.", out v) && v.IsFloat && v.N == 5.0);
        Assert.True(LuaNumber.TryParse(".5", out v) && v.N == 0.5);
        Assert.True(LuaNumber.TryParse("1E2", out v) && v.IsFloat && v.N == 100.0);
        Assert.True(LuaNumber.TryParse("-9223372036854775808", out v) && v.IsInteger && v.I == long.MinValue);
        Assert.True(LuaNumber.TryParse("9223372036854775808", out v) && v.IsFloat);
        Assert.True(LuaNumber.TryParse("0xffffffffffffffff", out v) && v.I == -1);
        Assert.Equal(-1, LuaNumber.HexVal('g'));
        Assert.Equal(15, LuaNumber.HexVal('F'));
    }
}
