using Hedgehog;
using Hedgehog.Linq;
using Cameo.Jit;
using Cameo.Runtime.Vm;
using Xunit;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>Hedgehog properties for the math built-ins: the MathFn instruction (type-checked
/// code), the global function and the math.* function agree bit for bit with each other and
/// with <see cref="MathBuiltins.Apply"/>.</summary>
public class MathBuiltinProperties
{
    private static readonly Hedgehog.Gen<double> GenX = Gen.Frequency(
        (5, Gen.Double(Range.LinearFromDouble(0, -50, 50))),
        (2, Gen.Double(Range.ExponentialFromDouble(0, -1e300, 1e300))),
        (1, Gen.Item(0.0, -0.0, 1.0, -1.0, 0.5, 2.0, 1e-300, double.PositiveInfinity, double.NegativeInfinity, double.NaN)));

    private static long Bits(Value v) => BitConverter.DoubleToInt64Bits(v.N);

    [Fact]
    public void InstructionGlobalAndLibraryAgree()
    {
        var chk = new LuaHost(typeCheck: true);
        var raw = new LuaHost(typeCheck: false);
        var gen = from fn in Gen.Int32(Range.Constant(0, MathBuiltins.Names.Length - 1))
                  from x in GenX
                  select (fn, x);
        Prop.Hh(gen, p =>
        {
            var (fnIndex, x) = p;
            string name = MathBuiltins.Names[fnIndex];
            long expected = BitConverter.DoubleToInt64Bits(MathBuiltins.Apply((MathFn)fnIndex, x));
            chk.L.SetGlobal("input", Value.FromFloat(x));
            raw.L.SetGlobal("input", Value.FromFloat(x));
            // type-checked: compiled to the MathFn instruction
            Value viaInstruction = chk.Run($"global input: number\nreturn {name}(input)")[0];
            // unchecked: an ordinary call of the global function
            Value viaGlobal = raw.Run($"return {name}(input)")[0];
            Assert.True(viaInstruction.IsFloat && viaGlobal.IsFloat);
            Assert.Equal(expected, Bits(viaInstruction));
            Assert.True(expected == Bits(viaGlobal),
                $"{name} input={x:R} inputBits={BitConverter.DoubleToInt64Bits(x)} expected={expected} actual={Bits(viaGlobal)}");
            // math.name is the same function (log and atan there take an optional 2nd argument)
            Value viaMath = raw.Run($"return math.{name}(input)")[0];
            Assert.Equal(expected, Bits(viaMath));
        }, tests: 150);
    }

    [Fact]
    public void IntegersAreConvertedAndNonNumbersRejectedLikeTheFunction()
    {
        var chk = new LuaHost(typeCheck: true);
        Prop.Hh(Gen.Int64(Range.LinearFromInt64(0, -1000, 1000)), n =>
        {
            Value r = chk.Run($"local n: integer = {n}\nreturn sqrt(n), sin(n)")[0];
            Assert.Equal(BitConverter.DoubleToInt64Bits(Math.Sqrt(n)), Bits(r));
        }, tests: 50);
        string? viaInstruction = chk.ErrorOf(() => chk.Run("return cos({} as number)"));
        Assert.Contains("bad argument #1 to 'cos' (number expected, got table)", viaInstruction);
    }

    [Fact]
    public void IdentitiesHold()
    {
        var chk = new LuaHost(typeCheck: true);
        Prop.Hh(Gen.Double(Range.LinearFromDouble(1, 0.001, 1000)), x =>
        {
            chk.L.SetGlobal("input", Value.FromFloat(x));
            Value[] r = chk.Run("global input: number\nlocal x = input\nreturn sin(x) * sin(x) + cos(x) * cos(x), exp(log(x)) / x, log2(x) - log(x) / log(2), sqrt(x) * sqrt(x) / x, deg(rad(x)) / x");
            foreach (Value v in r.Take(2)) Assert.InRange(v.N, 1 - 1e-12, 1 + 1e-12);
            Assert.InRange(r[2].N, -1e-12, 1e-12);
            Assert.InRange(r[3].N, 1 - 1e-12, 1 + 1e-12);
            Assert.InRange(r[4].N, 1 - 1e-12, 1 + 1e-12);
        }, tests: 100);
    }

    [Fact]
    public void AtanUsesDefaultSecondArgumentAcrossAllExecutionPaths()
    {
        using var engine = new ScriptEngine();
        JitLibrary.Enable(engine);
        engine.RunString("global function angle(x: number): number return atan(x) end");
        Value interpreted = engine.State.Globals.GetStr("angle");
        Assert.True(JitLibrary.TryCompile(interpreted, out Value compiled, out _));
        engine.Options.TypeCheck = false;
        // Seed 1009 minimized this case. Atan and Atan2(x, 1) can differ by a ULP;
        // the intrinsic must preserve Lua's optional-second-argument operation.
        foreach (double x in new[] { BitConverter.Int64BitsToDouble(-4615092424867879579),
                     1.2436258277069439, 0.0, -0.0,
                     double.Epsilon, -double.Epsilon, double.MaxValue, -double.MaxValue,
                     double.PositiveInfinity, double.NegativeInfinity, double.NaN })
        {
            long expected = BitConverter.DoubleToInt64Bits(Math.Atan2(x, 1.0));
            engine.State.Globals.SetStr("input", Value.FromFloat(x));
            Value[] calls = engine.RunString("return atan(input), math.atan(input), atan(input, 1), math.atan(input, 1)");
            foreach (Value actual in calls)
            {
                Assert.True(actual.IsFloat);
                Assert.Equal(expected, Bits(actual));
            }
            Assert.Equal(expected, BitConverter.DoubleToInt64Bits(MathBuiltins.Apply(MathFn.Atan, x)));
            Assert.Equal(expected, Bits(engine.State.Call(interpreted, [Value.FromFloat(x)])[0]));
            Assert.Equal(expected, Bits(engine.State.Call(compiled, [Value.FromFloat(x)])[0]));
        }
    }
}
