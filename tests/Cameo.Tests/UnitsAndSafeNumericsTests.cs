using Cameo;
using CsCheck;
using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;
using Xunit;

namespace Cameo.Tests;

public sealed class UnitsAndSafeNumericsTests
{
    private static Value[] RunHermetic(string source)
    {
        using var engine = CameoEngine.CreateHermetic();
        return engine.RunString(source);
    }

    private static IReadOnlyList<Diagnostic> Check(string source)
    {
        using var engine = new CameoEngine();
        return engine.Check(source);
    }

    // ------------------------------------------------------------------ SharpUnits

    [Fact]
    public void Units_quantities_algebra_conversions_and_zero_PrepareDotCall()
    {
        using var engine = CameoEngine.CreateHermetic();
        const string code = """
            let d = units.qty(1500.0, "m")
            let t = units.qty(30.0, "s")
            let v = d / t
            let dKm = d.in_unit("km")
            let parsed = units.parse("1.5 km")
            let power = units.qty(12.0, "V") * units.qty(2.5, "A")
            return dKm.value, v.si_value, d == parsed, power.in_unit("W").value, isFrozen(d)
            """;

        Proto proto = engine.Compile(code);
        static bool HasPrepareDotCall(Proto p)
        {
            for (int i = 0; i < p.Code.Length; i++)
                if (p.At(i).Op == OpCode.PrepareDotCall) return true;
            foreach (Proto child in p.Protos)
                if (HasPrepareDotCall(child)) return true;
            return false;
        }
        Assert.False(HasPrepareDotCall(proto));

        Value[] res = engine.RunString(code);
        Assert.Equal(1.5, res[0].N, 9);
        Assert.Equal(50.0, res[1].N, 9);
        Assert.True(res[2].AsBool);
        Assert.Equal(30.0, res[3].N, 9);
        Assert.True(res[4].AsBool);
    }

    [Fact]
    public void Units_enforces_compile_time_iso80000_affine_safety()
    {
        Value[] res = RunHermetic("""
            let freezing = units.point(0.0, "°C")
            let boiling = units.point(212.0, "degF")
            let span = boiling - freezing
            let warmer = freezing + units.qty(25.0, "K")
            let f = warmer.to_fahrenheit()
            let pGauge = units.point(2.0, "bar", "gauge")
            let pAbs = pGauge.relative_to("vacuum")
            return span.in_unit("K").value, warmer.to_celsius().value, f.value, warmer.to_kelvin().value, pAbs.value
            """);

        Assert.Equal(100.0, res[0].N, 6);
        Assert.Equal(25.0, res[1].N, 6);
        Assert.Equal(77.0, res[2].N, 6);
        Assert.Equal(298.15, res[3].N, 6);
        Assert.True(res[4].N > 3.0 && res[4].N < 3.02);

        // Adding two affine points MUST be a compile-time error!
        IReadOnlyList<Diagnostic> errs = Check("""
            let a = units.point(20.0, "°C")
            let b = units.point(25.0, "°C")
            let illegal = a + b
            return illegal
            """);
        Assert.NotEmpty(errs);
    }

    [Fact]
    public void Units_series_stats_and_parallel_map_transfer_in_hermetic_mode()
    {
        Value[] res = RunHermetic("""
            let samples = units.series([1000.0, 2000.0, 3000.0, 4000.0], "mV")
            let inV = samples.in_unit("V")
            let velocities = [10.0, 20.0, 30.0, 40.0]
            let energies = velocities.parallelMap((v: number) -> SharpUnits.Quantity => {
                let speed = units.qty(v, "m/s")
                let mass = units.qty(2.0, "kg")
                return mass * speed * speed * 0.5
            })
            return #inV, inV.min().value, inV.max().value, inV.mean().value, inV.peak_to_peak().value,
                   energies[1].in_unit("J").value, energies[4].in_unit("J").value
            """);

        Assert.Equal(4L, res[0].I);
        Assert.Equal(1.0, res[1].N, 9);
        Assert.Equal(4.0, res[2].N, 9);
        Assert.Equal(2.5, res[3].N, 9);
        Assert.Equal(3.0, res[4].N, 9);
        Assert.Equal(100.0, res[5].N, 9);
        Assert.Equal(1600.0, res[6].N, 9);
    }

    [Fact]
    public void Property_units_length_and_temperature_round_trips()
    {
        using var engine = CameoEngine.CreateHermetic();
        engine.RunString("""
            fn roundTrip(meters: number, celsius: number) -> number, number {
                let len = units.qty(meters, "m").in_unit("km").in_unit("mm").in_unit("m")
                let temp = units.point(celsius, "°C").to_fahrenheit().to_kelvin().to_celsius()
                return len.value, temp.value
            }
            global runRT: function(number, number): number, number = roundTrip
            """);
        Value fn = engine.State.GetGlobal("runRT");

        Gen.Int[-10000, 10000].Select(Gen.Int[-200, 500]).Sample((m, c) =>
        {
            Value[] r = engine.State.Call(fn, Value.FromFloat(m), Value.FromFloat(c));
            Assert.Equal(m, r[0].N, 6);
            Assert.Equal(c, r[1].N, 6);
        }, iter: 60, threads: 1);
    }

    // ------------------------------------------------------------------ SharpSafeNumerics

    [Fact]
    public void SafeNumerics_checked_saturating_wrapping_integers_and_simd_batch()
    {
        Value[] res = RunHermetic("""
            let maxI = 9223372036854775807
            let minI = -9223372036854775807 - 1
            let v1, e1 = safenum.intAdd(maxI, 1, "reject")
            let v2, e2 = safenum.intAdd(maxI, 1, "saturate")
            let v3, e3 = safenum.intDiv(minI, -1)
            let v4, e4 = safenum.intDiv(10, 0)
            let v5, e5 = safenum.intInRange(50, 0, 100)
            let v6, e6 = safenum.intInRange(150, 0, 100)

            let batch = safenum.intAddBatch([10, 2147483647, -2147483648], [20, 1, -1])
            return v1 == nil, e1, v2, e2, e3, e4, v5, e5, v6 == nil, e6,
                   json.encode(batch.values), json.encode(batch.errors), batch.vectorWidth >= 0
            """);

        Assert.True(res[0].AsBool);
        Assert.Equal("overflow", res[1].AsString);
        Assert.Equal(long.MaxValue, res[2].I);
        Assert.Equal("none", res[3].AsString);
        Assert.Equal("overflow", res[4].AsString);
        Assert.Equal("divide_by_zero", res[5].AsString);
        Assert.Equal(50L, res[6].I);
        Assert.Equal("none", res[7].AsString);
        Assert.True(res[8].AsBool);
        Assert.Equal("out_of_range", res[9].AsString);
        Assert.Equal("[30,0,0]", res[10].AsString);
        Assert.Equal("[\"none\",\"overflow\",\"underflow\"]", res[11].AsString);
        Assert.True(res[12].AsBool);
    }

    [Fact]
    public void SafeNumerics_fixed64_and_ieee_decimal64_decimal128_and_parallel_transfer()
    {
        Value[] res = RunHermetic("""
            let half, eHalf = safenum.fixedFromRatio(1, 2)
            let quarter, _ = safenum.fixedFromRatio(1, 4)
            let threeQuarters = half + quarter
            let _, eInexactFix = safenum.fixedFromRatio(1, 3, "exact")
            let roundedFix, eRoundFix = safenum.fixedFromRatio(1, 3, "to_even")

            let a = safenum.dec64("0.1")
            let b = safenum.dec64("0.2")
            let c = safenum.dec64("0.3")
            let exactSum = (a + b) == c

            let one = safenum.dec128("1")
            let three = safenum.dec128("3")
            let _, eInexactDec = one.tryDiv(three, "exact")
            let approx, eApprox = one.tryDiv(three, "to_even")

            let prices = ["19.99", "5.50", "0.01"]
            let taxed = prices.parallelMap((p: string) -> safenum.Decimal64 => {
                let d = safenum.dec64(p)
                return d * safenum.dec64("1.10")
            })
            return eHalf, threeQuarters.toNumber(), eInexactFix, eRoundFix,
                   exactSum, eInexactDec, eApprox, tostring(approx),
                   taxed[1] == safenum.dec64("21.989"), taxed[2] == safenum.dec64("6.05"),
                   isFrozen(a), isFrozen(half)
            """);

        Assert.Equal("none", res[0].AsString);
        Assert.Equal(0.75, res[1].N, 12);
        Assert.Equal("inexact", res[2].AsString);
        Assert.Equal("none", res[3].AsString);
        Assert.True(res[4].AsBool);
        Assert.Equal("inexact", res[5].AsString);
        Assert.Equal("none", res[6].AsString);
        Assert.StartsWith("0.3333333333", res[7].AsString);
        Assert.True(res[8].AsBool);
        Assert.True(res[9].AsBool);
        Assert.True(res[10].AsBool);
        Assert.True(res[11].AsBool);
    }

    [Fact]
    public void Property_simd_intAddBatch_matches_checked_int32_reference()
    {
        var edgeInt = Gen.OneOf(Gen.Int[int.MinValue, int.MaxValue], Gen.Const(int.MaxValue), Gen.Const(int.MinValue), Gen.Const(1), Gen.Const(-1), Gen.Const(0));
        Gen.Select(edgeInt, edgeInt).Array[0, 40].Sample(pairs =>
        {
            string ls = "[" + string.Join(", ", pairs.Select(p => p.Item1)) + "]";
            string rs = "[" + string.Join(", ", pairs.Select(p => p.Item2)) + "]";
            Value[] res = RunHermetic($"""
                let r = safenum.intAddBatch({ls}, {rs})
                return json.encode(r.values), json.encode(r.errors)
                """);

            var expVals = new List<int>(pairs.Length);
            var expErrs = new List<string>(pairs.Length);
            foreach (var (a, b) in pairs)
            {
                long sum = (long)a + b;
                if (sum > int.MaxValue) { expVals.Add(0); expErrs.Add("\"overflow\""); }
                else if (sum < int.MinValue) { expVals.Add(0); expErrs.Add("\"underflow\""); }
                else { expVals.Add((int)sum); expErrs.Add("\"none\""); }
            }
            Assert.Equal("[" + string.Join(",", expVals) + "]", res[0].AsString);
            Assert.Equal("[" + string.Join(",", expErrs) + "]", res[1].AsString);
        }, iter: 25);
    }
}
