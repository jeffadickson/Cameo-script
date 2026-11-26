using System.Globalization;
using Hedgehog;
using Hedgehog.Linq;
using Cameo.Fst;
using Xunit;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>Hedgehog: waveforms written from Teal with fst.write come back from fst.read.</summary>
public class FstProperties
{
    private static string Program(string source)
    {
        var engine = new ScriptEngine();
        FstLibrary.Enable(engine);
        var output = new StringWriter { NewLine = "\n" };
        engine.State.Stdout = output;
        RunResult r = engine.TryRunString(source);
        return r is RunCompleted ? output.ToString() : r is CompileFailed c ? "compile:" + c.Diagnostics[0] : r is RuntimeFailed f ? "error:" + f.Message : "?";
    }

    [Fact]
    public void A_sampled_trace_and_a_counter_survive_the_file()
    {
        var gen = from samples in Gen.Double(Range.LinearFromDouble(0, -1e6, 1e6)).List(Range.LinearInt32(1, 60))
                  from t0 in Gen.Int64(Range.LinearFromInt64(0, 0, 1000))
                  from dt in Gen.Int64(Range.LinearFromInt64(1, 1, 50))
                  from width in Gen.Int32(Range.LinearInt32(1, 40))
                  from counts in Gen.Int64(Range.LinearFromInt64(0, 0, 1L << 39)).List(Range.LinearInt32(1, 30))
                  select (samples, t0, dt, width, counts);
        Prop.Hh(gen, t =>
        {
            string path = Path.Combine(Path.GetTempPath(), "tealvm-fstprop-" + Guid.NewGuid().ToString("N") + ".fst").Replace('\\', '/');
            try
            {
                string values = string.Join(", ", t.samples.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
                long mask = (1L << t.width) - 1;
                string counts = string.Join(", ", t.counts.Select(c => c & mask));
                string times = string.Join(", ", Enumerable.Range(0, t.counts.Count).Select(i => i * 7));
                string output = Program($$$"""
                    local samples = FloatArray.from({ {{{values}}} })
                    local counts: {integer} = { {{{counts}}} }
                    assert(fst.write("{{{path}}}", { signals = {
                       { name = "a.v", values = samples, t0 = {{{t.t0}}}, dt = {{{t.dt}}} },
                       { name = "a.n", width = {{{t.width}}}, times = { {{{times}}} }, values = counts },
                    } }))
                    local v = fst.read("{{{path}}}", "a.v")
                    -- a sample equal to the one before it is not a change, so replay the changes
                    local at, ok = 1, true
                    for i = 1, #samples do
                       if at < #v.times and v.times[at + 1] == {{{t.t0}}} + (i - 1) * {{{t.dt}}} then at += 1 end
                       if v.reals[at] ~= samples[i] then ok = false end
                    end
                    local n = fst.read("{{{path}}}", "a.n")
                    local at2 = 1
                    for i = 1, #counts do
                       if at2 < #n.times and n.times[at2 + 1] == (i - 1) * 7 then at2 += 1 end
                       if tonumber(n.values[at2], 2) ~= counts[i] or #n.values[at2] ~= {{{t.width}}} then ok = false end
                    end
                    print(ok, v.times[1] == {{{t.t0}}}, n.times[1])
                    """);
                Assert.Equal("true\ttrue\t0\n", output);
            }
            finally { File.Delete(path); }
        });
    }
}
