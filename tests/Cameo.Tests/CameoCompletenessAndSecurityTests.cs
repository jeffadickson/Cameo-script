using System.Net;
using System.Net.Sockets;
using System.Text;
using Cameo;
using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

public class CameoCompletenessAndSecurityTests
{
    // ------------------------------------------------------------------ Pillar A: Security

    [Fact]
    public async Task NetworkPolicy_blocks_redirect_before_connecting_to_forbidden_target()
    {
        // Target server that MUST NEVER receive a request when redirected to.
        int forbiddenHits = 0;
        using var forbiddenListener = new TcpListener(IPAddress.Loopback, 0);
        forbiddenListener.Start();
        int forbiddenPort = ((IPEndPoint)forbiddenListener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task forbiddenTask = Task.Run(async () =>
        {
            try
            {
                using TcpClient client = await forbiddenListener.AcceptTcpClientAsync(cts.Token);
                Interlocked.Increment(ref forbiddenHits);
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        });

        // Allowed loopback server that returns 302 redirecting to the forbidden port.
        using var allowedListener = new TcpListener(IPAddress.Loopback, 0);
        allowedListener.Start();
        int allowedPort = ((IPEndPoint)allowedListener.LocalEndpoint).Port;
        Task allowedTask = Task.Run(async () =>
        {
            using TcpClient client = await allowedListener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream ns = client.GetStream();
            byte[] buf = new byte[4096];
            _ = await ns.ReadAsync(buf, cts.Token);
            string resp = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{forbiddenPort}/secret\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await ns.WriteAsync(Encoding.ASCII.GetBytes(resp), cts.Token);
        });

        // Allow only allowedPort (with blockPrivateAddresses: false so loopback test server is reachable).
        var policy = new NetworkPolicy(
            [new NetworkRule("127.0.0.1", allowHttp: true, port: allowedPort)],
            blockPrivateAddresses: false,
            maxRedirects: 5);

        using var engine = new ScriptEngine(new CameoRuntimeOptions { AllowNetwork = true, NetworkPolicy = policy });
        Value[] r = engine.RunString($@"
            local v, err = aio.httpGet('http://127.0.0.1:{allowedPort}/start'):await()
            return v == nil, err and err.cause and err.cause.kind
        ");
        Assert.True(r[0].IsTruthy);
        Assert.Equal("permission_denied", r[1].ToText());
        await allowedTask;
        await Task.Delay(50);
        cts.Cancel();
        await forbiddenTask;
        Assert.Equal(0, forbiddenHits);
    }

    [Fact]
    public async Task NetworkPolicy_enforces_MaxResponseBytes_on_streaming_body()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task serverTask = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream ns = client.GetStream();
            byte[] buf = new byte[4096];
            _ = await ns.ReadAsync(buf, cts.Token);
            string body = new('A', 4096);
            string resp = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await ns.WriteAsync(Encoding.ASCII.GetBytes(resp), cts.Token);
        });

        var policy = new NetworkPolicy(
            [new NetworkRule("127.0.0.1", allowHttp: true, port: port)],
            blockPrivateAddresses: false,
            maxResponseBytes: 128);

        using var engine = new ScriptEngine(new CameoRuntimeOptions { AllowNetwork = true, NetworkPolicy = policy });
        Value[] r = engine.RunString($@"
            local v, err = aio.httpGet('http://127.0.0.1:{port}/big'):await()
            return v == nil, err and err.cause and err.cause.kind, err and err.cause and err.cause.message
        ");
        Assert.True(r[0].IsTruthy);
        Assert.Equal("permission_denied", r[1].ToText());
        Assert.Contains("128", r[2].ToText());
        await serverTask;
    }

    [Fact]
    public void MaxSteps_aborts_interpreted_compiled_and_sandboxed_loops_deterministically()
    {
        using var engine = new ScriptEngine(new CameoRuntimeOptions { MaxSteps = 500 });
        RunResult r1 = engine.TryRunString("while true do end");
        Assert.Equal(AbortReason.StepLimit, Assert.IsType<RunAborted>(r1.Value).Reason);
        Assert.True(engine.State.StepsExecuted >= 500);

        // Engine remains reusable after StepLimit abort.
        RunResult ok = engine.TryRunString("local s = 0 for i = 1, 10 do s = s + i end return s");
        Assert.Equal(55, Assert.IsType<RunCompleted>(ok.Value).Values[0].I);

        // Compiled IL loops also honor MaxSteps.
        using var jitEngine = new ScriptEngine(new CameoRuntimeOptions { MaxSteps = 200 });
        Cameo.Jit.JitLibrary.Enable(jitEngine);
        RunResult r2 = jitEngine.TryRunString("local function spin() while true do end end  assert(jit.compile(spin))()");
        Assert.Equal(AbortReason.StepLimit, Assert.IsType<RunAborted>(r2.Value).Reason);

        // sandbox.run({ maxSteps = ... }) stops runaway child deterministically.
        using var host = new ScriptEngine();
        Value[] sr = host.RunString("local v, err = sandbox.run('while true do end', { maxSteps = 100 }):await() return v == nil, err and err.message");
        Assert.True(sr[0].IsTruthy);
        Assert.Contains("step limit", sr[1].ToText());
    }

    [Fact]
    public void Property_MaxSteps_deterministically_bounds_loop_iterations()
    {
        using var engine = new ScriptEngine(new CameoRuntimeOptions { MaxSteps = 250 });
        Gen.Int[1, 600].Sample(n =>
        {
            RunResult res = engine.TryRunString($"local s = 0 for i = 1, {n} do s = s + 1 end return s");
            if (n < 240)
            {
                var done = Assert.IsType<RunCompleted>(res.Value);
                Assert.Equal(n, done.Values[0].I);
            }
            else if (n > 260)
            {
                var aborted = Assert.IsType<RunAborted>(res.Value);
                Assert.Equal(AbortReason.StepLimit, aborted.Reason);
            }
        }, iter: 40, threads: 1);
    }

    [Fact]
    public void BytecodeValidator_accepts_compiled_protos_and_rejects_corrupted_ones()
    {
        using var engine = new CameoEngine();
        Proto valid = engine.Compile("fn f(x: integer) -> integer { return x + 1 } return f(41)");
        Assert.Null(BytecodeValidator.Validate(valid));

        // Corrupt jump out of bounds
        var badJump = new Proto { Name = "badJump", MaxStack = 4, Code = [new Instruction(OpCode.Jmp, 0, 50)], Lines = [1] };
        Assert.Contains("jump target", BytecodeValidator.Validate(badJump)!);

        // Corrupt constant index
        var badK = new Proto { Name = "badK", MaxStack = 4, Code = [new Instruction(OpCode.LoadK, 0, 9)], Lines = [1] };
        Assert.Contains("constant index", BytecodeValidator.Validate(badK)!);

        // Corrupt opcode
        var badOp = new Proto { Name = "badOp", MaxStack = 4, Code = [new Instruction((OpCode)250, 0, 0)], Lines = [1] };
        Assert.Contains("unknown opcode", BytecodeValidator.Validate(badOp)!);
    }

    // ------------------------------------------------------------------ Pillar C: Cameo Completeness

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cameo_braced_enum_and_destructuring_work(bool typeCheck)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        Value[] r = engine.RunString("""
            enum Status {
                "ok",
                "warn",
                "fail"
            }
            record Item {
                name: string
                status: Status
                count: integer
            }
            let item: Item = { name = "disk", status = "warn", count = 3 }
            let { name, status = st, count } = item
            let nums = { 10, 20, 30 }
            let [first, _, third] = nums
            var { count = c } = item
            c += 4
            return name, st, count, first, third, c
            """);
        Assert.Equal("disk", r[0].ToText());
        Assert.Equal("warn", r[1].ToText());
        Assert.Equal(3, r[2].I);
        Assert.Equal(10, r[3].I);
        Assert.Equal(30, r[4].I);
        Assert.Equal(7, r[5].I);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cameo_null_coalesce_and_optional_chaining_work(bool typeCheck)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        Value[] r = engine.RunString("""
            record Profile {
                name: string
                tag: string | nil
            }
            var calls = 0
            fn bump() -> string {
                calls += 1
                return "fallback"
            }
            let f: boolean | nil = false
            let n: string | nil = nil
            let s: string | nil = "  hi  "
            let p1: Profile | nil = { name = "Ada", tag = nil }
            let p2: Profile | nil = nil
            let r1 = f ?? true
            let r2 = n ?? bump()
            let r3 = "ok" ?? bump()
            let r4 = p1?.name ?? "anon"
            let r5 = p2?.name ?? "anon"
            let r6 = s?.trim()?.upper() ?? "none"
            let r7 = n?.trim()?.upper() ?? "none"
            let arr: {integer} | nil = { 11, 22 }
            let nilArr: {integer} | nil = nil
            let r8 = arr?.[2] ?? 0
            let r9 = nilArr?.[2] ?? 0
            return r1, r2, r3, calls, r4, r5, r6, r7, r8, r9
            """);
        Assert.False(r[0].IsTruthy);   // false ?? true === false (unlike Lua `or`!)
        Assert.Equal("fallback", r[1].ToText());
        Assert.Equal("ok", r[2].ToText());
        Assert.Equal(1, r[3].I);       // bump() called only once (short-circuited on "ok")
        Assert.Equal("Ada", r[4].ToText());
        Assert.Equal("anon", r[5].ToText());
        Assert.Equal("HI", r[6].ToText());
        Assert.Equal("none", r[7].ToText());
        Assert.Equal(22, r[8].I);
        Assert.Equal(0, r[9].I);
    }

    [Fact]
    public void Property_null_coalesce_matches_csharp_semantics_including_false()
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = false });
        Gen.Select(Gen.Int[0, 3], Gen.Int[-10, 10]).Sample((kind, fallback) =>
        {
            string lhs = kind switch { 0 => "nil", 1 => "false", 2 => "0", _ => "99" };
            Value[] r = engine.RunString($"return ({lhs}) ?? ({fallback})");
            if (kind == 0) Assert.Equal(fallback, r[0].I);
            else if (kind == 1) Assert.True(r[0].IsBoolean && !r[0].IsTruthy);
            else if (kind == 2) Assert.Equal(0, r[0].I);
            else Assert.Equal(99, r[0].I);
        }, iter: 40, threads: 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cameo_regex_literals_and_match_operators_work_alongside_division(bool typeCheck)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        Value[] r = engine.RunString("""
            let rx = /^error:\s*(\d+)/i
            let m1 = "ERROR: 404" =~ rx
            let m2 = "ERROR: 500" =~ /^error:\s*\d+/i
            let m3 = "INFO: ok" !~ /^error/i
            let m4 = "hello/world" =~ /hello\/world/
            let q = 20 / 4 / 2
            return m1, m2, m3, m4, q, rx.test("error: 7")
            """);
        Assert.True(r[0].IsTruthy);
        Assert.True(r[1].IsTruthy);
        Assert.True(r[2].IsTruthy);
        Assert.True(r[3].IsTruthy);
        Assert.Equal(2.5, r[4].AsDouble);
        Assert.True(r[5].IsTruthy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cameo_block_body_arrows_and_table_literal_arrows_coexist(bool typeCheck)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        Value[] r = engine.RunString("""
            let compute = (x: integer) -> integer => {
                let doubled = x * 2
                return doubled + 1
            }
            let sumList = Query.from({1, 2, 3, 4})
                .select((n) => {
                    let sq = n * n
                    return sq
                })
                .sum()
            let makeRec = (v: integer) => { value = v }
            let rec = makeRec(9)
            return compute(20), sumList, rec.value
            """);
        Assert.Equal(41, r[0].I);
        Assert.Equal(30, r[1].AsDouble);
        Assert.Equal(9, r[2].I);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cameo_expression_position_try_and_multi_statement_catch_work(bool typeCheck)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        Value[] r = engine.RunString("""
            enum Kind { "bad", "empty" }
            record Err {
                kind: Kind
                detail: string
            }
            var logged = ""
            fn parse(s: string) -> Result<integer, Err> {
                if s == "" { return err({ kind = "empty", detail = "blank" }) }
                let n = math.tointeger(tonumber(s))
                if n == nil { return err({ kind = "bad", detail = s }) }
                return ok(n)
            }
            fn addBoth(a: string, b: string) -> Result<integer, Err> {
                let sum = (try parse(a)) + (try parse(b))
                return ok(sum)
            }
            fn recoverWithLog(s: string) -> integer {
                return try parse(s)
                    catch empty {
                        logged = logged .. "empty;"
                        0
                    }
                    catch bad(e) {
                        logged = logged .. "bad:" .. e.detail .. ";";
                        -1
                    }
            }
            let good = addBoth("15", "27")
            let failed = addBoth("15", "nope")
            let r1 = recoverWithLog("")
            let r2 = recoverWithLog("xyz")
            let r3 = recoverWithLog("88")
            return good.unwrapOr(0), failed.isErr(), r1, r2, r3, logged
            """);
        Assert.Equal(42, r[0].I);
        Assert.True(r[1].IsTruthy);
        Assert.Equal(0, r[2].I);
        Assert.Equal(-1, r[3].I);
        Assert.Equal(88, r[4].I);
        Assert.Equal("empty;bad:xyz;", r[5].ToText());
    }

    // ------------------------------------------------------------------ Pillar D: Cameo Tooling

    [Fact]
    public void Cameo_dump_repl_and_max_steps_cli_commands_work()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"cameo-dump-{Guid.NewGuid():N}.cameo");
        try
        {
            File.WriteAllText(tmp, "let s = \"  hi  \".trim().upper()\nprint(s)\n");
            var parseDump = CameoCommands.Parse(["dump", tmp]);
            Assert.Null(parseDump.Error);
            var dumpOut = new StringWriter();
            var dumpRes = CameoCommands.Execute(parseDump.Command, dumpOut, TextWriter.Null);
            Assert.Equal(0, dumpRes.ExitCode);
            Assert.NotNull(dumpRes.Text);
            Assert.Contains("Self", dumpRes.Text!);
            Assert.DoesNotContain("PrepareDotCall", dumpRes.Text!);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }

        // REPL piped session: expressions, multiline fn, persistent globals, .exit
        var parseRepl = CameoCommands.Parse(["repl", "--sandboxed"]);
        Assert.Null(parseRepl.Error);
        var replOut = new StringWriter { NewLine = "\n" };
        var replErr = new StringWriter { NewLine = "\n" };
        var replIn = new StringReader("x = 20\nx + 22\nfn double(n) {\n  return n * 2\n}\ndouble(21)\nio == nil\n.exit\n999\n");
        var replRes = CameoCommands.Execute(parseRepl.Command, replOut, replErr, input: replIn);
        Assert.Equal(0, replRes.ExitCode);
        Assert.Equal("", replErr.ToString());
        Assert.Equal("42\n42\ntrue\n", replOut.ToString());

        // --max-steps on cameo -e
        var parseSteps = CameoCommands.Parse(["--max-steps", "100", "-e", "while true { }"]);
        Assert.Null(parseSteps.Error);
        var stepRes = CameoCommands.Execute(parseSteps.Command, TextWriter.Null, TextWriter.Null);
        Assert.Equal(125, stepRes.ExitCode);
        Assert.Contains(stepRes.Diagnostics, d => d.Message.Contains("StepLimit"));
    }
}
