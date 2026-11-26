using System.Diagnostics;
using CsCheck;
using Cameo.Hdf5;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// The HDF5 add-on: round trips of every stored type, a file written by the reference
/// implementation (h5py), files written here read back by h5py when it is installed, and
/// typed errors.
/// </summary>
public class Hdf5Tests
{
    [Fact]
    public void Relative_external_links_keep_the_opened_files_origin()
    {
        string dir = Directory.CreateTempSubdirectory("tealvm-h5-origin-").FullName;
        const string targetName = "tealvm-origin-target-6f9d6b19.h5";
        string conflict = Path.GetFullPath(targetName);
        bool ownsConflict = false;
        try
        {
            File.Copy(RepoFile("tests", "data", "external-parent.h5"), Path.Combine(dir, "parent.h5"));
            File.Copy(RepoFile("tests", "data", targetName), Path.Combine(dir, targetName));
            // Reserve exclusively: never overwrite an unknown working-directory file.
            using (var output = new FileStream(conflict, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsConflict = true;
                using var input = File.OpenRead(RepoFile("tests", "data", "external-conflict.h5"));
                input.CopyTo(output);
            }
            string parent = Path.Combine(dir, "parent.h5").Replace('\\', '/');
            Value[] values = Run("""
                local path = 'PARENT_'
                local data, shape = hdf5.read(path, 'linked')
                assert(data)
                local info = assert(hdf5.info(path, 'linked'))
                local entries = assert(hdf5.list(path))
                return data[1], shape[1], info.shape[1], info.attributes.origin, entries[1].shape[1]
                """.Replace("PARENT_", parent));
            Assert.Equal(42, values[0].AsDouble);
            Assert.Equal(1, values[1].I);
            Assert.Equal(1, values[2].I);
            Assert.Equal("sibling", values[3].ToText());
            Assert.Equal(1, values[4].I);
        }
        finally
        {
            if (ownsConflict) File.Delete(conflict);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("read", 0)]
    [InlineData("read", 32)]
    [InlineData("info", 0)]
    [InlineData("info", 32)]
    [InlineData("list", 0)]
    [InlineData("list", 32)]
    public void Failed_reads_release_file_handles_before_returning(string operation, int length)
    {
        string dir = Directory.CreateTempSubdirectory("tealvm-h5-handle-").FullName;
        string path = Path.Combine(dir, "invalid.h5");
        try
        {
            File.WriteAllBytes(path, new byte[length]);
            string sourcePath = path.Replace('\\', '/');
            string call = operation == "list" ? $"hdf5.list('{sourcePath}')" : $"hdf5.{operation}('{sourcePath}', 'x')";
            Value[] values = Run("local value,err = " + call + " return value,err");
            Assert.True(values[0].IsNil);
            Assert.True(values[1].IsTable);
            Assert.True(values[1].AsTable.GetStr("kind").IsString);
            // A format failure must retain its diagnostic, not become a later null
            // dereference after a constructor silently swallowed the original error.
            Assert.NotEqual(new NullReferenceException().Message, values[1].AsTable.GetStr("message").ToText());
            // Immediate exclusive reopening challenges deterministic ownership. Do not
            // force a GC: eventual finalization is not successful error-path cleanup.
            using var reopened = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(length, reopened.Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static readonly ScriptEngine Engine = Make();
    private static readonly object Gate = new();

    private static ScriptEngine Make()
    {
        var e = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
        Hdf5Library.Enable(e);
        return e;
    }

    private static Value[] Run(string source)
    {
        lock (Gate) return Engine.RunString(source, "h5");
    }

    private static string RepoFile(params string[] parts)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Cameo.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(new[] { dir ?? "." }.Concat(parts).ToArray());
    }

    [Fact]
    public void Numbers_round_trip_in_every_stored_type()
    {
        string path = Path.GetTempFileName();
        try
        {
            Value fn;
            lock (Gate) fn = Engine.RunString("""
                return function(path, a, type, rows)
                  local shape = rows > 0 and {rows, #a // rows} or nil
                  assert(hdf5.write(path, {['/d/x'] = {data = a, type = type, shape = shape}}))
                  local back, got = hdf5.read(path, '/d/x')
                  return back, table.concat(got, 'x'), assert(hdf5.info(path, '/d/x')).type
                end
                """, "h5")[0];
            var genType = Gen.Int[0, 8].Select(i => new[] { "float64", "float32", "int64", "int32", "int16", "int8", "uint32", "uint16", "uint8" }[i]);
            Prop.Cs(Gen.Select(Gen.Double[-70000, 70000].Array[1, 24], genType, Gen.Int[0, 3]), t =>
            {
                var (data, type, rowsWanted) = t;
                int rows = rowsWanted > 0 && data.Length % rowsWanted == 0 ? rowsWanted : 0;
                Value[] r;
                lock (Gate) r = Engine.State.Call(fn, Value.FromText(path), Value.FromUserdata(new FloatArray(Engine.State, data)), type, rows);
                static double Sat(double d, double lo, double hi) => Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), lo, hi) + 0.0;
                Func<double, double> expected = type switch
                {
                    "float64" => d => d,
                    "float32" => d => (float)d,
                    "int64" => d => Sat(d, long.MinValue, long.MaxValue),
                    "int32" => d => Sat(d, int.MinValue, int.MaxValue),
                    "int16" => d => Sat(d, short.MinValue, short.MaxValue),
                    "int8" => d => Sat(d, sbyte.MinValue, sbyte.MaxValue),
                    "uint32" => d => Sat(d, 0, uint.MaxValue),
                    "uint16" => d => Sat(d, 0, ushort.MaxValue),
                    _ => d => Sat(d, 0, 255),
                };
                Assert.Equal(data.Select(expected), ((FloatArray)r[0].O!).Data);
                Assert.Equal(rows > 0 ? $"{rows}x{data.Length / rows}" : data.Length.ToString(), r[1].AsString);
                Assert.Equal(type, r[2].AsString);
            }, iter: 120);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Strings_scalars_groups_and_attributes_round_trip()
    {
        string path = Path.GetTempFileName().Replace('\\', '/');
        try
        {
            Value[] r = Run("""
                local path = 'PATH_'
                assert(hdf5.write(path, {
                  ['/a/b/deep'] = FloatArray.from({1, 2}),
                  ['/a/labels'] = {'x', 'yé', ''},
                  ['/count'] = 42, ['/ratio'] = 0.5, ['/note'] = 'hello', ['/list'] = {1.5, 2.5},
                  ['/tagged'] = {data = FloatArray.new(3, 7), attributes = {units = 'V', rate = 8000, gain = 1.5, on = true, taps = {1, 2, 3}, names = {'p', 'q'}}},
                }, {created = 'now', version = 3}))
                local paths = {}
                for _, e in ipairs(assert(hdf5.list(path))) do paths[#paths + 1] = e.path .. ':' .. e.kind end
                table.sort(paths)
                local at = assert(hdf5.info(path, '/tagged')).attributes
                local root = assert(hdf5.info(path, '/'))
                return table.concat(paths, ' '), tostring(hdf5.read(path, 'a/b/deep')), table.concat(hdf5.read(path, '/a/labels'), '|'),
                       hdf5.read(path, '/count'), math.type(hdf5.read(path, '/count')), hdf5.read(path, '/ratio'), hdf5.read(path, '/note'), tostring(hdf5.read(path, '/list')),
                       at.units, at.rate, at.gain, at.on, tostring(at.taps), table.concat(at.names, ','), root.kind, root.attributes.created, root.attributes.version
                """.Replace("PATH_", path));
            Assert.Equal("/a/b/deep:dataset /a/b:group /a/labels:dataset /a:group /count:dataset /list:dataset /note:dataset /ratio:dataset /tagged:dataset", r[0].AsString);
            Assert.Equal("FloatArray(2){1.0, 2.0}", r[1].AsString);
            Assert.Equal(LuaText.Encode("x|yé|"), r[2].AsString);
            Assert.Equal(42, r[3].I);
            Assert.Equal("integer", r[4].AsString);
            Assert.Equal(0.5, r[5].N);
            Assert.Equal("hello", r[6].AsString);
            Assert.Equal("FloatArray(2){1.5, 2.5}", r[7].AsString);
            Assert.Equal("V", r[8].AsString);
            Assert.Equal(8000, r[9].I);
            Assert.Equal(1.5, r[10].N);
            Assert.Equal(1, r[11].I); // HDF5 has no boolean: true is stored as 1
            Assert.Equal("FloatArray(3){1.0, 2.0, 3.0}", r[12].AsString);
            Assert.Equal("p,q", r[13].AsString);
            Assert.Equal("group", r[14].AsString);
            Assert.Equal("now", r[15].AsString);
            Assert.Equal(3, r[16].I);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_written_by_the_reference_implementation_is_read_correctly()
    {
        string path = RepoFile("tests", "data", "reference-h5py.h5").Replace('\\', '/');
        Assert.True(File.Exists(path), path);
        Value[] r = Run("""
            local p = 'PATH_'
            local entries = {}
            for _, e in ipairs(assert(hdf5.list(p))) do entries[#entries + 1] = e.path .. ':' .. tostring(e.type) .. ':' .. (e.shape and table.concat(e.shape, 'x') or '-') end
            local f32, shape = hdf5.read(p, 'f32')
            local chunked = hdf5.read(p, 'chunked')
            local meas = assert(hdf5.info(p, 'meas'))
            return table.concat(entries, ' '), tostring(f32), table.concat(shape, 'x'), tostring(hdf5.read(p, 'u16')), tostring(hdf5.read(p, 'i8')),
                   hdf5.read(p, 'scalar'), #chunked, chunked[1], chunked[1000], math.abs(chunked:sum() - 500) < 1e-9,
                   meas.attributes.operator, meas.attributes.n, table.concat(hdf5.read(p, 'meas/names'), ','), table.concat(hdf5.read(p, 'meas/vl'), ',')
            """.Replace("PATH_", path));
        Assert.Equal("/chunked:float64:1000 /f32:float32:2x3 /i8:int8:2 /meas:nil:- /meas/names:string:2 /meas/vl:string:2 /scalar:float64: /u16:uint16:3", r[0].AsString);
        Assert.Equal("FloatArray(6){0.0, 1.0, 2.0, 3.0, 4.0, 5.0}", r[1].AsString);
        Assert.Equal("2x3", r[2].AsString);
        Assert.Equal("FloatArray(3){0.0, 1.0, 65535.0}", r[3].AsString);
        Assert.Equal("FloatArray(2){-128.0, 127.0}", r[4].AsString);
        Assert.Equal(3.25, r[5].N);
        Assert.Equal(1000, r[6].I);
        Assert.Equal(0.0, r[7].N);
        Assert.Equal(1.0, r[8].N);
        Assert.True(r[9].AsBool);
        Assert.Equal("ada", r[10].AsString);
        Assert.Equal(5, r[11].I);
        Assert.Equal("x,yy", r[12].AsString);
        Assert.Equal("alpha,beta", r[13].AsString);
    }

    [Fact]
    public void Files_written_here_are_read_by_the_reference_implementation_when_it_is_installed()
    {
        // skipped (passes vacuously) where python3 with h5py is not available
        if (!HasH5py()) return;
        string path = Path.GetTempFileName().Replace('\\', '/');
        try
        {
            Run("""
                assert(hdf5.write('PATH_', {
                  ['/scope/ch1'] = {data = FloatArray.from({0, 0.5, -0.5, 1}), attributes = {units = 'V', rate = 8000}},
                  ['/grid'] = {data = FloatArray.from({1, 2, 3, 4, 5, 6}), shape = {2, 3}, type = 'int16'},
                  ['/labels'] = {'a', 'b'}, ['/count'] = 7,
                }, {version = 2}))
                """.Replace("PATH_", path));
            string script = "import h5py,sys\nf=h5py.File(sys.argv[1],'r')\n" +
                            "print(f['scope/ch1'][...].tolist(), f['scope/ch1'].attrs['units'], int(f['scope/ch1'].attrs['rate']), str(f['grid'].dtype), f['grid'][...].tolist(), [s.decode() for s in f['labels'][...]], int(f['count'][()]), int(f.attrs['version']))";
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(path);
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, error);
            Assert.Equal("[0.0, 0.5, -0.5, 1.0] V 8000 int16 [[1, 2, 3], [4, 5, 6]] ['a', 'b'] 7 2", output.Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool HasH5py()
    {
        try
        {
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import h5py");
            using Process p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    [Fact]
    public void Failures_are_typed_and_misuse_is_reported()
    {
        string dir = Directory.CreateTempSubdirectory("tealvm-h5").FullName.Replace('\\', '/');
        try
        {
            File.WriteAllText(dir + "/not.h5", "this is not an HDF5 file");
            Value[] r = Run("""
                local dir = 'DIR_'
                assert(hdf5.write(dir .. '/ok.h5', {x = FloatArray.new(2)}))
                local function kind(...) local v, e = ... return v == nil and e.kind or 'ok' end
                return kind(hdf5.read(dir .. '/missing.h5', 'x')), kind(hdf5.read(dir .. '/not.h5', 'x')), kind(hdf5.read(dir .. '/ok.h5', 'nope')),
                       kind(hdf5.list(dir .. '/not.h5')), kind(hdf5.info(dir .. '/ok.h5', 'nope')), kind(hdf5.write(dir .. '/no/such/dir/f.h5', {x = 1})),
                       select(2, pcall(hdf5.write, dir .. '/a.h5', {x = {data = FloatArray.new(8), shape = {3, 3}}})),
                       select(2, pcall(hdf5.write, dir .. '/a.h5', {x = {data = FloatArray.new(1), type = 'float128'}})),
                       select(2, pcall(hdf5.write, dir .. '/a.h5', {x = print})),
                       select(2, pcall(hdf5.write, dir .. '/a.h5', {x = {1, 'a'}})),
                       select(2, pcall(hdf5.write, dir .. '/a.h5', {['a'] = 1, ['a/b'] = 2})) ~= nil,
                       select(2, pcall(hdf5.write, dir .. '/a.h5', {[1] = 1}))
                """.Replace("DIR_", dir));
            Assert.Equal(new[] { "not_found", "invalid", "not_found", "invalid", "not_found", "not_found" }, r[..6].Select(v => v.AsString));
            Assert.Contains("shape 3x3 does not hold 8 values", r[6].AsString);
            Assert.Contains("unknown type 'float128'", r[7].AsString);
            Assert.Contains("cannot store a function", r[8].AsString);
            Assert.Contains("only strings or only numbers", r[9].AsString);
            Assert.True(r[10].AsBool);
            Assert.Contains("dataset names must be strings", r[11].AsString);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_library_is_an_add_on_and_type_checks()
    {
        var plain = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
        Assert.True(plain.TryRunString("return hdf5") is RunCompleted { Values: [{ IsNil: true }] });
        string path = Path.GetTempFileName().Replace('\\', '/');
        try
        {
            var typed = new ScriptEngine();
            Hdf5Library.Enable(typed);
            RunResult r = typed.TryRunString("""
                local ok, err = hdf5.write("PATH_", {wave = FloatArray.linspace(0, 1, 5)})
                if err then error(err.message) end
                local data, shape = hdf5.read("PATH_", "wave")
                local wave = data as FloatArray
                local entries, lerr = hdf5.list("PATH_")
                return #wave, wave:sum(), (shape as {integer})[1], entries[1].kind, ok, lerr
                """.Replace("PATH_", path));
            if (r is not RunCompleted done)
            {
                Assert.Fail(r is CompileFailed c ? c.Diagnostics[0].ToString() : r is RuntimeFailed f ? f.Message : "the program did not complete");
                return;
            }
            Value[] v = done.Values;
            Assert.Equal(5, v[0].I);
            Assert.Equal(2.5, v[1].N);
            Assert.Equal(5, v[2].I);
            Assert.Equal("dataset", v[3].AsString);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
