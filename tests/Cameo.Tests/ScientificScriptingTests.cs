using Cameo;
using CsCheck;
using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Xunit;

namespace Cameo.Tests;

public class ScientificScriptingTests
{
    [Fact]
    public void FloatArray_Freeze_Transfer_List_Interop_And_Zero_PrepareDotCall()
    {
        using var engine = CameoEngine.CreateHermetic();
        string src = """
            let xs: List<number> = [2.0, 4.0, 6.0, 8.0]
            let fa = FloatArray.from(xs)
            let copyList: List<number> = fa.toList()
            freeze(fa)
            let frozenBefore = isFrozen(fa)
            let okSet = pcall(() => { fa.set(1, 99.0) })
            let okIdx = pcall(() => { fa[1] = 99.0 })
            let okFill = pcall(() => { fa.fill(0.0) })
            let okInPlace = pcall(() => { fa.addInPlace(1.0) })

            let workerScaled: List<FloatArray> = parallel.map(
                [fa, FloatArray.from([1.0, 3.0, 5.0, 7.0])],
                (arr: FloatArray) -> FloatArray => arr * 2.0
            )
            let workerFrozen: List<boolean> = parallel.map(
                [fa],
                (arr: FloatArray) -> boolean => isFrozen(arr)
            )
            return frozenBefore, okSet, okIdx, okFill, okInPlace, copyList.len(), workerScaled[1].sum(), workerScaled[2].sum(), workerFrozen[1]
            """;

        Proto proto = engine.Compile(src, "fa.cameo");
        Assert.DoesNotContain(proto.Code, ins => ins.Op == OpCode.PrepareDotCall);

        Value[] res = engine.RunString(src, "fa.cameo");
        Assert.True(res[0].IsTruthy);
        Assert.False(res[1].IsTruthy);
        Assert.False(res[2].IsTruthy);
        Assert.False(res[3].IsTruthy);
        Assert.False(res[4].IsTruthy);
        Assert.Equal(4L, res[5].I);
        Assert.Equal(40.0, res[6].N, 6);
        Assert.Equal(32.0, res[7].N, 6);
        Assert.True(res[8].IsTruthy);
    }

    [Fact]
    public void FloatArray_Scientific_Methods_Std_Quantile_Cumsum_Diff_Interp()
    {
        using var engine = CameoEngine.CreateHermetic();
        Value[] res = engine.RunString("""
            let a = FloatArray.from([10.0, 2.0, 38.0, 23.0, 38.0, 23.0, 21.0])
            let popStd = a.std()
            let sampStd = a.std(true)
            let med = a.median()
            let q25 = a.quantile(0.25)
            let q75 = a.quantile(0.75)

            let seq = FloatArray.from([1.0, 3.0, 6.0, 10.0])
            let cs = seq.cumsum()
            let df = seq.diff()

            let xp = FloatArray.from([0.0, 1.0, 3.0])
            let fp = FloatArray.from([0.0, 10.0, 20.0])
            let sMid = FloatArray.interp(2.0, xp, fp)
            let vec = FloatArray.interp(FloatArray.from([-1.0, 0.5, 2.0, 5.0]), xp, fp)

            return popStd, sampStd, med, q25, q75,
                   cs[1], cs[4], df[1], df[3],
                   sMid, vec[1], vec[2], vec[3], vec[4]
            """);

        double[] raw = [10, 2, 38, 23, 38, 23, 21];
        double mean = raw.Average();
        double ss = raw.Sum(x => (x - mean) * (x - mean));
        Assert.Equal(Math.Sqrt(ss / raw.Length), res[0].N, 9);
        Assert.Equal(Math.Sqrt(ss / (raw.Length - 1)), res[1].N, 9);
        Assert.Equal(23.0, res[2].N, 9);
        Assert.Equal(15.5, res[3].N, 9);
        Assert.Equal(30.5, res[4].N, 9);
        Assert.Equal(1.0, res[5].N, 9);
        Assert.Equal(20.0, res[6].N, 9);
        Assert.Equal(2.0, res[7].N, 9);
        Assert.Equal(4.0, res[8].N, 9);
        Assert.Equal(15.0, res[9].N, 9);
        Assert.Equal(0.0, res[10].N, 9);
        Assert.Equal(5.0, res[11].N, 9);
        Assert.Equal(15.0, res[12].N, 9);
        Assert.Equal(20.0, res[13].N, 9);
    }

    [Fact]
    public void Math_Predicates_IsClose_And_Hermetic_Seeded_Rng()
    {
        using var engine = CameoEngine.CreateHermetic();
        string src = """
            let nanVal = 0.0 / 0.0
            let posInf = 1.0 / 0.0
            let negInf = -1.0 / 0.0

            let checks = [
                math.isnan(nanVal),
                not math.isnan(1.0),
                math.isinf(posInf),
                math.isinf(negInf),
                not math.isinf(42.0),
                math.isfinite(42.0),
                not math.isfinite(posInf),
                not math.isfinite(nanVal),
                math.isclose(1.0, 1.0 + 1e-10),
                not math.isclose(1.0, 1.01),
                math.isclose(1.0, 1.005, 0.01),
                math.isclose(0.0, 1e-12, 1e-9, 1e-11),
                math.isclose(posInf, posInf),
                not math.isclose(posInf, negInf),
                not math.isclose(nanVal, nanVal)
            ]
            var allOk = true
            for b in checks {
                if not b { allOk = false }
            }

            -- Ambient math.random is absent in Hermetic mode, while explicit-seed math.rng works!
            let noAmbient = (math.random == nil)
            let r1 = math.rng(42)
            let r2 = math.rng(42)
            let snap = r1.clone()
            let a1 = r1.float()
            let a2 = r2.float()
            let a3 = snap.float()
            let i1 = r1.int(10, 20)
            let i2 = r2.int(10, 20)
            let sampleNormals = r1.normals(4000, 10.0, 2.0)

            -- Freeze prevents advancing until cloned; Rng transfers across parallel.map
            let baseRng = math.rng(999)
            freeze(baseRng)
            let okFrozenDraw = pcall(() => { baseRng.float() })
            let workerMeans: List<number> = parallel.map(
                [1, 2, 3],
                (seed: integer) -> number => math.rng(seed).floats(100).mean()
            )
            let workerMeansRepeat: List<number> = parallel.map(
                [1, 2, 3],
                (seed: integer) -> number => math.rng(seed).floats(100).mean()
            )
            return allOk, noAmbient, a1 == a2 and a2 == a3, i1 == i2 and i1 >= 10 and i1 <= 20,
                   sampleNormals.mean(), sampleNormals.std(), okFrozenDraw,
                   workerMeans[1] == workerMeansRepeat[1] and workerMeans[2] == workerMeansRepeat[2] and workerMeans[3] == workerMeansRepeat[3]
            """;

        Proto proto = engine.Compile(src, "rng.cameo");
        Assert.DoesNotContain(proto.Code, ins => ins.Op == OpCode.PrepareDotCall);

        Value[] res = engine.RunString(src, "rng.cameo");
        Assert.True(res[0].IsTruthy);
        Assert.True(res[1].IsTruthy);
        Assert.True(res[2].IsTruthy);
        Assert.True(res[3].IsTruthy);
        Assert.InRange(res[4].N, 9.8, 10.2);
        Assert.InRange(res[5].N, 1.85, 2.15);
        Assert.False(res[6].IsTruthy);
        Assert.True(res[7].IsTruthy);
    }

    [Fact]
    public void Hdf5_And_Fst_Available_In_Standard_CameoEngine_And_Absent_In_Hermetic_Sandboxed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cameo-sci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string h5Path = Path.Combine(dir, "wave.h5").Replace("\\", "/");
            string fstPath = Path.Combine(dir, "sig.fst").Replace("\\", "/");

            using var std = new CameoEngine();
            Value[] res = std.RunString($$"""
                let data = FloatArray.from([1.5, 2.5, 3.5, 4.5])
                let okW = hdf5.write("{{h5Path}}", { signal = data })
                let rawBack, rawShape = hdf5.read("{{h5Path}}", "signal")
                let readBack = rawBack as FloatArray
                let shape = rawShape as {integer}

                let w = assert(fst.create("{{fstPath}}", { timescale = "1ns" }))
                let v = w.var("clk", 1)
                w.time(0)
                w.set(v, "0")
                w.time(10)
                w.set(v, "1")
                w.close()

                let info = assert(fst.info("{{fstPath}}"))
                return okW, readBack.sum(), shape[1], #info.signals
                """);
            Assert.True(res[0].IsTruthy);
            Assert.Equal(12.0, res[1].N, 9);
            Assert.Equal(4L, res[2].I);
            Assert.Equal(1L, res[3].I);

            // Absent in Hermetic and Sandboxed profiles
            using var hermetic = CameoEngine.CreateHermetic(typeCheck: false);
            Value[] hRes = hermetic.RunString("return hdf5 == nil, fst == nil");
            Assert.True(hRes[0].IsTruthy);
            Assert.True(hRes[1].IsTruthy);

            using var sandboxed = new CameoEngine(new CameoRuntimeOptions
            {
                TypeCheck = false,
                Libraries = CameoLibraries.Sandboxed,
                FilePolicy = FilePolicy.DenyAll
            });
            Value[] sRes = sandboxed.RunString("return hdf5 == nil, fst == nil");
            Assert.True(sRes[0].IsTruthy);
            Assert.True(sRes[1].IsTruthy);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CsCheck_FloatArray_Cumsum_Diff_Quantile_And_Rng_Determinism()
    {
        var gen =
            from xs in Gen.Int[-500, 500].Array[2, 20]
            from seed in Gen.Int[-1_000_000, 1_000_000]
            select (xs, seed);

        using var engine = CameoEngine.CreateHermetic();
        gen.Sample(t =>
        {
            string elems = string.Join(", ", t.xs.Select(x => $"{x}.0"));
            Value[] res = engine.RunString($$"""
                let a = FloatArray.from([{{elems}}])
                let cs = a.cumsum()
                let df = a.diff()
                let q0 = a.quantile(0.0)
                let q1 = a.quantile(1.0)
                let r1 = math.rng({{t.seed}})
                let r2 = r1.clone()
                let sameRng = (r1.float() == r2.float()) and (r1.int(-100, 100) == r2.int(-100, 100))
                return cs[#cs], df.sum(), q0, q1, sameRng
                """);

            double expectedSum = t.xs.Sum();
            double expectedDiffSum = t.xs[^1] - t.xs[0];
            Assert.Equal(expectedSum, res[0].N, 9);
            Assert.Equal(expectedDiffSum, res[1].N, 9);
            Assert.Equal(t.xs.Min(), res[2].N, 9);
            Assert.Equal(t.xs.Max(), res[3].N, 9);
            Assert.True(res[4].IsTruthy);
        }, iter: 60, threads: 1);
    }
}
