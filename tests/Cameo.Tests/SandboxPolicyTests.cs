using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>
/// Exercises the real sandbox boundary. The finite permission space is exhausted;
/// this is not a claim that the VM confines files or arbitrary host callbacks.
/// </summary>
public class SandboxPolicyTests
{
    private static ScriptEngine Engine(int permissions = 0) => new(new CameoRuntimeOptions
    {
        TypeCheck = false,
        AllowNetwork = (permissions & 1) != 0,
        AllowProcesses = (permissions & 2) != 0,
        AllowDatabase = (permissions & 4) != 0,
        Timeout = TimeSpan.FromSeconds(10),
    });

    private static string Allow(int mask) => "{" + string.Join(",", new[] { "net", "run", "db" }
        .Where((_, bit) => (mask & (1 << bit)) != 0).Select(name => "'" + name + "'")) + "}";

    private static void AssertWorkerReleased(ScriptEngine engine)
    {
        // A completion can be posted just before the worker thread's finally removes
        // it. The public counter has no change event; bound this test-only observation.
        Assert.True(SpinWait.SpinUntil(() => engine.State.RunningWorkers == 0, TimeSpan.FromSeconds(5)),
            "Worker did not leave the active registry after completion");
    }

    [Fact]
    public void All_parent_and_requested_permission_sets_are_enforced_exactly()
    {
        // A test-only native observer reads the actual child state after Sandbox has
        // assigned permissions. No process/network/database operation is needed to
        // observe a granted bit, so this matrix has no external side effects.
        for (int parent = 0; parent < 8; parent++)
        for (int requested = 0; requested < 8; requested++)
        {
            using var engine = Engine(parent);
            int workersCreated = 0;
            var factory = engine.State.WorkerFactory!;
            engine.State.WorkerFactory = () =>
            {
                workersCreated++;
                LuaState child = factory();
                child.SetGlobal("policy_mask", Value.FromFunction((ref CallContext c) => c.Return(
                    (c.L.AllowNetwork ? 1 : 0) | (c.L.AllowProcesses ? 2 : 0) | (c.L.AllowDatabase ? 4 : 0)), "policy_mask"));
                return child;
            };
            Value[] values = engine.RunString($"return sandbox.run('return policy_mask()', {{allow={Allow(requested)}}}):await()");
            bool accepted = (requested & ~parent) == 0;
            if (accepted)
            {
                Assert.Equal(requested, values[0].I);
                Assert.True(values[1].IsNil);
                Assert.Equal(1, workersCreated);
            }
            else
            {
                Assert.True(values[0].IsNil);
                Table error = Assert.IsType<Table>(values[1].O);
                Assert.Equal("failed", error.GetStr("kind").ToText());
                Assert.Contains("cannot grant", error.GetStr("message").ToText());
                Assert.Equal(0, workersCreated); // Refuse before worker creation/compilation.
            }
            AssertWorkerReleased(engine);
            Assert.Equal(42, engine.RunString("return 42")[0].I);
        }
    }

    [Fact]
    public void A_nested_sandbox_cannot_recover_rights_its_parent_dropped()
    {
        using var engine = Engine(7);
        for (int requested = 1; requested < 8; requested++)
        {
            string child = $"local v,e = sandbox.run('return 99', {{allow={Allow(requested)}}}):await() return e.kind";
            Value[] values = engine.RunString($"return sandbox.run([=[{child}]=]):await()");
            Assert.Equal("failed", values[0].ToText());
            Assert.True(values[1].IsNil);
        }
        Assert.Equal(42, engine.RunString("return sandbox.run(\"return sandbox.run('return 42'):get()\"):get()")[0].I);
    }

    [Theory]
    [InlineData(false, "aio.exec('tealvm-policy-no-such-program')")]
    [InlineData(false, "aio.spawn('tealvm-policy-no-such-program')")]
    [InlineData(false, "aio.spawn('tealvm-policy-no-such-program', {}, {pty=true})")]
    [InlineData(true, "aio.exec('tealvm-policy-no-such-program')")]
    [InlineData(true, "aio.spawn('tealvm-policy-no-such-program')")]
    [InlineData(true, "aio.spawn('tealvm-policy-no-such-program', {}, {pty=true})")]
    public void Process_entry_points_deny_before_trying_to_start_a_program(bool sandbox, string call)
    {
        // PTY is a spawn option; expect operates on an already acquired stream/process.
        // An absent program distinguishes policy refusal from a failed process start.
        using var engine = Engine(sandbox ? 2 : 0);
        string body = $"local v,e = {call}:await() return v, e.cause.kind";
        Value[] values = engine.RunString(sandbox
            ? $"return sandbox.run([=[{body.Replace("return v, e.cause.kind", "return e.cause.kind")}]=]):get()"
            : body);
        if (sandbox) Assert.Equal("permission_denied", values[0].ToText());
        else
        {
            Assert.True(values[0].IsNil);
            Assert.Equal("permission_denied", values[1].ToText());
        }
        Assert.Equal(42, engine.RunString("return 42")[0].I);
    }

    [Theory]
    [InlineData("aio.httpGet('http://127.0.0.1:9/')")]
    [InlineData("aio.httpRequest({method='GET', url='http://127.0.0.1:9/'})")]
    [InlineData("aio.getJson('http://127.0.0.1:9/')")]
    [InlineData("aio.httpSession():get('http://127.0.0.1:9/')")]
    public void Child_network_entry_points_require_the_child_permission(string call)
    {
        using var engine = Engine(1);
        string child = $"local v,e = {call}:await() return e.cause.kind";
        Assert.Equal("permission_denied", engine.RunString($"return sandbox.run([=[{child}]=]):get()")[0].ToText());
    }

    [Fact]
    public void Database_refusal_precedes_provider_invocation()
    {
        using var engine = Engine(4);
        int providerCalls = 0;
        engine.State.DatabaseFactory = _ =>
        {
            Interlocked.Increment(ref providerCalls);
            throw new InvalidOperationException("A denied operation reached the provider");
        };
        Assert.Equal("permission_denied", engine.RunString("return sandbox.run(\"local v,e = db.open(':memory:') return e.cause.kind\"):get()")[0].ToText());
        Assert.Equal(0, providerCalls);
        Assert.Equal(42, engine.RunString("return 42")[0].I);
    }

    [Fact]
    public void Unknown_permissions_are_rejected_without_running_a_child()
    {
        using var engine = Engine(7);
        int workersCreated = 0;
        var factory = engine.State.WorkerFactory!;
        engine.State.WorkerFactory = () => { workersCreated++; return factory(); };
        Value[] result = engine.RunString("return pcall(sandbox.run, 'return 99', {allow={'root'}})");
        Assert.False(result[0].IsTruthy);
        Assert.Contains("unknown permission 'root'", result[1].ToText());
        Assert.Equal(0, workersCreated);
        Assert.Equal(42, engine.RunString("return 42")[0].I);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("false")]
    [InlineData("{}")]
    public void Non_string_permission_entries_are_rejected(string entry)
    {
        using var engine = Engine(7);
        Value[] result = engine.RunString($"return pcall(sandbox.run, 'return 99', {{allow={{{entry}}}}})");
        Assert.False(result[0].IsTruthy);
        Assert.Contains("unknown permission ''", result[1].ToText());
        AssertWorkerReleased(engine);
        Assert.Equal(42, engine.RunString("return 42")[0].I);
    }

    [Fact]
    public void Legacy_process_entry_points_cannot_bypass_the_permission_gate()
    {
        using var engine = Engine();
        Value[] result = engine.RunString("return sandbox.run(\"assert(os.execute == nil) local f,e = io.popen('tealvm-policy-no-such-program') assert(f == nil) return e\"):get()");
        Assert.Equal("'popen' not supported", result[0].ToText());
    }

    [Fact]
    public void A_child_timeout_cannot_be_swallowed_by_nested_protected_calls()
    {
        using var engine = Engine();
        Value[] result = engine.RunString("""
            local child = sandbox.run([=[
                while true do
                    pcall(function()
                        xpcall(function() while true do end end, function(e) return e end)
                    end)
                end
            ]=], {timeout=0.2})
            local v,e = child:await()
            return e.kind
            """);
        Assert.Equal("timeout", result[0].ToText());
        AssertWorkerReleased(engine);
        Assert.Equal(42, engine.RunString("return 42")[0].I);
    }

    [Fact]
    public void An_empty_permission_set_does_not_confine_file_access()
    {
        string parent = Path.GetTempPath();
        Assert.True(Directory.Exists(parent));
        string directory = Directory.CreateTempSubdirectory("tealvm-sandbox-files-").FullName;
        string path = Path.Combine(directory, "owned.txt");
        try
        {
            File.WriteAllText(path, "sandbox file access is unrestricted");
            using var engine = Engine();
            Value[] values = engine.RunString("""
                local path = ...
                return sandbox.run([=[
                    local path = ...
                    local f = assert(io.open(path, 'rb'))
                    local text = f:read('*a')
                    f:close()
                    assert(fs.read(path) == text)
                    assert(aio.readFile(path):get() == text)
                    return text
                ]=], {args={path}}):get()
                """, "sandbox-files", Value.FromText(path));
            Assert.Equal("sandbox file access is unrestricted", values[0].ToText());
        }
        finally
        {
            // Only the GUID-owned fixture is removed; no recursive deletion is needed.
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Cancelling_a_running_child_escapes_both_protected_call_forms_and_releases_the_worker()
    {
        using var engine = Engine();
        using var entered = new ManualResetEventSlim();
        var factory = engine.State.WorkerFactory!;
        engine.State.WorkerFactory = () =>
        {
            LuaState child = factory();
            child.SetGlobal("cancel_gate", Value.FromFunction((ref CallContext c) =>
            {
                entered.Set(); // The worker is inside both protected calls before cancellation.
                return c.Return();
            }, "cancel_gate"));
            return child;
        };
        engine.State.SetGlobal("wait_for_child", Value.FromFunction((ref CallContext c) =>
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Worker did not enter protected code");
            return c.Return();
        }, "wait_for_child"));
        Value[] values = engine.RunString("""
            local child = sandbox.run([=[
                while true do
                    pcall(function()
                        xpcall(function() cancel_gate() while true do end end, function(e) return e end)
                    end)
                end
            ]=])
            wait_for_child()
            assert(child:cancel())
            local v,e = child:await()
            return e.kind
            """);
        Assert.Equal("cancelled", values[0].ToText());
        AssertWorkerReleased(engine);
        Assert.Equal(42, engine.RunString("return 42")[0].I);
    }
}
