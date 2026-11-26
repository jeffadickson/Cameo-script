using System.Buffers.Binary;
using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// FloatArray against a plain double[] model: element access, element-wise arithmetic
/// (vectorised in the VM, scalar here), reductions, slicing, binary formats, and the limits.
/// </summary>
public class FloatArrayTests
{
    private static readonly LuaHost Host = new();
    private static readonly object Gate = new();

    private static Value[] Run(string source)
    {
        lock (Gate) return Host.Run(source);
    }

    private static Value Arr(double[] xs) => Value.FromUserdata(new FloatArray(Host.L, (double[])xs.Clone()));
    private static double[] Data(Value v) => ((FloatArray)v.O!).Data;

    private static Value[] Call(string body, params Value[] args)
    {
        lock (Gate)
        {
            Value fn = Host.Run("return function(a, b, k) " + body + " end")[0];
            return Host.L.Call(fn, args);
        }
    }

    // values that exercise rounding, signs, zeros and the special floats; lengths around the
    // SIMD width so that both the vector loop and the scalar tail run
    private static readonly Gen<double> GenValue = Gen.Frequency(
        (6, Gen.Double[-1000, 1000]),
        (2, Gen.Int[-5, 5].Select(i => (double)i)),
        (1, Gen.OneOf(Gen.Const(0.0), Gen.Const(-0.0), Gen.Const(double.NaN), Gen.Const(double.PositiveInfinity), Gen.Const(double.NegativeInfinity), Gen.Const(double.Epsilon), Gen.Const(double.MaxValue))));

    private static readonly Gen<(double[] A, double[] B, double K)> GenPair =
        Gen.Int[0, 21].SelectMany(n => Gen.Select(GenValue.Array[n], GenValue.Array[n], GenValue));

    private static void Same(IEnumerable<double> expected, double[] actual) =>
        Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), actual.Select(BitConverter.DoubleToInt64Bits));

    [Fact]
    public void Elementwise_arithmetic_matches_scalar_arithmetic_bit_for_bit()
    {
        Prop.Cs(GenPair, t =>
        {
            var (a, b, k) = t;
            Value[] r = Call("return a + b, a - b, a * b, a / b, a + k, a - k, a * k, a / k, k + a, k - a, k * a, k / a, -a", Arr(a), Arr(b), k);
            Same(a.Zip(b, (x, y) => x + y), Data(r[0]));
            Same(a.Zip(b, (x, y) => x - y), Data(r[1]));
            Same(a.Zip(b, (x, y) => x * y), Data(r[2]));
            Same(a.Zip(b, (x, y) => x / y), Data(r[3]));
            Same(a.Select(x => x + k), Data(r[4]));
            Same(a.Select(x => x - k), Data(r[5]));
            Same(a.Select(x => x * k), Data(r[6]));
            Same(a.Select(x => x / k), Data(r[7]));
            Same(a.Select(x => k + x), Data(r[8]));
            Same(a.Select(x => k - x), Data(r[9]));
            Same(a.Select(x => k * x), Data(r[10]));
            Same(a.Select(x => k / x), Data(r[11]));
            Same(a.Select(x => x * -1.0), Data(r[12]));
        }, iter: 300);
    }

    [Fact]
    public void In_place_arithmetic_changes_the_array_and_operators_do_not()
    {
        Prop.Cs(GenPair, t =>
        {
            var (a, b, k) = t;
            Value va = Arr(a), vb = Arr(b);
            Value[] r = Call("local c = a:copy() local same = c:addInPlace(b):mulInPlace(k):subInPlace(b):divInPlace(k) == c or #c == 0 or c ~= c return c, a + b, rawequal(c:addInPlace(0), c)", va, vb, k);
            Same(a.Zip(b, (x, y) => ((x + y) * k - y) / k).Select(x => x + 0), Data(r[0]));
            Same(a, Data(va)); // untouched by + and by copy
            Same(b, Data(vb));
            Assert.True(r[2].AsBool);
        }, iter: 200);
    }

    [Fact]
    public void Reductions_match_a_left_to_right_loop()
    {
        Prop.Cs(Gen.Double[-1e6, 1e6].Array[1, 40], a =>
        {
            Value[] r = Call("local lo, li = a:min() local hi, hj = a:max() return a:sum(), a:mean(), lo, li, hi, hj, a:rms(), a:variance(), a:dot(a)", Arr(a));
            double sum = 0, sq = 0;
            foreach (double d in a) { sum += d; sq += d * d; }
            double mean = sum / a.Length, var = 0;
            foreach (double d in a) var += (d - mean) * (d - mean);
            Assert.Equal(sum, r[0].N);
            Assert.Equal(mean, r[1].N);
            Assert.Equal(a.Min(), r[2].N);
            Assert.Equal(Array.IndexOf(a, a.Min()) + 1, r[3].I);
            Assert.Equal(a.Max(), r[4].N);
            Assert.Equal(Array.IndexOf(a, a.Max()) + 1, r[5].I);
            Assert.Equal(Math.Sqrt(sq / a.Length), r[6].N);
            Assert.Equal(var / a.Length, r[7].N);
            Assert.Equal(sq, r[8].N);
        }, iter: 200);
    }

    [Fact]
    public void Indexing_slicing_and_filling_follow_the_model()
    {
        Prop.Cs(Gen.Select(Gen.Double[-9, 9].Array[0, 12], Gen.Int[-15, 15], Gen.Int[-15, 15]), t =>
        {
            var (a, i, j) = t;
            Value[] r = Call($"""
                local n, sum = 0, 0
                for k, v in ipairs(a) do n = n + 1 sum = sum + v end
                local viaIndex = FloatArray.new(#a)
                for k = 1, #a do viaIndex[k] = a[k] end
                return #a, n, a[{i}], a:get({i}), a:slice({i}, {j}), a:copy():fill(7, {i}, {j}), viaIndex == a, FloatArray.from(a:totable()) == a, a[#a + 1], a[0]
                """, Arr(a));
            int n = a.Length;
            Assert.Equal(n, r[0].I);
            Assert.Equal(n, r[1].I);
            if (i >= 1 && i <= n) { Assert.Equal(a[i - 1], r[2].N); Assert.Equal(a[i - 1], r[3].N); }
            else Assert.True(r[2].IsNil && r[3].IsNil);
            // string.sub's index rules
            int from = i < 0 ? Math.Max(n + i + 1, 1) : i == 0 ? 1 : i;
            int to = j < 0 ? n + j + 1 : Math.Min(j, n);
            int count = Math.Max(to - from + 1, 0);
            Same(a.Skip(from - 1).Take(count), Data(r[4]));
            double[] filled = (double[])a.Clone();
            for (int k = from - 1; k < from - 1 + count; k++) filled[k] = 7;
            Same(filled, Data(r[5]));
            Assert.True(r[6].AsBool && r[7].AsBool);
            Assert.True(r[8].IsNil && r[9].IsNil);
        }, iter: 300);
    }

    [Fact]
    public void Binary_formats_match_binaryprimitives_and_round_trip()
    {
        Prop.Cs(Gen.Select(Gen.OneOf(Gen.Double[-70000, 70000], Gen.Int[-300, 300].Select(x => x + 0.5), Gen.Const(double.NaN), Gen.Const(double.PositiveInfinity), Gen.Const(double.NegativeInfinity)).Array[0, 9], Gen.Bool), t =>
        {
            var (a, big) = t;
            Value[] r = Call($"""
                local big = {(big ? "true" : "false")}
                return a:tobytes('f64', big), a:tobytes('f32', big), a:tobytes('i32', big), a:tobytes('i16', big), a:tobytes('u8'),
                       FloatArray.frombytes(a:tobytes('f64', big), 'f64', big), FloatArray.frombytes(a:tobytes('f32', big), 'f32', big),
                       FloatArray.frombytes(a:tobytes('i32', big), 'i32', big), FloatArray.frombytes(a:tobytes('i16', big), 'i16', big),
                       FloatArray.frombytes(a:tobytes('u8'), 'u8'), a:tobytes() == a:tobytes('f64', false)
                """, Arr(a));
            static double Sat(double d, double lo, double hi) => double.IsNaN(d) ? 0 : Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), lo, hi) + 0.0; // + 0.0: an integer has no negative zero
            byte[] Pack(int size, Action<byte[], int, double> write)
            {
                var bytes = new byte[a.Length * size];
                for (int k = 0; k < a.Length; k++) write(bytes, k * size, a[k]);
                return bytes;
            }
            Assert.Equal(Pack(8, (b, o, d) => { if (big) BinaryPrimitives.WriteDoubleBigEndian(b.AsSpan(o), d); else BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(o), d); }), LuaText.ToBytes(r[0].AsString));
            Assert.Equal(Pack(4, (b, o, d) => { if (big) BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(o), (float)d); else BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), (float)d); }), LuaText.ToBytes(r[1].AsString));
            Assert.Equal(Pack(4, (b, o, d) => { int v = (int)Sat(d, int.MinValue, int.MaxValue); if (big) BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v); }), LuaText.ToBytes(r[2].AsString));
            Assert.Equal(Pack(2, (b, o, d) => { short v = (short)Sat(d, short.MinValue, short.MaxValue); if (big) BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), v); }), LuaText.ToBytes(r[3].AsString));
            Assert.Equal(Pack(1, (b, o, d) => b[o] = (byte)Sat(d, 0, 255)), LuaText.ToBytes(r[4].AsString));
            Same(a, Data(r[5]));
            Same(a.Select(d => (double)(float)d), Data(r[6]));
            Same(a.Select(d => Sat(d, int.MinValue, int.MaxValue)), Data(r[7]));
            Same(a.Select(d => Sat(d, short.MinValue, short.MaxValue)), Data(r[8]));
            Same(a.Select(d => Sat(d, 0, 255)), Data(r[9]));
            Assert.True(r[10].AsBool);
        }, iter: 300);
    }

    [Fact]
    public void Elementwise_functions_match_system_math()
    {
        Prop.Cs(Gen.Double[-50, 50].Array[0, 10], a =>
        {
            Value[] r = Call("return a:abs(), a:sqrt(), a:sin(), a:cos(), a:exp(), a:log(), a:clamp(-3, 4.5), a:map(function(x, i) return x * i end)", Arr(a));
            Same(a.Select(x => Math.Abs(x)), Data(r[0]));
            Same(a.Select(x => Math.Sqrt(x)), Data(r[1]));
            Same(a.Select(x => Math.Sin(x)), Data(r[2]));
            Same(a.Select(x => Math.Cos(x)), Data(r[3]));
            Same(a.Select(x => Math.Exp(x)), Data(r[4]));
            Same(a.Select(x => Math.Log(x)), Data(r[5]));
            Same(a.Select(x => Math.Clamp(x, -3, 4.5)), Data(r[6]));
            Same(a.Select((x, i) => x * (i + 1)), Data(r[7]));
        }, iter: 200);
    }

    [Theory]
    [InlineData("FloatArray.new(2)[3] = 1", "outside the FloatArray (length 2)")]
    [InlineData("FloatArray.new(2)[0] = 1", "outside the FloatArray (length 2)")]
    [InlineData("FloatArray.new(2)[1] = 'x'", "a FloatArray holds numbers, not a string")]
    [InlineData("FloatArray.new(2)[1] = '5'", "a FloatArray holds numbers, not a string")]
    [InlineData("FloatArray.new(2).name = 1", "attempt to set field 'name' of a FloatArray")]
    [InlineData("return FloatArray.new(2) + FloatArray.new(3)", "different lengths (2 and 3)")]
    [InlineData("return FloatArray.new(2) + {}", "arithmetic on a FloatArray and a table value")]
    [InlineData("return 'x' * FloatArray.new(2)", "arithmetic on a FloatArray and a string value")]
    [InlineData("return FloatArray.new(2):dot(FloatArray.new(1))", "different lengths (2 and 1)")]
    [InlineData("return FloatArray.new(2):addInPlace(FloatArray.new(1))", "different lengths (2 and 1)")]
    [InlineData("return FloatArray.new(0):mean()", "array is empty")]
    [InlineData("return FloatArray.new(0):min()", "array is empty")]
    [InlineData("return FloatArray.new(0):rms()", "array is empty")]
    [InlineData("return FloatArray.new(-1)", "length must not be negative")]
    [InlineData("return FloatArray.new(1 << 28)", "array too large")]
    [InlineData("return FloatArray.from({1, 'x'})", "element 2 is a string, not a number")]
    [InlineData("return FloatArray.from({1, '2'})", "element 2 is a string, not a number")]
    [InlineData("return FloatArray.frombytes('abc', 'f32')", "not a multiple of 4 bytes")]
    [InlineData("return FloatArray.new(1):tobytes('i8')", "format must be")]
    [InlineData("return FloatArray.new(1):clamp(2, 1)", "upper bound is below the lower bound")]
    [InlineData("return FloatArray.new(1):map(function() return 'x' end)", "returned a string for element 1")]
    [InlineData("return FloatArray.new(1):set(2, 0)", "index 2 is outside the array (length 1)")]
    [InlineData("return FloatArray.sum({})", "FloatArray expected, got table")]
    public void Misuse_is_an_error_that_says_what_was_wrong(string source, string message)
    {
        var e = Assert.Throws<LuaException>(() => Run(source));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Values_and_edges()
    {
        Value[] r = Run("""
            local a = FloatArray.new(3)
            a[1] = 2 a[2] = 2.5 a[3.0] = -0.0
            local e = FloatArray.new(0)
            local one = FloatArray.linspace(5, 9, 1)
            local two = FloatArray.linspace(0.1, 0.7, 2)
            return math.type(a[1]), a[1], 1 / a[3], a['sum'] == FloatArray.sum, a.missing, #e, e:sum(), tostring(e), (e + e) == e, #(e * 2), e:tobytes(),
                   one[1], two[1], two[2], tostring(FloatArray.new(10, 1)), type(a), FloatArray.from(a) == a, rawequal(FloatArray.from(a), a),
                   a ~= FloatArray.new(3), FloatArray.new(1, 0 / 0) == FloatArray.new(1, 0 / 0), #FloatArray.linspace(1, 2, 0), a == 1
            """);
        Assert.Equal("float", r[0].AsString);
        Assert.Equal(2.0, r[1].N);
        Assert.Equal(double.NegativeInfinity, r[2].N);
        Assert.True(r[3].AsBool);
        Assert.True(r[4].IsNil);
        Assert.Equal(0, r[5].I);
        Assert.Equal(0.0, r[6].N);
        Assert.Equal("FloatArray(0){}", r[7].AsString);
        Assert.True(r[8].AsBool);
        Assert.Equal(0, r[9].I);
        Assert.Equal("", r[10].AsString);
        Assert.Equal(5.0, r[11].N);
        Assert.Equal(0.1, r[12].N);
        Assert.Equal(0.7, r[13].N);
        Assert.Equal("FloatArray(10){1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, ...}", r[14].AsString);
        Assert.Equal("userdata", r[15].AsString);
        Assert.True(r[16].AsBool);
        Assert.False(r[17].AsBool);
        Assert.True(r[18].AsBool);
        Assert.False(r[19].AsBool); // nan is not equal to nan, in an array as anywhere
        Assert.Equal(0, r[20].I);
        Assert.False(r[21].AsBool);
    }

    [Fact]
    public void The_array_limit_is_configurable_and_a_worker_gets_its_own_copy()
    {
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
        engine.State.Limits.MaxArrayLength = 100;
        engine.State.Stdout = new StringWriter();
        Assert.True(engine.TryRunString("return #FloatArray.new(100)") is RunCompleted);
        Assert.True(engine.TryRunString("return FloatArray.new(101)") is RuntimeFailed { Message: var m } && m.Contains("array too large"));
        Assert.True(engine.TryRunString("return FloatArray.linspace(0, 1, 101)") is RuntimeFailed);
        Assert.True(engine.TryRunString("return FloatArray.frombytes(string.rep('x', 101), 'u8')") is RuntimeFailed);
        Assert.True(engine.TryRunString("return Future.parallel(function() return FloatArray.new(101) end):get()") is RuntimeFailed);

        Value[] r = Run("""
            local a = FloatArray.from({1, 2, 3})
            local back, same = table.unpack(Future.parallel(function(x) x[1] = 100 return {x, x} end, a):get())
            return a[1], back[1], back == same, rawequal(back, same), rawequal(back, a)
            """);
        Assert.Equal(1.0, r[0].N);
        Assert.Equal(100.0, r[1].N);
        Assert.True(r[2].AsBool && r[3].AsBool);
        Assert.False(r[4].AsBool);
    }
}
