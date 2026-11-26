using CsCheck;
using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Xunit;

namespace Cameo.Tests;

/// <summary>ScriptEngine.TryCall: the host calling a function of a loaded script.</summary>
public class HostCallTests
{
    private static ScriptEngine Load(string source, CameoRuntimeOptions? options = null)
    {
        var engine = new ScriptEngine(options);
        Assert.IsType<RunCompleted>(engine.TryRunString(source).Value);
        return engine;
    }

    [Fact]
    public void A_script_function_is_called_with_host_values_and_returns_all_its_results()
    {
        using var engine = Load("global function f(a: integer, s: string): integer, string return a * 2, s .. '!' end");
        var done = Assert.IsType<RunCompleted>(engine.TryCall(engine.State.GetGlobal("f"), Value.FromInt(21), Value.FromText("hi")).Value);
        Assert.Equal(42, done.Values[0].I);
        Assert.Equal("hi!", done.Values[1].ToText());
    }

    [Fact]
    public void State_persists_between_calls()
    {
        using var engine = Load("local n = 0  global function next(): integer n += 1 return n end");
        Value next = engine.State.GetGlobal("next");
        for (int i = 1; i <= 5; i++)
            Assert.Equal(i, Assert.IsType<RunCompleted>(engine.TryCall(next).Value).Values[0].I);
    }

    [Fact]
    public void An_error_in_the_function_is_a_RuntimeFailed_carrying_the_error_value()
    {
        using var engine = Load("global function f() error({kind = 'bad', code = 7}) end");
        var failed = Assert.IsType<RuntimeFailed>(engine.TryCall(engine.State.GetGlobal("f")).Value);
        Assert.Equal("bad", ((Table)failed.Error.O!).GetStr("kind").ToText());
    }

    [Fact]
    public void The_time_limit_applies_to_calls_and_the_engine_stays_usable()
    {
        using var engine = Load("global function spin() while true do end end  global function ok(): integer return 1 end",
            new CameoRuntimeOptions { Timeout = TimeSpan.FromMilliseconds(200) });
        var aborted = Assert.IsType<RunAborted>(engine.TryCall(engine.State.GetGlobal("spin")).Value);
        Assert.Equal(AbortReason.Timeout, aborted.Reason);
        Assert.Equal(1, Assert.IsType<RunCompleted>(engine.TryCall(engine.State.GetGlobal("ok")).Value).Values[0].I);
    }

    [Fact]
    public void Exit_and_tasks_started_by_the_call_are_handled()
    {
        using var engine = Load("""
            global done = 0
            global function quit() os.exit(4) end
            global function later() Future.runAsync(function() Future.delay(0.01):await() done = 1 end) end
            """);
        Assert.Equal(4, Assert.IsType<RunExited>(engine.TryCall(engine.State.GetGlobal("quit")).Value).Code);
        Assert.IsType<RunCompleted>(engine.TryCall(engine.State.GetGlobal("later")).Value);
        Assert.Equal(1, engine.State.GetGlobal("done").I);      // the call waited for the task it started
    }

    [Fact]
    public void A_FloatArray_result_is_the_scripts_own_buffer()
    {
        using var engine = Load("global buf = FloatArray.new(4, 1)  global function get(): FloatArray return buf end  global function total(): number return buf:sum() end");
        double[] data = ((FloatArray)Assert.IsType<RunCompleted>(engine.TryCall(engine.State.GetGlobal("get")).Value).Values[0].O!).Data;
        data[0] = 10;                                            // the host writes, the script sees it
        Assert.True(Assert.IsType<RunCompleted>(engine.TryCall(engine.State.GetGlobal("total")).Value).Values[0].TryToDouble(out double total));
        Assert.Equal(13.0, total);
    }

    // Property: calling a script function from the host gives what the same arithmetic gives in C#.
    [Fact]
    public void Calls_agree_with_a_reference_computation()
    {
        using var engine = Load("global function f(a: integer, b: integer, x: number): integer, number return a * b - a, x * 0.5 + a end");
        Value f = engine.State.GetGlobal("f");
        Gen.Select(Gen.Long[-1_000_000, 1_000_000], Gen.Long[-1_000_000, 1_000_000], Gen.Double[-1e9, 1e9]).Sample(t =>
        {
            var (a, b, x) = t;
            Value[] r = ((RunCompleted)engine.TryCall(f, Value.FromInt(a), Value.FromInt(b), Value.FromFloat(x)).Value!).Values;
            return r[0].I == a * b - a && r[1].TryToDouble(out double d) && d == x * 0.5 + a;
        }, iter: 2000, threads: 1);      // one engine: one thread
    }

    [Fact]
    public void Started_tasks_run_only_when_the_host_pumps_and_several_progress_together()
    {
        using var engine = Load("""
            global events: {string} = {}
            global function slow(name: string, gate: Future<boolean>): string
               table.insert(events, name .. " begins")
               gate:await()
               table.insert(events, name .. " ends")
               return name
            end
            global function broken(): string error({kind = "bad"}) end
            """);
        Value slow = engine.State.GetGlobal("slow");
        // Completion order is controlled by the host, not by assuming it will be
        // scheduled between two timer deadlines under arbitrary contention.
        var releaseA = new TaskCompletionSource<Value>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource<Value>(TaskCreationOptions.RunContinuationsAsynchronously);
        LuaFuture a = engine.Start(slow, Value.FromText("a"), Value.FromUserdata(engine.State.FutureFromTask(releaseA.Task)));
        LuaFuture b = engine.Start(slow, Value.FromText("b"), Value.FromUserdata(engine.State.FutureFromTask(releaseB.Task)));
        LuaFuture bad = engine.Start(engine.State.GetGlobal("broken"));
        Assert.False(a.IsDone);
        Assert.Equal(0, ((Table)engine.State.GetGlobal("events").O!).Length);       // nothing ran yet

        Assert.IsType<RunCompleted>(engine.TryPump(TimeSpan.Zero, out bool idle).Value);
        Assert.False(idle);
        Assert.Equal(2, ((Table)engine.State.GetGlobal("events").O!).Length);       // both began; neither blocked the host
        Assert.True(bad.IsDone && bad.Error != null);

        var started = System.Diagnostics.Stopwatch.StartNew();
        releaseB.SetResult(Value.True);
        while (!b.IsDone)
        {
            Assert.IsType<RunCompleted>(engine.TryPump(TimeSpan.FromMilliseconds(10), out idle).Value);
            Assert.False(a.IsDone);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
        }
        releaseA.SetResult(Value.True);
        while (!a.IsDone)
        {
            Assert.IsType<RunCompleted>(engine.TryPump(TimeSpan.FromMilliseconds(10), out idle).Value);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
        }
        Assert.Equal("a", a.Value.ToText());
        Assert.Equal("b", b.Value.ToText());
        engine.TryPump(TimeSpan.Zero, out idle);
        Assert.True(idle);
    }

    [Fact]
    public void Pumping_is_subject_to_the_time_limit()
    {
        using var engine = Load("global function spin() while true do end end", new CameoRuntimeOptions { Timeout = TimeSpan.FromMilliseconds(200) });
        engine.Start(engine.State.GetGlobal("spin"));
        var aborted = Assert.IsType<RunAborted>(engine.TryPump(TimeSpan.FromSeconds(5), out _).Value);
        Assert.Equal(AbortReason.Timeout, aborted.Reason);
        Assert.IsType<RunCompleted>(engine.TryRunString("return 1").Value);
    }
}
