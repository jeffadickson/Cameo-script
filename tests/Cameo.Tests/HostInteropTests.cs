using Cameo;
using CsCheck;
using Cameo.Runtime;
using Cameo.Runtime.Interop;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Xunit;

namespace Cameo.Tests;

public class HostInteropTests
{
    private sealed class Inventory
    {
        private readonly Dictionary<string, int> _stock = new() { ["apple"] = 3, ["pear"] = 7 };

        [Expose]
        public string Owner { get; set; } = "shop";

        [Expose]
        public int Count(string item) => _stock.GetValueOrDefault(item);

        [Expose("add_stock")]
        public int AddStock(string item, int delta = 1)
        {
            _stock[item] = _stock.GetValueOrDefault(item) + delta;
            return _stock[item];
        }

        [Expose(AsFuture = true)]
        public async Task<string> FetchLabelAsync(string item)
        {
            await Task.Delay(25);
            return $"{Owner}:{item}={Count(item)}";
        }

        // Not marked [Expose], so must be hidden in opt-in mode.
        public void Wipe() => _stock.Clear();
    }

    private sealed class OptOutSensor
    {
        public double Calibration { get; } = 1.25;
        public double Read(double raw) => raw * Calibration;
        [Hide] public void Reset() { }
    }

    private sealed class OverloadedHost
    {
        public int Compute(int x) => x;
        public double Compute(double x) => x;
    }

    [Fact]
    public void Register_Typed_Delegate_And_Static_Type_Checking()
    {
        using var engine = new CameoEngine();
        engine.Register("add", (int a, int b) => a + b);
        engine.Register("greet", (string name, string greeting = "hello") => $"{greeting}, {name}");

        // Valid checked Cameo program
        Value[] res = engine.RunString("""
            let s: integer = add(20, 22)
            let g1: string = greet("Ada")
            let g2: string = greet("Grace", "hi")
            return s, g1, g2
            """);
        Assert.Equal(42L, res[0].I);
        Assert.Equal("hello, Ada", res[1].ToText());
        Assert.Equal("hi, Grace", res[2].ToText());

        // Static checker catches argument & return type errors before running
        var diags = engine.Check("""
            fn bad() -> string {
                return add("two", 3)
            }
            """);
        Assert.True(diags.Count >= 2, string.Join("; ", diags));
    }

    [Fact]
    public void Expose_OptIn_OptOut_Live_Properties_And_Compile_Time_Checks()
    {
        var inv = new Inventory();
        var sensor = new OptOutSensor();
        using var engine = new CameoEngine();
        engine.Expose("inventory", inv);
        engine.Expose("sensor", sensor);

        Value[] res = engine.RunString("""
            let before: integer = inventory.count("apple")
            let after: integer = inventory.add_stock("apple", 5)
            let ownerBefore: string = inventory.owner
            inventory.owner = "lab"
            let reading: number = sensor.read(4.0)
            return before, after, ownerBefore, inventory.owner, reading, sensor.calibration
            """);
        Assert.Equal(3L, res[0].I);
        Assert.Equal(8L, res[1].I);
        Assert.Equal("shop", res[2].ToText());
        Assert.Equal("lab", res[3].ToText());
        Assert.Equal("lab", inv.Owner);
        Assert.Equal(5.0, res[4].N, 6);
        Assert.Equal(1.25, res[5].N, 6);

        // Hidden / unexposed members fail at compile time
        Assert.NotEmpty(engine.Check("inventory.wipe()"));
        Assert.NotEmpty(engine.Check("sensor.reset()"));
        Assert.NotEmpty(engine.Check("let x = inventory.count(123)"));

        // Writing to a read-only property fails cleanly at runtime
        var writeRes = engine.TryRunString("sensor.calibration = 2.0");
        Assert.True(writeRes.Value is RuntimeFailed rf && rf.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase));

        // Overloads without [Expose("...")] / [Hide] are rejected cleanly
        Assert.Throws<ArgumentException>(() => engine.Expose("bad", new OverloadedHost()));
    }

    [Fact]
    public void Register_And_Expose_AsFuture_Overlap_Concurrently()
    {
        var inv = new Inventory();
        using var engine = new CameoEngine();
        engine.Expose("inventory", inv);
        engine.Register("fetch", async (string key) =>
        {
            await Task.Delay(30);
            return key.ToUpperInvariant();
        }, asFuture: true);

        Value[] res = engine.RunString("""
            let f1: Future<string> = fetch("alpha")
            let f2: Future<string> = fetch("beta")
            let f3: Future<string> = inventory.fetchLabelAsync("pear")
            return f1.get(), f2.get(), f3.get()
            """);
        Assert.Equal("ALPHA", res[0].ToText());
        Assert.Equal("BETA", res[1].ToText());
        Assert.Equal("shop:pear=7", res[2].ToText());

        // Task-returning delegate without asFuture: true is rejected with helpful error
        Assert.Throws<ArgumentException>(() => engine.Register("badAsync", async () => await Task.FromResult(1)));
        // Non-Task delegate with asFuture: true is rejected
        Assert.Throws<ArgumentException>(() => engine.Register("badSync", () => 1, asFuture: true));
    }

    [Fact]
    public void Register_ThreadSafe_Works_Inside_Hermetic_Parallel_Map()
    {
        using var engine = CameoEngine.CreateHermetic();
        engine.Register("score", (string word) => word.Length * 10, threadSafe: true);

        Value[] res = engine.RunString("""
            let scores: List<integer> = parallel.map(["fig", "apple", "kiwi"], (w: string) -> integer => score(w))
            return json.encode(scores)
            """);
        Assert.Equal("[30,50,40]", res[0].ToText());
    }

    [Fact]
    public void DefineValue_And_Sequence_Dict_FloatArray_Marshalling()
    {
        using var engine = CameoEngine.CreateHermetic();
        engine.DefineValue("weights", new[] { 1.5, 2.5, 3.0 });
        engine.DefineValue("limits", new Dictionary<string, int> { ["max"] = 100, ["min"] = 5 });
        engine.Register("scale", (double[] xs, double factor) => xs.Select(x => x * factor).ToArray());
        engine.Register("sumMap", (IReadOnlyDictionary<string, int> d) => d.Values.Sum());
        engine.Register("faMean", (FloatArray arr) => arr.Data.Average());

        Value[] res = engine.RunString("""
            let scaled: List<number> = scale(weights, 2.0)
            let total: integer = sumMap(limits)
            let fa = FloatArray.from({ 2.0, 4.0, 6.0 })
            let m: number = faMean(fa)
            return scaled[1], scaled[2], scaled[3], total, m
            """);
        Assert.Equal(3.0, res[0].N, 6);
        Assert.Equal(5.0, res[1].N, 6);
        Assert.Equal(6.0, res[2].N, 6);
        Assert.Equal(105L, res[3].I);
        Assert.Equal(4.0, res[4].N, 6);
    }

    [Fact]
    public void Infinite_Enumerable_Stopped_By_MaxSteps_And_Host_Exceptions_Named()
    {
        static IEnumerable<int> Endless()
        {
            int i = 0;
            while (true) yield return i++;
        }

        using var bounded = CameoEngine.CreateHermetic(maxSteps: 500);
        bounded.Register("endless", Endless);
        RunResult aborted = bounded.TryRunString("let xs = endless()");
        Assert.True(aborted.Value is RunAborted { Reason: AbortReason.StepLimit });

        using var engine = new CameoEngine();
        engine.Register("boom", (int x) =>
        {
            if (x < 0) throw new InvalidOperationException("negative input");
            return x;
        });
        RunResult failed = engine.TryRunString("return boom(-1)");
        Assert.True(failed.Value is RuntimeFailed rf &&
            rf.Message.Contains("boom", StringComparison.Ordinal) &&
            rf.Message.Contains("negative input", StringComparison.Ordinal));
    }

    [Fact]
    public void CsCheck_Typed_Interop_Marshalling_RoundTrip()
    {
        var gen =
            from a in Gen.Int[-10_000, 10_000]
            from b in Gen.Int[-10_000, 10_000]
            from xs in Gen.Int[-100, 100].Array[0, 12]
            from tag in Gen.String[Gen.Char.AlphaNumeric, 0, 16]
            select (a, b, xs, tag);

        gen.Sample(t =>
        {
            using var engine = CameoEngine.CreateHermetic();
            engine.DefineValue("hostTag", t.tag);
            engine.DefineValue("hostNums", t.xs);
            engine.Register("combine", (int x, int y, int[] arr, string label) =>
                $"{label}:{x + y}:{arr.Sum()}");

            Value[] res = engine.RunString($"return combine({t.a}, {t.b}, hostNums, hostTag)");
            string expected = $"{t.tag}:{t.a + t.b}:{t.xs.Sum()}";
            Assert.Equal(expected, res[0].ToText());
        }, iter: 50, threads: 1);
    }
}
