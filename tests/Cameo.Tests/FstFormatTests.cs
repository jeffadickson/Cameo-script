using System.Diagnostics;
using System.Globalization;
using System.Text;
using CsCheck;
using Cameo.Fst;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// The FST writer and reader, checked against each other and against GTKWave's own tools
/// (fst2vcd, vcd2fst) when they are installed.
/// </summary>
public class FstFormatTests
{
    private static string Temp(string ext) => Path.Combine(Path.GetTempPath(), "tealvm-fst-" + Guid.NewGuid().ToString("N") + ext);

    internal static bool HaveTool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, name)));

    internal static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{tool} failed: {error}");
        return output;
    }

    // ---- a model of a waveform: variables and (time, variable, value) events in time order
    internal enum Kind { Bits, Real, Text }
    internal sealed record Variable(string Name, Kind Kind, int Width);
    internal sealed record Event(ulong Time, int Variable, string Value);        // Value: bits, a double as R, or text

    private static void Write(string path, IReadOnlyList<Variable> variables, IEnumerable<Event> events, long blockSize = 8 << 20)
    {
        using var w = new FstWriter(path, -9, "test", "today") { BlockSize = blockSize };
        w.PushScope("top");
        var handles = new int[variables.Count];
        for (int i = 0; i < variables.Count; i++)
        {
            Variable v = variables[i];
            handles[i] = v.Kind switch
            {
                Kind.Real => w.AddReal(v.Name),
                Kind.Text => w.AddString(v.Name),
                _ => w.AddVariable(v.Name, v.Width),
            };
        }
        w.PopScope();
        foreach (Event e in events)
        {
            w.SetTime(e.Time);
            switch (variables[e.Variable].Kind)
            {
                case Kind.Real: w.EmitReal(handles[e.Variable], double.Parse(e.Value, CultureInfo.InvariantCulture)); break;
                case Kind.Text: w.EmitString(handles[e.Variable], Encoding.UTF8.GetBytes(e.Value)); break;
                default: w.Emit(handles[e.Variable], Encoding.ASCII.GetBytes(e.Value)); break;
            }
        }
    }

    // What a reader should see: per variable, its changes with repeats of the same value and
    // all but the last change at one time removed.
    private static List<(ulong Time, string Value)>[] Expected(IReadOnlyList<Variable> variables, IEnumerable<Event> events, bool keepRepeats = false)
    {
        var result = new List<(ulong, string)>[variables.Count];
        for (int i = 0; i < result.Length; i++) result[i] = new();
        foreach (Event e in events)
        {
            Variable v = variables[e.Variable];
            string value = v.Kind == Kind.Bits ? Extend(e.Value.ToLowerInvariant(), v.Width) : e.Value;
            var list = result[e.Variable];
            if (list.Count > 0 && list[^1].Item1 == e.Time) list.RemoveAt(list.Count - 1);   // replaced at the same time
            if (!keepRepeats && list.Count > 0 && list[^1].Item2 == value) continue;
            list.Add((e.Time, value));
        }
        return result;

        static string Extend(string bits, int width) =>
            new string(bits[0] is 'x' or 'z' ? bits[0] : '0', width - bits.Length) + bits;
    }

    private static List<(ulong Time, string Value)>[] ReadBack(string path, IReadOnlyList<Variable> variables)
    {
        using FstReader r = FstReader.Open(path);
        var result = new List<(ulong, string)>[variables.Count];
        var traces = r.Read(Enumerable.Range(1, r.HandleCount));
        for (int i = 0; i < variables.Count; i++)
        {
            FstSignal s = r.Find("top." + variables[i].Name)!;
            FstTrace t = traces[s.Handle];
            result[i] = new();
            for (int k = 0; k < t.Times.Count; k++)
                result[i].Add((t.Times[k], s.IsReal ? t.Reals[k].ToString("R", CultureInfo.InvariantCulture) : Encoding.UTF8.GetString(t.Values[k])));
        }
        return result;
    }

    private static readonly Variable[] Vars =
    [
        new("clk", Kind.Bits, 1), new("data", Kind.Bits, 8), new("wide", Kind.Bits, 70),
        new("vout", Kind.Real, 64), new("state", Kind.Text, 0), new("idle", Kind.Bits, 4),
    ];

    private static List<Event> Sample()
    {
        var e = new List<Event>
        {
            new(0, 0, "0"), new(0, 1, "00000000"), new(0, 2, "x"), new(0, 3, "0"), new(0, 4, "RESET"),
        };
        for (ulong t = 5; t <= 200; t += 5)
        {
            e.Add(new(t, 0, (t / 5 % 2).ToString()));
            if (t % 20 == 0) e.Add(new(t, 1, Convert.ToString((int)(t * 7 % 256), 2)));
            if (t % 50 == 0) e.Add(new(t, 2, t == 100 ? "1z0x" : new string('1', 70)));
            e.Add(new(t, 3, Math.Sin(t / 20.0).ToString("R", CultureInfo.InvariantCulture)));
            if (t == 40) e.Add(new(t, 4, "RUN"));
            if (t == 160) e.Add(new(t, 4, "DONE ok"));
        }
        e.Add(new(200, 0, "z"));
        return e;
    }

    [Fact]
    public void What_is_written_is_read_back()
    {
        string path = Temp(".fst");
        try
        {
            List<Event> events = Sample();
            Write(path, Vars, events);
            using (FstReader r = FstReader.Open(path))
            {
                Assert.Equal(0ul, r.StartTime);
                Assert.Equal(200ul, r.EndTime);
                Assert.Equal(-9, r.TimescaleExponent);
                Assert.Equal("test", r.Version);
                Assert.Equal("today", r.Date);
                Assert.Equal(["top.clk", "top.data", "top.wide", "top.vout", "top.state", "top.idle"], r.Signals.Select(s => s.Name));
                Assert.Equal([1, 8, 70, 64, 0, 4], r.Signals.Select(s => s.Width));
                Assert.Equal("top", Assert.Single(r.Scopes).Name);
            }
            var expected = Expected(Vars, events);
            var actual = ReadBack(path, Vars);
            for (int i = 0; i < Vars.Length; i++) Assert.Equal(expected[i], actual[i]);
            Assert.Empty(actual[5]);                       // never given a value: no changes
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Many_small_blocks_read_the_same_as_one()
    {
        string one = Temp(".fst"), many = Temp(".fst");
        try
        {
            List<Event> events = Sample();
            Write(one, Vars, events);
            Write(many, Vars, events, blockSize: 64);
            Assert.True(new FileInfo(many).Length > new FileInfo(one).Length);
            var a = ReadBack(one, Vars);
            var b = ReadBack(many, Vars);
            for (int i = 0; i < Vars.Length; i++) Assert.Equal(a[i], b[i]);
        }
        finally { File.Delete(one); File.Delete(many); }
    }

    // ---- VCD, to compare with GTKWave's converters

    // Parses the output of fst2vcd: per variable name, (time, value).
    private static Dictionary<string, List<(ulong, string)>> ParseVcd(string vcd)
    {
        var names = new Dictionary<string, string>();          // id code -> name
        var result = new Dictionary<string, List<(ulong, string)>>();
        var scope = new List<string>();
        ulong time = 0;
        bool header = true;
        foreach (string raw in vcd.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (header)
            {
                string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (w[0] == "$scope") scope.Add(w[2]);
                else if (w[0] == "$upscope") scope.RemoveAt(scope.Count - 1);
                else if (w[0] == "$var") { names[w[3]] = string.Join('.', scope) + "." + w[4]; result[names[w[3]]] = new(); }
                else if (w[0] == "$enddefinitions") header = false;
                continue;
            }
            if (line[0] == '#') { time = ulong.Parse(line[1..]); continue; }
            if (line[0] == '$') continue;
            string id, value;
            if (line[0] is 'b' or 'r' or 's')
            {
                int space = line.IndexOf(' ');
                value = line[1..space];
                id = line[(space + 1)..];
                if (line[0] == 'r') value = double.Parse(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                if (line[0] == 's') value = value.Replace("\\x20", " ").Replace("\\040", " ");
            }
            else { value = line[..1]; id = line[1..]; }
            var list = result[names[id]];
            if (list.Count > 0 && list[^1].Item1 == time) list.RemoveAt(list.Count - 1);
            list.Add((time, value));
        }
        return result;
    }

    [Fact]
    public void GTKWave_reads_what_the_writer_writes()
    {
        if (!HaveTool("fst2vcd")) return;                  // GTKWave's tools are not installed
        foreach (long blockSize in new long[] { 8 << 20, 64 })
        {
            string path = Temp(".fst");
            try
            {
                List<Event> events = Sample();
                Write(path, Vars, events, blockSize);
                string vcd = Run("fst2vcd", path);
                Assert.Contains("$timescale", vcd);
                Assert.Contains("1ns", vcd);
                var parsed = ParseVcd(vcd);
                var expected = Expected(Vars, events);
                for (int i = 0; i < Vars.Length; i++)
                {
                    var got = parsed["top." + Vars[i].Name];
                    // fst2vcd prints the value every variable starts with; ours start as x / NaN
                    if (got.Count > 0 && expected[i].Count > 0 && got[0].Item1 == 0 && expected[i][0].Time == 0) { }
                    var want = expected[i];
                    if (Vars[i].Kind == Kind.Bits)
                    {
                        // VCD drops leading zeros/x of vectors; compare after extending again
                        got = got.Select(g => (g.Item1, Vars[i].Width == 1 ? g.Item2 :
                            new string(g.Item2[0] is 'x' or 'z' ? g.Item2[0] : '0', Vars[i].Width - g.Item2.Length) + g.Item2)).ToList();
                    }
                    if (want.Count == 0) continue;         // a variable that never changed: only its initial value is printed
                    if (Vars[i].Kind == Kind.Real)
                    {
                        // fst2vcd prints reals with 16 significant digits
                        Assert.Equal(want.Select(w => w.Time), got.Select(g => g.Item1));
                        for (int k = 0; k < want.Count; k++)
                        {
                            double a = double.Parse(want[k].Value, CultureInfo.InvariantCulture), b = double.Parse(got[k].Item2, CultureInfo.InvariantCulture);
                            Assert.True(Math.Abs(a - b) <= 1e-15 * Math.Max(1, Math.Abs(a)), $"{a} vs {b}");
                        }
                        continue;
                    }
                    Assert.Equal(want, got);
                }
            }
            finally { File.Delete(path); }
        }
    }

    private static string ToVcd(IReadOnlyList<Variable> variables, IEnumerable<Event> events)
    {
        var sb = new StringBuilder("$date today $end\n$version test $end\n$timescale 1ns $end\n$scope module top $end\n");
        for (int i = 0; i < variables.Count; i++)
        {
            Variable v = variables[i];
            string type = v.Kind switch { Kind.Real => "real", Kind.Text => "string", _ => "wire" };
            sb.Append($"$var {type} {(v.Kind == Kind.Bits ? v.Width : v.Kind == Kind.Real ? 64 : 1)} {(char)('!' + i)} {v.Name} $end\n");
        }
        sb.Append("$upscope $end\n$enddefinitions $end\n");
        ulong? time = null;
        foreach (Event e in events)
        {
            if (time != e.Time) { sb.Append('#').Append(e.Time).Append('\n'); time = e.Time; }
            Variable v = variables[e.Variable];
            char id = (char)('!' + e.Variable);
            sb.Append(v.Kind switch
            {
                Kind.Real => $"r{e.Value} {id}\n",
                Kind.Text => $"s{e.Value.Replace(" ", "\\x20")} {id}\n",
                _ => v.Width == 1 ? $"{e.Value}{id}\n" : $"b{e.Value} {id}\n",
            });
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("-4")]      // LZ4 (the default of every tool that writes FST)
    [InlineData("-F")]      // FastLZ
    [InlineData("-Z")]      // zlib
    [InlineData("-c")]      // the whole file compressed again on close
    public void The_reader_reads_what_GTKWave_writes(string option)
    {
        if (!HaveTool("vcd2fst")) return;
        string vcd = Temp(".vcd"), fst = Temp(".fst");
        try
        {
            // enough changes that the chains are worth compressing
            var events = new List<Event> { new(0, 0, "0"), new(0, 1, "0"), new(0, 2, "0"), new(0, 3, "0"), new(0, 4, "S0"), new(0, 5, "0") };
            var rng = new Random(11);
            for (ulong t = 1; t <= 4000; t++)
            {
                events.Add(new(t, 0, (t % 2).ToString()));
                if (t % 3 == 0) events.Add(new(t, 1, Convert.ToString(rng.Next(256), 2).PadLeft(8, '0')));
                if (t % 97 == 0) events.Add(new(t, 2, rng.Next(4) == 0 ? "x" + new string('1', 69) : Convert.ToString(rng.NextInt64(), 2).PadLeft(70, '0')));
                if (t % 5 == 0) events.Add(new(t, 3, (rng.NextDouble() * 10 - 5).ToString("R", CultureInfo.InvariantCulture)));
                if (t % 400 == 0) events.Add(new(t, 4, "S" + t / 400));
            }
            File.WriteAllText(vcd, ToVcd(Vars, events));
            Run("vcd2fst", option, vcd, fst);

            var expected = Expected(Vars, events, keepRepeats: true);   // GTKWave's writer stores a repeated value again
            var actual = ReadBack(fst, Vars);
            for (int i = 0; i < Vars.Length; i++) Assert.Equal(expected[i], actual[i]);
        }
        finally { File.Delete(vcd); File.Delete(fst); }
    }

    [Fact]
    public void Files_that_are_not_FST_are_rejected_not_crashed_on()
    {
        string path = Temp(".fst");
        try
        {
            File.WriteAllBytes(path, "this is not a waveform file at all, just text"u8.ToArray());
            Assert.Throws<FstFormatException>(() => FstReader.Open(path));
            File.WriteAllBytes(path, []);
            Assert.Throws<FstFormatException>(() => FstReader.Open(path));

            // a good file with any single byte changed, or cut short, must read or fail cleanly
            Write(path, Vars, Sample());
            byte[] good = File.ReadAllBytes(path);
            var rng = new Random(5);
            for (int trial = 0; trial < 400; trial++)
            {
                byte[] bad = (byte[])good.Clone();
                if (trial % 4 == 0) bad = bad[..rng.Next(bad.Length)];
                else bad[rng.Next(bad.Length)] ^= (byte)(1 << rng.Next(8));
                File.WriteAllBytes(path, bad);
                try
                {
                    using FstReader r = FstReader.Open(path);
                    r.Read(Enumerable.Range(1, r.HandleCount));
                }
                catch (FstFormatException) { }
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_writer_refuses_misuse()
    {
        string path = Temp(".fst");
        try
        {
            using var w = new FstWriter(path);
            int bit = w.AddVariable("b", 1), real = w.AddReal("r"), text = w.AddString("s");
            Assert.Throws<ArgumentOutOfRangeException>(() => w.AddVariable("zero", 0));
            Assert.Throws<InvalidOperationException>(() => w.PopScope());
            w.SetTime(10);
            Assert.Throws<InvalidOperationException>(() => w.AddVariable("late", 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetTime(9));
            Assert.Throws<ArgumentOutOfRangeException>(() => w.Emit(99, "1"u8));
            Assert.Throws<ArgumentException>(() => w.Emit(bit, "2"u8));
            Assert.Throws<ArgumentException>(() => w.Emit(bit, "10"u8));
            Assert.Throws<ArgumentException>(() => w.Emit(real, "1"u8));
            Assert.Throws<ArgumentException>(() => w.EmitReal(bit, 1.0));
            Assert.Throws<ArgumentException>(() => w.EmitString(bit, "x"u8));
            Assert.Throws<ArgumentException>(() => w.EmitInteger(text, 1));
        }
        finally { File.Delete(path); }
    }

    // Property: any sequence of events written is what the reader returns, for any block size.
    [Fact]
    public void Random_waveforms_round_trip()
    {
        var genEvent = Gen.Select(Gen.Int[0, 3], Gen.Int[0, Vars.Length - 2], Gen.Int[0, 5], Gen.Long, Gen.Double[-1e6, 1e6]);
        Gen.Select(genEvent.Array[0, 60], Gen.Int[1, 400]).Sample(t =>
        {
            var (raw, blockSize) = t;
            ulong time = 0;
            var events = new List<Event>();
            foreach (var (dt, variable, shape, bits, number) in raw)
            {
                time += (ulong)dt;
                Variable v = Vars[variable];
                string value = v.Kind switch
                {
                    Kind.Real => number.ToString("R", CultureInfo.InvariantCulture),
                    Kind.Text => "s" + (bits & 7),
                    _ => shape switch
                    {
                        0 => "x",
                        1 => "z",
                        2 => new string("01xz"[(int)(bits & 3)], v.Width),
                        _ => string.Concat(Enumerable.Range(0, v.Width).Select(i => (char)('0' + (int)((bits >> (i % 64)) & 1)))),
                    },
                };
                events.Add(new(time, variable, value));
            }
            string path = Temp(".fst");
            try
            {
                Write(path, Vars, events, blockSize);
                var expected = Expected(Vars, events);
                var actual = ReadBack(path, Vars);
                for (int i = 0; i < Vars.Length; i++)
                    if (!expected[i].SequenceEqual(actual[i])) return false;
                return true;
            }
            finally { File.Delete(path); }
        }, iter: 300);
    }
}
