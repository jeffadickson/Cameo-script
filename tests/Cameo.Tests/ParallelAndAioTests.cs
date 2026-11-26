using System.Diagnostics;
using System.Net;
using System.Text;
using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// Parallel workers (Future.parallel) and asynchronous I/O (aio): value transfer between VMs,
/// isolation, cancellation and the watchdog, permissions, and typed errors.
/// </summary>
public class ParallelAndAioTests
{
    private static (RunResult Result, string Output) Run(string source, CameoRuntimeOptions? options = null, ScriptEngine? engine = null)
    {
        engine ??= new ScriptEngine(options ?? new CameoRuntimeOptions { TypeCheck = false });
        var output = new StringWriter { NewLine = "\n" };
        engine.State.Stdout = output;
        RunResult r = engine.TryRunString(source);
        lock (output) return (r, output.ToString());
    }

    private static string Ok(string source, CameoRuntimeOptions? options = null)
    {
        var (r, output) = Run(source, options);
        if (r is RuntimeFailed f) Assert.Fail(f.Message);
        Assert.True(r is RunCompleted);
        return output;
    }

    // ------------------------------------------------------------------ value transfer

    // A canonical, structure-revealing serialiser written in Lua: equal output means equal value.
    private const string Ser = """
        local function ser(v, seen)
          seen = seen or {}
          local t = math.type(v) or type(v)
          if t == 'table' then
            if seen[v] then return '<' .. seen[v] .. '>' end
            seen.n = (seen.n or 0) + 1
            seen[v] = seen.n
            local keys = {}
            for k in pairs(v) do keys[#keys + 1] = k end
            table.sort(keys, function(a, b)
              local ta, tb = math.type(a) or type(a), math.type(b) or type(b)
              if ta ~= tb then return ta < tb end
              if ta == 'boolean' then return tostring(a) < tostring(b) end
              return a < b
            end)
            local out = {}
            for _, k in ipairs(keys) do out[#out + 1] = ser(k, seen) .. '=' .. ser(v[k], seen) end
            return '{' .. table.concat(out, ',') .. '}'
          elseif t == 'string' then return string.format('%q', v)
          elseif t == 'float' then return string.format('f%a', v)
          else return t .. ':' .. tostring(v) end
        end

        """;

    private static readonly Gen<string> GenScalar = Gen.OneOf(
        Gen.Long.Select(i => i == long.MinValue ? "math.mininteger" : i.ToString()),
        Gen.Double.Select(d => double.IsNaN(d) ? "(0/0)" : double.IsPositiveInfinity(d) ? "math.huge" : double.IsNegativeInfinity(d) ? "-math.huge" : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + (d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ".0" : "")),
        Gen.Bool.Select(b => b ? "true" : "false"),
        Gen.Byte.Array[0, 12].Select(bytes => "\"" + string.Concat(bytes.Select(b => "\\" + b.ToString("D3"))) + "\""));

    private static readonly Gen<string> GenKey = Gen.OneOf(
        Gen.Int[-3, 40].Select(i => $"[{i}]"),
        Gen.Int[0, 30].Select(i => $"k{i}"),
        Gen.Bool.Select(b => b ? "[true]" : "[false]"),
        Gen.Int[1, 9].Select(i => $"[{i}.5]"));

    private static readonly Gen<string> GenValue = Gen.Recursive<string>((depth, self) =>
        depth >= 3 ? GenScalar : Gen.Frequency(
            (3, GenScalar),
            (1, self.Array[0, 5].Select(items => "{" + string.Join(", ", items) + "}")),
            (1, Gen.Select(GenKey, self).Array[0, 5].Select(fields => "{" + string.Join(", ", fields.DistinctBy(f => f.Item1).Select(f => $"{f.Item1} = {f.Item2}")) + "}"))));

    [Fact]
    public void Data_survives_a_round_trip_through_a_worker()
    {
        var host = new LuaHost();
        Prop.Cs(GenValue, literal =>
        {
            Value[] r = host.Run(Ser + $"""
                local v = {literal}
                local back = Future.parallel(function(x) return x end, v):get()
                return ser(v), ser(back), (type(v) ~= 'table') or back ~= v
                """);
            Assert.Equal(r[0].AsString, r[1].AsString);
            Assert.True(r[2].AsBool, "a table must come back as a copy");
        }, iter: 150);
    }

    [Fact]
    public void Shared_upvalues_stay_shared_inside_the_worker_but_not_with_the_parent()
    {
        Assert.Equal("3\t0\n", Ok("""
            local n = 0
            local function inc() n = n + 1 end
            local function get() return n end
            print(Future.parallel(function() inc() inc() inc() return get() end):get(), n)
            """));
    }

    [Fact]
    public void Globals_of_the_parent_are_not_visible_in_a_worker()
    {
        Assert.Equal("nil\tfunction\tnil\n", Ok("""
            answer = 42
            local seen, p = table.unpack(Future.parallel(function() leak = 1 return {answer, type(print)} end):get(), 1, 2)
            print(seen, p, leak)
            """));
    }

    [Fact]
    public void A_replaced_standard_function_is_sent_as_the_replacement()
    {
        Assert.Equal("custom 5\n", Ok("""
            local real = tostring
            tostring = function(v) return 'custom ' .. real(v) end
            print(Future.parallel(function(f) return f(5) end, tostring):get())
            """));
    }

    [Fact]
    public void Deeply_nested_data_is_refused_not_crashed_on()
    {
        string output = Ok("""
            local t = {}
            local head = t
            for i = 1, 300000 do t.next = {} t = t.next end
            local v, err = Future.parallel(function(x) return 1 end, head):await()
            print(v, err.kind, err.message)
            """);
        Assert.Equal("nil\tfailed\tFuture.parallel: argument 1: value is too deeply nested to send to another VM\n", output);
    }

    [Fact]
    public void Workers_can_require_modules()
    {
        string dir = Directory.CreateTempSubdirectory("tealvm-worker").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "shapes.tl"), "local M = {}\nfunction M.area(w: number, h: number): number return w * h end\nreturn M\n");
            File.WriteAllText(Path.Combine(dir, "main.tl"), """
                local shapes = require("shapes")
                local f = Future.parallel(function(w: number, h: number): number
                   return (require("shapes") as any).area(w, h) + shapes.area(1, 1)
                end, 3, 4)
                print(f:get())
                """);
            var engine = new ScriptEngine();
            var output = new StringWriter { NewLine = "\n" };
            engine.State.Stdout = output;
            RunResult r = engine.TryRunFile(Path.Combine(dir, "main.tl"));
            Assert.True(r is RunCompleted, r is RuntimeFailed rf ? rf.Message : "did not complete");
            Assert.Equal("13\n", output.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Worker_output_reaches_the_parents_stdout_intact()
    {
        string output = Ok("""
            local fs = {}
            for i = 1, 8 do
              fs[i] = Future.parallel(function(id) for j = 1, 200 do print('worker ' .. id .. ' line ' .. j) end end, i)
            end
            Future.allOf(fs):get()
            """);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1600, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^worker \d line \d+$", l));
        for (int id = 1; id <= 8; id++) // each worker's own lines stay in order
            Assert.Equal(Enumerable.Range(1, 200), lines.Where(l => l.StartsWith($"worker {id} ")).Select(l => int.Parse(l.Split(' ')[3])));
    }

    [Fact]
    public void Limits_apply_inside_workers()
    {
        Assert.Equal("failed\ttrue\n", Ok("""
            local function deep(n) return 1 + deep(n + 1) end
            local _, err = Future.parallel(deep, 1):await()
            print(err.kind, err.message:find('stack overflow') ~= nil)
            """));
    }

    [Fact]
    public void Exit_in_a_worker_does_not_exit_the_program()
    {
        Assert.Equal("failed\tos.exit was called in a parallel worker\tstill here\n", Ok("""
            local _, err = Future.parallel(function() os.exit(3) end):await()
            print(err.kind, err.message, 'still here')
            """));
    }

    // ------------------------------------------------------------------ stopping

    [Fact]
    public void The_watchdog_stops_workers_and_the_engine_stays_usable()
    {
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false, Timeout = TimeSpan.FromMilliseconds(150) });
        var sw = Stopwatch.StartNew();
        var (r, _) = Run("""
            local fs = {}
            for i = 1, 3 do fs[i] = Future.parallel(function() while true do end end) end
            Future.allOf(fs):get()
            """, engine: engine);
        Assert.True(r is RunAborted { Reason: AbortReason.Timeout });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(SpinWait.SpinUntil(() => engine.State.RunningWorkers == 0, TimeSpan.FromSeconds(10)), "workers kept running after the abort");
        // results of the aborted run's workers are stale and must not disturb the next run
        var (again, output) = Run("print(Future.parallel(function() return 'fresh' end):get())", engine: engine);
        Assert.True(again is RunCompleted);
        Assert.Equal("fresh\n", output);
    }

    [Fact]
    public void The_watchdog_is_not_delayed_by_more_busy_workers_than_processors()
    {
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false, Timeout = TimeSpan.FromMilliseconds(150) });
        engine.State.Limits.MaxParallelWorkers = Environment.ProcessorCount * 4;
        var sw = Stopwatch.StartNew();
        var (r, _) = Run($"""
            local fs = {"{"}{"}"}
            for i = 1, {Environment.ProcessorCount * 6} do fs[i] = Future.parallel(function() while true do end end) end
            Future.allOf(fs):get()
            """, engine: engine);
        Assert.True(r is RunAborted { Reason: AbortReason.Timeout });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"a 150 ms timeout took {sw.ElapsedMilliseconds} ms");
        Assert.True(SpinWait.SpinUntil(() => engine.State.RunningWorkers == 0, TimeSpan.FromSeconds(10)), "workers (running or queued) outlived the abort");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void Many_workers_queue_for_the_available_slots(int slots)
    {
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
        engine.State.Limits.MaxParallelWorkers = slots;
        var (r, output) = Run("""
            local fs = {}
            for i = 1, 500 do fs[i] = Future.parallel(function(n) return n * 2 end, i) end
            local sum = 0
            for _, v in ipairs(Future.allOf(fs):get()) do sum = sum + v end
            print(sum)
            """, engine: engine);
        Assert.True(r is RunCompleted);
        Assert.Equal("250500\n", output);
        Assert.Equal(0, engine.State.RunningWorkers);
    }

    [Fact]
    public void Nested_workers_do_not_deadlock_on_a_single_slot()
    {
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
        engine.State.Limits.MaxParallelWorkers = 1;
        var (r, output) = Run("""
            local function tree(depth)
              if depth == 0 then return 1 end
              local a, b = Future.parallel(tree, depth - 1), Future.parallel(tree, depth - 1)
              return a:get() + b:get()
            end
            print(Future.parallel(tree, 4):get())
            """, engine: engine);
        Assert.True(r is RunCompleted, r is RuntimeFailed f ? f.Message : "did not complete");
        Assert.Equal("16\n", output);
    }

    [Fact]
    public void A_program_waits_for_its_workers_like_for_its_tasks()
    {
        Assert.Equal("main done\nworker done\n", Ok("""
            Future.parallel(function() local x = 0 for i = 1, 2000000 do x = x + i end print('worker done') end)
            print('main done')
            """));
    }

    [Fact]
    public void Cancel_from_another_thread_stops_main_and_workers()
    {
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
        var canceller = Task.Run(async () => { await Task.Delay(100); engine.Cancel(); });
        var (r, _) = Run("Future.parallel(function() while true do end end):get()", engine: engine);
        canceller.Wait();
        Assert.True(r is RunAborted { Reason: AbortReason.Cancelled });
        Assert.True(SpinWait.SpinUntil(() => engine.State.RunningWorkers == 0, TimeSpan.FromSeconds(10)));
    }

    // ------------------------------------------------------------------ aio: files

    [Fact]
    public void Files_round_trip_any_bytes()
    {
        string path = Path.GetTempFileName();
        try
        {
            var host = new LuaHost();
            Value fn = host.Run("return function(path, data) aio.writeFile(path, data):get() return aio.readFile(path):get() end")[0];
            Prop.Cs(Gen.Byte.Array[0, 2000], bytes =>
            {
                string data = LuaText.FromBytes(bytes);
                Value[] r = host.L.Call(fn, Value.FromText(path), Value.FromString(data));
                host.L.Scheduler.RunPendingTasks();
                Assert.Equal(data, r[0].AsString);
                Assert.Equal(bytes, File.ReadAllBytes(path));
            }, iter: 60);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ListDir_lists_names_in_order()
    {
        string dir = Directory.CreateTempSubdirectory("tealvm-aio").FullName;
        try
        {
            foreach (string n in new[] { "b.txt", "a.txt", "ü.txt" }) File.WriteAllText(Path.Combine(dir, n), "");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            Assert.Equal("a.txt b.txt sub ü.txt\n", Ok($"print(table.concat(aio.listDir([[{dir}]]):get(), ' '))"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void An_abort_during_pending_io_leaves_the_engine_usable()
    {
        using var server = new SlowServer(delayMs: 3000);
        // cancelled from outside rather than by a timeout, so that the second run has no deadline
        var engine = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false, AllowNetwork = true });
        var sw = Stopwatch.StartNew();
        new Thread(() => { Thread.Sleep(150); engine.Cancel(); }).Start();
        var (r, _) = Run($"aio.httpGet('{server.Url}'):get()", engine: engine);
        Assert.True(r is RunAborted { Reason: AbortReason.Cancelled });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), "the abort waited for the request");
        var (again, output) = Run("print(aio.readFile('/nonexistent-file'):await())", engine: engine);
        Assert.True(again is RunCompleted);
        Assert.StartsWith("nil\tFutureError(failed)", output);
    }

    // ------------------------------------------------------------------ aio: HTTP

    /// <summary>A loopback HTTP server: echoes method, a header and the body; /missing is 404.</summary>
    private sealed class SlowServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }

        public SlowServer(int delayMs = 0)
        {
            for (int attempt = 0; ; attempt++)
            {
                int port = Random.Shared.Next(20000, 60000);
                Url = $"http://127.0.0.1:{port}/";
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add(Url);
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 20)
                {
                    // A failed Start can dispose the instance on Windows.
                    _listener.Close();
                    _listener = new HttpListener();
                }
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch (Exception) { return; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (delayMs > 0) await Task.Delay(delayMs);
                            using var ms = new MemoryStream();
                            await ctx.Request.InputStream.CopyToAsync(ms);
                            byte[] head = Encoding.ASCII.GetBytes($"{ctx.Request.HttpMethod} {ctx.Request.Url!.AbsolutePath} tag={ctx.Request.Headers["X-Tag"]} type={ctx.Request.ContentType} body=");
                            ctx.Response.StatusCode = ctx.Request.Url.AbsolutePath == "/missing" ? 404 : 200;
                            ctx.Response.Headers["X-Reply"] = "yes";
                            ctx.Response.ContentType = "text/plain";
                            ctx.Response.ContentLength64 = head.Length + ms.Length;
                            await ctx.Response.OutputStream.WriteAsync(head);
                            await ctx.Response.OutputStream.WriteAsync(ms.ToArray());
                            ctx.Response.Close();
                        }
                        catch (Exception) { }
                    });
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

    private static readonly CameoRuntimeOptions Net = new() { TypeCheck = false, AllowNetwork = true };

    [Fact]
    public void Http_get_and_post_with_headers_and_binary_body()
    {
        using var server = new SlowServer();
        string output = Ok($"""
            local r = aio.httpGet('{server.Url}hello', {"{"}['X-Tag'] = 'abc'{"}"}):get()
            print(r.status, r.body, r.headers['x-reply'], r.headers['content-type'])
            local p = aio.httpRequest({"{"}url = '{server.Url}post', method = 'post', body = 'a\0\255z',
              headers = {"{"}['Content-Type'] = 'application/octet-stream', ['X-Tag'] = 7{"}"}{"}"}):get()
            print(p.status, #p.body, p.body:sub(-4) == 'a\0\255z', p.body:sub(1, -5))
            local m = aio.httpGet('{server.Url}missing'):get()
            print(m.status)
            """, Net);
        Assert.Equal(
            "200\tGET /hello tag=abc type= body=\tyes\ttext/plain\n" +
            "200\t56\ttrue\tPOST /post tag=7 type=application/octet-stream body=\n" +
            "404\n", output);
    }

    [Fact]
    public void Http_requests_run_concurrently()
    {
        using var server = new SlowServer(delayMs: 300);
        var sw = Stopwatch.StartNew();
        string output = Ok($"""
            local fs = {"{"}{"}"}
            for i = 1, 6 do fs[i] = aio.httpGet('{server.Url}' .. i) end
            local n = 0
            for _, r in ipairs(Future.allOf(fs):get()) do n = n + r.status end
            print(n)
            """, Net);
        Assert.Equal("1200\n", output);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500), $"six 300 ms requests took {sw.ElapsedMilliseconds} ms: not concurrent");
    }

    [Fact]
    public async Task Http_failures_are_typed()
    {
        using var server = new SlowServer(delayMs: 2000);
        await using var reset = new ResetServer();
        string output = Ok($"""
            local function kinds(f) local v, e = f:await() return tostring(v) .. ' ' .. e.kind .. ' ' .. e.cause.kind end
            print(kinds(aio.httpRequest({"{"}url = '{server.Url}', timeout = 0.1{"}"})))
            print(kinds(aio.httpGet('{reset.Url}')))
            print(kinds(aio.httpGet('not a url')))
            local c = aio.httpGet('{server.Url}')
            c:cancel()
            local v, e = c:await()
            print(v, e.kind, e.cause) -- cancelled by the program: there is no I/O failure behind it
            """, Net);
        Assert.Equal("nil timeout timeout\nnil failed network\nnil failed invalid\nnil\tcancelled\tnil\n", output);
    }

    [Fact]
    public void Network_and_processes_are_denied_by_default_and_in_workers()
    {
        Assert.Equal("permission_denied\tpermission_denied\tpermission_denied\n", Ok("""
            local _, a = aio.httpGet('http://127.0.0.1:1/'):await()
            local _, b = aio.exec('echo'):await()
            local c = Future.parallel(function() local _, e = aio.exec('echo'):await() return e.cause.kind end):get()
            print(a.cause.kind, b.cause.kind, c)
            """));
    }

    // ------------------------------------------------------------------ aio: processes

    private static readonly CameoRuntimeOptions Proc = new() { TypeCheck = false, AllowProcesses = true };

    [Fact]
    public void Exec_collects_output_exit_code_and_feeds_stdin()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        string output = Ok("""
            local e = aio.exec('echo', {'hello', 'a b', 3}):get()
            print(e.code, e.stdout == 'hello a b 3\n', e.stderr == '')
            local c = aio.exec('cat', nil, 'piped\0in'):get()
            print(c.code, c.stdout == 'piped\0in')
            local s = aio.exec('sh', {'-c', 'echo oops >&2; exit 3'}):get()
            print(s.code, s.stderr)
            -- arguments are passed as they are: no shell expands them
            print(aio.exec('echo', {'$HOME; ls'}):get().stdout)
            local _, err = aio.exec('/no/such/program'):await()
            print(err.kind, err.cause.kind)
            """, Proc);
        Assert.Equal("0\ttrue\ttrue\n0\ttrue\n3\toops\n\n$HOME; ls\n\nfailed\tnot_found\n", output);
    }

    [Fact]
    public void Cancelling_exec_kills_the_program()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var sw = Stopwatch.StartNew();
        string output = Ok("""
            local p = aio.exec('sleep', {'30'})
            Future.delay(0.1):get()
            p:cancel()
            print((select(2, p:await())).kind)
            """, Proc);
        Assert.Equal("cancelled\n", output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }
}
