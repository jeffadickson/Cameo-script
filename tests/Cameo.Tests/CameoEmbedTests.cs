using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cameo;
using Cameo.Embed;
using CsCheck;
using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Xunit;

namespace Cameo.Tests;

public sealed class CameoEmbedTests
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Cameo.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        throw new InvalidOperationException("Could not locate repository root containing Cameo.slnx");
    }

    private static string? EnsureCppRunner()
    {
        string root = RepoRoot();
        string embedDir = Path.Combine(root, "embed", "cameo_embed");
        string runner = Path.Combine(embedDir, "build", "cameo_embed_runner");
        if (File.Exists(runner))
            return runner;

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return null;

        var psi = new ProcessStartInfo("bash", Path.Combine(embedDir, "run.sh"))
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process? proc = Process.Start(psi);
        if (proc is null) return null;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Failed to build cameo_embed_runner:\n{stdout}\n{stderr}");
        return File.Exists(runner) ? runner : null;
    }

    private static string RunCppVm(byte[] imageBytes, params string[] extraArgs)
    {
        string? runner = EnsureCppRunner();
        Assert.NotNull(runner);

        string tmp = Path.Combine(Path.GetTempPath(), $"cameo_embed_{Guid.NewGuid():N}.cemb");
        try
        {
            File.WriteAllBytes(tmp, imageBytes);
            var psi = new ProcessStartInfo(runner!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(tmp);
            foreach (string arg in extraArgs)
                psi.ArgumentList.Add(arg);

            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(15_000), "cameo_embed_runner timed out");
            if (!string.IsNullOrEmpty(stderr))
                throw new InvalidOperationException($"cameo_embed_runner stderr: {stderr}");
            return stdout.Trim();
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void ProfileValidator_rejects_units_dictionaries_lists_heap_strings_and_default_floats()
    {
        // Physical units excluded
        var rUnits = EmbedCompiler.Compile("""
            export fn bad() -> integer {
                let d = units.qty(10, "m")
                return 0
            }
            """, options: new EmbedCompileOptions { TypeCheck = false });
        Assert.False(rUnits.Success);
        Assert.Contains(rUnits.Diagnostics, d => d.Code == "CAMEOEMB001");

        // Dynamic Dict excluded
        var rDict = EmbedCompiler.Compile("""
            export fn bad() -> integer {
                let d: Dict<string, integer> = Dict.new()
                return d.len()
            }
            """);
        Assert.False(rDict.Success);
        Assert.Contains(rDict.Diagnostics, d => d.Code == "CAMEOEMB001");

        // Dynamic map type {string: integer} excluded
        var rMapType = EmbedCompiler.Compile("""
            export fn bad() -> integer {
                let m: {string: integer} = {}
                return 0
            }
            """);
        Assert.False(rMapType.Success);
        Assert.Contains(rMapType.Diagnostics, d => d.Code == "CAMEOEMB018");

        // Keyed dictionary literal {[k] = v} excluded
        var rKeyed = EmbedCompiler.Compile("""
            export fn bad(k: string) -> integer {
                let m = { [k] = 42 }
                return 0
            }
            """, options: new EmbedCompileOptions { TypeCheck = false });
        Assert.False(rKeyed.Success);
        Assert.Contains(rKeyed.Diagnostics, d => d.Code == "CAMEOEMB018");

        // Dynamic List literal excluded
        var rList = EmbedCompiler.Compile("""
            export fn bad() -> integer {
                let xs: List<integer> = [1, 2, 3]
                return xs.len()
            }
            """);
        Assert.False(rList.Success);
        Assert.Contains(rList.Diagnostics, d => d.Code == "CAMEOEMB001");

        // Dynamic string concat excluded
        var rConcat = EmbedCompiler.Compile("""
            export fn bad(a: string, b: string) -> string {
                return a .. b
            }
            """);
        Assert.False(rConcat.Success);
        Assert.Contains(rConcat.Diagnostics, d => d.Code == "CAMEOEMB002");

        // Integer-only by default rejects float constants and '/'
        var rFloat = EmbedCompiler.Compile("""
            export fn bad(x: number) -> number {
                return x / 2.0
            }
            """);
        Assert.False(rFloat.Success);
        Assert.Contains(rFloat.Diagnostics, d => d.Code == "CAMEOEMB014");

        // Read-only probe mode rejects axi.write32 and p.write32
        var rAxiWr = EmbedCompiler.Compile("""
            export fn bad() -> boolean {
                return axi.write32(0x10000004, 1)
            }
            """, options: new EmbedCompileOptions { AllowMemWrites = false });
        Assert.False(rAxiWr.Success);
        Assert.Contains(rAxiWr.Diagnostics, d => d.Code == "CAMEOEMB019");

        var rPtrWr = EmbedCompiler.Compile("""
            export fn bad() -> boolean {
                let p: Ptr = ptr(0x20000000)
                return p.write32(1)
            }
            """, options: new EmbedCompileOptions { AllowMemWrites = false });
        Assert.False(rPtrWr.Success);
        Assert.Contains(rPtrWr.Diagnostics, d => d.Code == "CAMEOEMB019");

        // BootOnlyClosures rejects allocating closures inside runtime functions
        var rBootOnly = EmbedCompiler.Compile("""
            export fn make(x: integer) -> function(integer): integer {
                return (y: integer) -> integer => x + y
            }
            """, options: new EmbedCompileOptions { BootOnlyClosures = true });
        Assert.False(rBootOnly.Success);
        Assert.Contains(rBootOnly.Diagnostics, d => d.Code == "CAMEOEMB010");
    }

    [Fact]
    public void Flagship_axi_debug_script_matches_between_CameoEngine_and_Cpp20_StaticVm()
    {
        string examplePath = Path.Combine(RepoRoot(), "examples", "17-embed-axi-debug.cameo");
        var options = new EmbedCompileOptions
        {
            AllowFloats = false,
            BootOnlyClosures = true,
        };
        EmbedCompileResult compiled = EmbedCompiler.CompileFile(examplePath, options);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));

        EmbedCompiledImage image = compiled.Image!;
        Assert.True(image.Bytes.Length > EmbedBinaryFormat.HeaderSize);
        Assert.Equal(0, image.Bytes.Length % 8);
        Assert.Equal(image.Report.Crc32C, EmbedBinaryFormat.Crc32C(image.Bytes.AsSpan(16)));
        Assert.True(image.Report.Flags.HasFlag(EmbedImageFlags.HasPointers));
        Assert.True(image.Report.Flags.HasFlag(EmbedImageFlags.HasAxi));
        Assert.True(image.Report.Flags.HasFlag(EmbedImageFlags.HasMemWrite));
        Assert.True(image.Report.Flags.HasFlag(EmbedImageFlags.HasDbg));
        Assert.True(image.Report.Flags.HasFlag(EmbedImageFlags.HasRtosSyscalls));
        Assert.False(image.Report.Flags.HasFlag(EmbedImageFlags.HasFloat));

        // 1. Run in C# CameoEngine + SimulatedEmbedHost
        using var engine = new CameoEngine(new CameoRuntimeOptions
        {
            Libraries = CameoLibraries.Hermetic,
            FilePolicy = FilePolicy.DenyAll,
        });
        var sim = new SimulatedEmbedHost();
        var uart = sim.AddWindow(0x10000000UL, 0x100, EmbedMemoryPerms.Read | EmbedMemoryPerms.Write | EmbedMemoryPerms.Axi, 4, 4);
        var hwsched = sim.AddWindow(0x10010000UL, 0x400, EmbedMemoryPerms.Read | EmbedMemoryPerms.Axi, 4, 4);
        var sram = sim.AddWindow(0x20000000UL, 0x200, EmbedMemoryPerms.Read | EmbedMemoryPerms.Write | EmbedMemoryPerms.Ram, 1, 8);
        uart.SeedU32(8, 0x00000004u);      // TX empty, not full
        hwsched.SeedU32(4, 0x00000003u);   // active_tid=3, seu_fault=0
        sram.SeedU32(0, 0x11223344u);
        sram.SeedU32(4, 0x55667788u);
        sram.SeedU32(8, 0xAABBCCDDu);
        sram.SeedU32(12, 0x13579BDFu);
        sim.AddTask(new SimulatedTaskInfo(State: 1, Priority: 3, StackBytes: 2048, GuardOk: true, Cycles: 12000));
        sim.AddTask(new SimulatedTaskInfo(State: 2, Priority: 2, StackBytes: 1536, GuardOk: true, Cycles: 8000));

        sim.Install(engine.Runtime);
        Value[] mod = engine.RunFile(examplePath);
        Assert.Single(mod);
        Value diagFn = mod[0].AsTable.GetStr("diagnose_bus");
        Value[] csRes = engine.State.Call(diagFn, Value.FromInt(7));
        long expectedChecksum = csRes[0].I;
        long manualXor = 0x11223344L ^ 0x55667788L ^ 0xAABBCCDDL ^ 0x13579BDFL;
        Assert.Equal(manualXor, expectedChecksum);
        Assert.Equal(0x5Au, uart.PeekU32(12));
        Assert.Single(sim.WatchdogCheckins);
        Assert.Single(sim.HexdumpLines);
        Assert.StartsWith("0x20000000: 44 33 22 11 88 77 66 55 DD CC BB AA DF 9B 57 13", sim.HexdumpLines[0]);
        Assert.Single(sim.TraceEvents);

        // 2. Run the exact same compiled CEMB binary in native C++20 StaticVm
        string cppOut = RunCppVm(image.Bytes,
            "--win", "10000000:100:rw:4:4",
            "--win", "10010000:400:ro:4:4",
            "--win", "20000000:200:rw:1:8",
            "--seed32", "10000008:4",
            "--seed32", "10010004:3",
            "--seed32", "20000000:11223344",
            "--seed32", "20000004:55667788",
            "--seed32", "20000008:AABBCCDD",
            "--seed32", "2000000C:13579BDF",
            "--task", "1:3:2048:1:12000",
            "--task", "2:2:1536:1:8000",
            "--call", "diagnose_bus", "7");

        string lastLine = cppOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
        Assert.Equal($"OK int:{expectedChecksum}", lastLine);
        Assert.Contains("DBG uart_stat=0x00000004", cppOut);
        Assert.Contains("DBG 0x20000000: 44 33 22 11 88 77 66 55 DD CC BB AA DF 9B 57 13", cppOut);
        Assert.Contains("LOG active_tid=3", cppOut);
        Assert.Contains("WDT 0", cppOut);
    }

    [Fact]
    public void Cpp20_StaticVm_executes_pointers_axi_burst_records_arrays_dbg_and_policy_faults()
    {
        string src = """
            record RegView {
                base: Ptr
                count: integer
            }

            export fn test_all() -> integer {
                let base: Ptr = ptr(0x20000000)
                let p1: Ptr = base + 4
                let p2: Ptr = p1.align_up(16)
                dbg.assert(p1 < p2, 1)
                dbg.expect_eq(p2.diff(base), 16, 2)
                dbg.assert(p2.write32(0xCAFEBABE), 3)
                let rv: RegView = { base = p2, count = 4 }
                let readback: integer = rv.base.read32()
                dbg.expect_eq(readback, 0xCAFEBABE, 4)

                // Write 2 words via scratchpad burst_write32 and read back via Ptr
                axi.scratch_set32(0, 0x10203040)
                axi.scratch_set32(1, 0x50607080)
                let n, ok = axi.burst_write32(base, 2, 0)
                dbg.assert(ok && n == 2, 5)
                let w0 = base.read32()
                let w1 = (base + 4).read32()

                // Fixed array (no dicts) + bitfield ops
                let arr: {integer} = { dbg.popcount(0xF0F0), dbg.clz32(0x00010000), dbg.ctz32(0x00000080), dbg.bswap32(0x12345678) }
                dbg.expect_eq(arr[1], 8, 6)
                dbg.expect_eq(arr[2], 15, 7)
                dbg.expect_eq(arr[3], 7, 8)
                dbg.expect_eq(arr[4], 0x78563412, 9)

                // Read-only AXI window must deny write cleanly
                let ro_ok = axi.write32(0x10010000, 123)
                dbg.assert(!ro_ok, 10)
                dbg.assert(!axi.window_check(0x10010002, 4, false), 11) // unaligned
                dbg.assert(axi.window_check(0x10010000, 4, false), 12)

                return w0 + w1 + dbg.set_bits(0x1100, 4, 4, 0xA)
            }
            """;

        EmbedCompileResult compiled = EmbedCompiler.Compile(src, "ptr_axi_test.cameo");
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));

        string cppOut = RunCppVm(compiled.Image!.Bytes,
            "--win", "10010000:400:ro:4:4",
            "--win", "20000000:200:rw:1:8",
            "--call", "test_all");

        long expected = 0x10203040L + 0x50607080L + 0x11A0L;
        Assert.Equal($"OK int:{expected}", cppOut);

        // Runtime read-only flag rejects image with HasMemWrite (Status::WriteDenied == 26)
        string roDenied = RunCppVm(compiled.Image!.Bytes, "--ro", "--call", "test_all");
        Assert.Equal("ERR 26", roDenied);

        // Infinite loop must hit StepLimitExceeded (Status::StepLimitExceeded == 7)
        EmbedCompileResult inf = EmbedCompiler.Compile("""
            export fn hang() -> integer {
                while true {
                }
                return 0
            }
            """);
        Assert.True(inf.Success);
        string budgetOut = RunCppVm(inf.Image!.Bytes, "--budget", "200", "--call", "hang");
        Assert.Equal("ERR 7", budgetOut);
    }

    [Fact]
    public void Cameo_embed_CLI_emits_binary_hex_and_freestanding_Cpp20_header()
    {
        string examplePath = Path.Combine(RepoRoot(), "examples", "17-embed-axi-debug.cameo");
        string tmpDir = Path.Combine(Path.GetTempPath(), $"cameo_embed_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string binPath = Path.Combine(tmpDir, "diag.cemb");
            string hdrPath = Path.Combine(tmpDir, "diag.hpp");
            string hexPath = Path.Combine(tmpDir, "diag.hex");
            var parse = CameoCommands.Parse([
                "embed",
                "--boot-only-closures",
                "--symbol", "kAxiDiag",
                "--namespace", "firmware::diag",
                "--out-bin", binPath,
                "--out-header", hdrPath,
                "--out-hex", hexPath,
                examplePath,
            ]);
            Assert.Null(parse.Error);
            Assert.IsType<CameoEmbedCommand>(parse.Command);

            var res = CameoCommands.Execute(parse.Command, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, res.ExitCode);
            Assert.Contains("CEMB v1:", res.Text);
            Assert.True(File.Exists(binPath));
            Assert.True(File.Exists(hdrPath));
            Assert.True(File.Exists(hexPath));

            byte[] bin = File.ReadAllBytes(binPath);
            string hex = File.ReadAllText(hexPath, Encoding.UTF8).Trim();
            Assert.Equal(Convert.ToHexString(bin), hex);

            string header = File.ReadAllText(hdrPath, Encoding.UTF8);
            Assert.Contains("namespace firmware::diag", header);
            Assert.Contains("alignas(8) inline constexpr uint8_t kAxiDiag[", header);
            Assert.Contains("struct kAxiDiag_requirements", header);
            Assert.Contains("static constexpr bool uses_float = false;", header);

            if (OperatingSystem.IsLinux())
            {
                string cppPath = Path.Combine(tmpDir, "check_hdr.cpp");
                string objPath = Path.Combine(tmpDir, "check_hdr.o");
                File.WriteAllText(cppPath, """
                    #include "cameo_embed_vm.hpp"
                    #include "diag.hpp"
                    using Req = firmware::diag::kAxiDiag_requirements;
                    static cameo::embed::StaticVm<
                        Req::stack_slots, Req::frames, Req::globals,
                        Req::boxes, Req::closures, Req::records, Req::record_slots
                    > g_diag_vm;
                    extern "C" int load_diag() noexcept {
                        return static_cast<int>(g_diag_vm.load(firmware::diag::kAxiDiag));
                    }
                    """);
                string includeDir = Path.Combine(RepoRoot(), "embed", "cameo_embed", "include");
                var psi = new ProcessStartInfo("g++")
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (string a in new[]
                {
                    "-std=c++20", "-ffreestanding", "-fno-exceptions", "-fno-rtti",
                    "-Wall", "-Wextra", "-Werror", "-pedantic",
                    "-I", includeDir, "-I", tmpDir, "-c", cppPath, "-o", objPath
                })
                    psi.ArgumentList.Add(a);
                using Process proc = Process.Start(psi)!;
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit(30_000);
                Assert.True(proc.ExitCode == 0, $"g++ failed on generated header:\n{err}");
            }
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void Differential_property_integer_ptr_and_bitfields_match_Cpp20_StaticVm()
    {
        string kernelSrc = """
            export fn eval_ops(a: integer, b: integer, s: integer) -> integer {
                var acc: integer = (a + b) - (a * 3)
                if b != 0 {
                    acc = acc ^ (a ~/ b)
                    acc = acc + (a % b)
                }
                let sh = (s % 70) - 35
                acc = acc ^ (a << sh) ^ (b >> sh)
                acc = acc & (~b | a)
                let p: Ptr = ptr(0x20000000) + (math.abs(a) % 256)
                let aligned: Ptr = p.align_up(8)
                let off: integer = aligned.diff(ptr(0x20000000))
                let lsb: integer = math.abs(s) % 24
                let width: integer = (math.abs(b) % 8) + 1
                let bf: integer = dbg.bits(acc, lsb, width)
                let rebuilt: integer = dbg.set_bits(acc, lsb, width, bf ^ 1)
                let sw: integer = dbg.bswap32(rebuilt)
                return acc + off + bf + sw + dbg.popcount(rebuilt & 0xFFFF)
            }
            """;

        EmbedCompileResult compiled = EmbedCompiler.Compile(kernelSrc, "diff_prop.cameo");
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        byte[] img = compiled.Image!.Bytes;

        using var engine = new CameoEngine(new CameoRuntimeOptions
        {
            Libraries = CameoLibraries.Hermetic,
            FilePolicy = FilePolicy.DenyAll,
        });
        var sim = new SimulatedEmbedHost();
        sim.Install(engine.Runtime);
        Value[] mod = engine.RunString(kernelSrc, "diff_prop.cameo");
        Value fn = mod[0].AsTable.GetStr("eval_ops");

        Gen.Select(Gen.Int[-100000, 100000], Gen.Int[-100000, 100000], Gen.Int[-100, 100])
            .Sample((a, b, s) =>
            {
                long csVal = engine.State.Call(fn, Value.FromInt(a), Value.FromInt(b), Value.FromInt(s))[0].I;
                string cppOut = RunCppVm(img, "--call", "eval_ops",
                    a.ToString(CultureInfo.InvariantCulture),
                    b.ToString(CultureInfo.InvariantCulture),
                    s.ToString(CultureInfo.InvariantCulture));
                return cppOut == $"OK int:{csVal}";
            }, iter: 40, threads: 1);
    }

    [Fact]
    public void Crc32C_property_detects_any_single_bit_corruption_in_CEMB_image()
    {
        EmbedCompileResult compiled = EmbedCompiler.Compile("""
            export fn probe(x: integer) -> integer {
                return dbg.bits(x, 4, 8) + 1
            }
            """);
        Assert.True(compiled.Success);
        byte[] clean = compiled.Image!.Bytes;

        Gen.Select(Gen.Int[16, clean.Length - 1], Gen.Int[0, 7])
            .Sample((byteIdx, bitIdx) =>
            {
                byte[] mutated = (byte[])clean.Clone();
                mutated[byteIdx] ^= (byte)(1 << bitIdx);
                uint crc = EmbedBinaryFormat.Crc32C(mutated.AsSpan(16));
                return crc != compiled.Image.Report.Crc32C;
            }, iter: 100, threads: 1);

        // Verify C++20 StaticVm rejects a 1-bit flip with Status::ChecksumMismatch (3)
        byte[] bad = (byte[])clean.Clone();
        bad[^1] ^= 0x01;
        Assert.Equal("ERR 3", RunCppVm(bad));
    }

    [Fact]
    public void Svd_regmap_overlay_compiles_runs_identically_on_host_and_Cpp20_and_honors_read_only()
    {
        const string svdXml = """
            <device>
              <peripherals>
                <peripheral>
                  <name>AXI_CTRL</name>
                  <baseAddress>0x40000000</baseAddress>
                  <registers>
                    <register>
                      <name>CFG</name>
                      <addressOffset>0x00</addressOffset>
                      <access>read-write</access>
                      <fields>
                        <field><name>ENABLE</name><bitRange>[0:0]</bitRange></field>
                        <field><name>MODE</name><bitRange>[7:4]</bitRange></field>
                      </fields>
                    </register>
                    <register>
                      <name>STATUS</name>
                      <addressOffset>0x04</addressOffset>
                      <access>read-only</access>
                      <fields>
                        <field><name>CODE</name><bitOffset>8</bitOffset><bitWidth>8</bitWidth></field>
                      </fields>
                    </register>
                  </registers>
                </peripheral>
              </peripherals>
            </device>
            """;

        string generated = SvdRegMapGenerator.GenerateFromSvd(svdXml, "test.svd");
        string fullScript = generated + """
            export fn configure_and_read() -> integer {
                let r = axi_ctrl_open(0x40000000)
                let ok1 = axi_ctrl_write_cfg(r, 0x00000031)
                dbg.assert(ok1, 1)
                let ok2 = axi_ctrl_set_cfg_mode(r, 0xA)
                dbg.assert(ok2, 2)
                let mode = axi_ctrl_get_cfg_mode(r)
                let en = axi_ctrl_get_cfg_enable(r)
                let code = axi_ctrl_get_status_code(r)
                return (mode << 16) | (en << 8) | code
            }
            """;

        EmbedCompileResult compiled = EmbedCompiler.Compile(fullScript, "svd_test.cameo");
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));

        // --read-only must reject write32_at / rmw_bits32_at inside generated register overlay
        EmbedCompileResult ro = EmbedCompiler.Compile(fullScript, "svd_test.cameo",
            new EmbedCompileOptions { AllowMemWrites = false });
        Assert.False(ro.Success);
        Assert.Contains(ro.Diagnostics, d => d.Code == "CAMEOEMB019");

        // Run in C# SimulatedEmbedHost
        using var engine = new CameoEngine(new CameoRuntimeOptions
        {
            Libraries = CameoLibraries.Hermetic,
            FilePolicy = FilePolicy.DenyAll,
        });
        var sim = new SimulatedEmbedHost();
        sim.AddWindow(0x40000000UL, 0x100, EmbedMemoryPerms.Read | EmbedMemoryPerms.Write | EmbedMemoryPerms.Axi, minWidth: 4, maxWidth: 4);
        Assert.True(sim.TryWrite(0x40000004UL, 4, 0x00005A00u)); // STATUS.CODE = 0x5A
        sim.Install(engine.Runtime);
        Value[] mod = engine.RunString(fullScript, "svd_test.cameo");
        long csVal = engine.State.Call(mod[0].AsTable.GetStr("configure_and_read"))[0].I;
        Assert.Equal((0xAL << 16) | (1L << 8) | 0x5AL, csVal);

        // Run in C++20 StaticVm (runner maps 0x40000000 RW AXI window with --seed32)
        string cppOut = RunCppVm(compiled.Image!.Bytes, "--win", "40000000:100:rw:4:4",
            "--seed32", "40000004:00005A00", "--call", "configure_and_read");
        Assert.Equal($"OK int:{csVal}", cppOut);

        // Built-in riscue and mps2 board regmaps must also compile cleanly
        foreach (string board in new[] { "riscue", "mps2" })
        {
            string bSrc = SvdRegMapGenerator.GenerateBuiltIn(board) + "\nexport fn ping() -> integer { return 1 }\n";
            var bRes = EmbedCompiler.Compile(bSrc, $"{board}.cameo");
            Assert.True(bRes.Success, $"{board}: " + string.Join("\n", bRes.Diagnostics));
        }
    }

    [Fact]
    public void BusTrace_and_NXTR_dumps_export_to_valid_GTKWave_FST_waveforms()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "cameo_embed_fst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string traceTxt = Path.Combine(tempDir, "bus.txt");
            string busFst = Path.Combine(tempDir, "bus.fst");
            File.WriteAllText(traceTxt, """
                bt seq=0 wr=1 w=4 addr=0x40000000 val=0x00000031 wp=0
                bt seq=1 wr=0 w=4 addr=0x40000004 val=0x00005A00 wp=1
                ok bustrace count=2
                """);

            int count = BusTraceFstExporter.ExportFileToFst(traceTxt, busFst);
            Assert.Equal(2, count);
            using (var reader = new Cameo.Fst.FstReader(busFst))
            {
                Assert.Contains(reader.Signals, s => s.Name.EndsWith("axi_addr", StringComparison.Ordinal));
                Assert.Contains(reader.Signals, s => s.Name.EndsWith("axi_data", StringComparison.Ordinal));
                Assert.Contains(reader.Signals, s => s.Name.EndsWith("watchpoint_hit", StringComparison.Ordinal));
            }

            // Build a valid 32-byte NXTR binary TraceRing dump (16B header + 1x 16B event)
            byte[] nxtr = new byte[32];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(nxtr.AsSpan(0, 4), BusTraceFstExporter.NexrtDumpMagic);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(nxtr.AsSpan(4, 4), 1u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(nxtr.AsSpan(8, 4), 1u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(nxtr.AsSpan(12, 4), 1u);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nxtr.AsSpan(16, 8), 1000UL);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(nxtr.AsSpan(24, 2), 0x0101);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(nxtr.AsSpan(26, 2), 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(nxtr.AsSpan(28, 4), 0x40000004u);

            string nxtrBin = Path.Combine(tempDir, "trace.nxtr");
            string nxtrFst = Path.Combine(tempDir, "trace.fst");
            File.WriteAllBytes(nxtrBin, nxtr);
            Assert.Equal(1, BusTraceFstExporter.ExportFileToFst(nxtrBin, nxtrFst));
            using (var reader = new Cameo.Fst.FstReader(nxtrFst))
            {
                Assert.Contains(reader.Signals, s => s.Name.EndsWith("event_id", StringComparison.Ordinal));
                Assert.Contains(reader.Signals, s => s.Name.EndsWith("event_arg", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class MockMonitorTransport : IEmbedLineTransport
    {
        private readonly Queue<string> _out = new();
        private readonly List<byte> _staged = new();
        public List<string> ReceivedCommands { get; } = new();

        public void SendLine(string line)
        {
            ReceivedCommands.Add(line);
            if (line == "script reset") { _staged.Clear(); _out.Enqueue("ok reset"); }
            else if (line.StartsWith("script hex ", StringComparison.Ordinal))
            {
                byte[] chunk = Convert.FromHexString(line["script hex ".Length..].Trim());
                _staged.AddRange(chunk);
                _out.Enqueue($"ok bytes={_staged.Count}");
            }
            else if (line == "script load")
            {
                uint crc = EmbedBinaryFormat.Crc32C(_staged.ToArray().AsSpan(16));
                _out.Enqueue($"ok loaded crc=0x{crc:X8}");
            }
            else if (line.StartsWith("script save ", StringComparison.Ordinal))
                _out.Enqueue($"ok saved slot={line["script save ".Length..]} bytes={_staged.Count}");
            else if (line.StartsWith("script run ", StringComparison.Ordinal))
                _out.Enqueue("ok ret=42");
            else if (line.StartsWith("script break ", StringComparison.Ordinal))
            {
                string[] p = line.Split(' ');
                _out.Enqueue($"ok bp set proto={p[2]} pc={p[3]}");
            }
            else if (line.StartsWith("script start ", StringComparison.Ordinal))
                _out.Enqueue("ok paused proto=1 pc=0");
            else if (line == "script step")
                _out.Enqueue("ok stepped proto=1 pc=1");
            else if (line == "script regs")
                _out.Enqueue("paused=1 proto=1 pc=1 depth=1 r0=7 r1=35");
            else if (line == "script cont")
                _out.Enqueue("ok ret=42");
            else if (line == "script bustrace")
            {
                _out.Enqueue("bt seq=0 wr=1 w=4 addr=0x20000004 val=0x0000002A wp=1");
                _out.Enqueue("ok bustrace count=1");
            }
            else
                _out.Enqueue("err unknown");
        }

        public string? ReadLine(TimeSpan timeout) => _out.Count > 0 ? _out.Dequeue() : null;
        public void Dispose() { }
    }

    [Fact]
    public void DebugMap_uploader_and_remote_debug_session_round_trip_cleanly()
    {
        string src = """
            export fn diag(x: integer) -> integer {
                let y = x + 35
                return y
            }
            """;
        EmbedCompileResult compiled = EmbedCompiler.Compile(src, "diag.cameo");
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        EmbedCompiledImage img = compiled.Image!;

        // Debug map JSON round-trip & source-line resolution
        string mapJson = img.DebugMap.ToJson();
        EmbedDebugMap parsedMap = EmbedDebugMap.FromJson(mapJson);
        Assert.Equal(img.Report.Crc32C, parsedMap.Crc32C);
        Assert.True(parsedMap.TryFindLocationForLine(2, out int bpProto, out int bpPc));
        Assert.Equal(2, parsedMap.ResolveLine(bpProto, bpPc));

        // Upload + flash save + run + remote debug stepping & bustrace over transport
        using var transport = new MockMonitorTransport();
        EmbedUploadResult up = EmbedUploader.Upload(transport, img, saveSlot: 0, runExport: "diag", runArgs: [7]);
        Assert.True(up.Success, up.Error);
        Assert.Equal(img.Report.Crc32C, up.TargetCrc32C);
        Assert.Equal("ok ret=42", up.RunOutput);

        var dbg = new EmbedRemoteDebugSession(transport, parsedMap);
        Assert.True(dbg.SetBreakpointAtLine(2, out int pId, out int pc));
        Assert.Equal(bpProto, pId);
        Assert.Equal(bpPc, pc);

        var st0 = dbg.Start("diag", 7);
        Assert.Equal(EmbedDebugStopKind.Paused, st0.Kind);
        var st1 = dbg.StepInstruction();
        Assert.Equal(EmbedDebugStopKind.Stepped, st1.Kind);
        var regs = dbg.ReadRegisters();
        Assert.True(regs.IsPaused);
        Assert.Equal("7", regs.Registers[0]);
        Assert.Equal("35", regs.Registers[1]);
        var done = dbg.Continue();
        Assert.Equal(EmbedDebugStopKind.Completed, done.Kind);
        Assert.Equal(42L, done.ReturnValue);

        var bt = dbg.CaptureBusTrace();
        Assert.Single(bt);
        Assert.True(bt[0].WatchpointHit);
        Assert.Equal(0x20000004UL, bt[0].Address);
    }

    [Fact]
    public void CLI_embed_supports_gen_regmap_trace_to_fst_and_out_map()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "cameo_cli_ext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string regFile = Path.Combine(tempDir, "mps2_regs.cameo");
            var parseReg = Cameo.CameoCommands.Parse(["embed", "--gen-regmap", "mps2", "--out-regmap", regFile]);
            Assert.Null(parseReg.Error);
            var resReg = Cameo.CameoCommands.Execute(parseReg.Command!, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, resReg.ExitCode);
            Assert.True(File.Exists(regFile));

            string mapFile = Path.Combine(tempDir, "mps2_regs.map.json");
            var parseMap = Cameo.CameoCommands.Parse(["embed", regFile, "--out-map", mapFile]);
            Assert.Null(parseMap.Error);
            var resMap = Cameo.CameoCommands.Execute(parseMap.Command!, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, resMap.ExitCode);
            Assert.True(File.Exists(mapFile));
            Assert.Contains("CmsdkTimer0Regs", File.ReadAllText(regFile));

            string traceTxt = Path.Combine(tempDir, "bt.txt");
            string outFst = Path.Combine(tempDir, "bt.fst");
            File.WriteAllText(traceTxt, "bt seq=0 wr=0 w=4 addr=0x40000004 val=0x12345678 wp=0\n");
            var parseFst = Cameo.CameoCommands.Parse(["embed", "--trace-to-fst", traceTxt, "--out-fst", outFst]);
            Assert.Null(parseFst.Error);
            var resFst = Cameo.CameoCommands.Execute(parseFst.Command!, TextWriter.Null, TextWriter.Null);
            Assert.Equal(0, resFst.ExitCode);
            Assert.True(File.Exists(outFst));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
