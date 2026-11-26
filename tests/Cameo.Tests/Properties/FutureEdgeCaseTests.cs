using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>Example-based cases added to kill mutants that survived Stryker on
/// Runtime/Futures.cs, Stdlib/FutureLib.cs and Runtime/MathBuiltins.cs.</summary>
public class FutureEdgeCaseTests
{
    private static string Run(string src)
    {
        var host = new LuaHost();
        Value[] r = host.Run(src);
        return string.Join("|", r.Select(v => v.ToString()));
    }

    [Fact]
    public void RunAsyncDiscardsItsResultAndJoinRaises()
    {
        Assert.Equal("nil|5|1", Run(@"
            local r = Future.runAsync(function() return 5 end)
            local s = Future.supplyAsync(function() return 5, 6 end)
            return r:get(), s:get(), select('#', s:get())"));
        Assert.Equal("false|failed", Run(@"
            local ok, e = pcall(function() return Future.failedFuture('x'):join() end)
            return ok, e.kind"));
    }

    [Fact]
    public void AwaitAlwaysReturnsValueAndError()
    {
        Assert.Equal("2|2|1", Run(@"
            local done = Future.completedFuture(1)
            local pending = Future.delay(0, 1)
            return select('#', done:await()), select('#', pending:await()), select('#', done:get())"));
    }

    [Fact]
    public void ARaisedFutureErrorPassesThroughUnwrapped()
    {
        // a task that lets a failed get() escape fails with that same error, not a wrapper
        Assert.Equal("true|failed|boom|true|cancelled|cancelled|timeout|deadlock", Run(@"
            local inner = Future.failedFuture('boom')
            local _, e1 = inner:await()
            local outer = Future.supplyAsync(function() return inner:get() end)
            local _, e2 = outer:await()
            local c = Future.new(); c:cancel()
            local _, e3 = Future.supplyAsync(function() return c:get() end):await()
            local _, e4 = Future.supplyAsync(function() return Future.delay(5):orTimeout(0.001):get() end):await()
            local _, e5 = Future.supplyAsync(function() error(setmetatable({kind = 'deadlock', message = 'm'}, getmetatable(e1))) end):await()
            return e1 == e2, e2.kind, e2.cause, c:isCancelled(), e3.kind, e3.message, e4.kind, e5.kind"));
    }

    [Fact]
    public void TostringShowsTheState()
    {
        Assert.Equal("Future(pending)|Future(completed)|Future(failed)|Future(cancelled)|FutureError(failed): x", Run(@"
            local p, d, f, c = Future.new(), Future.completedFuture(1), Future.failedFuture('x'), Future.new()
            c:cancel()
            return tostring(p), tostring(d), tostring(f), tostring(c), tostring(select(2, f:await()))"));
    }

    [Fact]
    public void TimeoutsInsideAndOutsideTasks()
    {
        Assert.Equal("timeout|timeout|ok|ok|timeout", Run(@"
            local function kind(f, ...) local ok, e = pcall(f.get, f, ...) return ok and 'ok' or e.kind end
            local inTask = Future.supplyAsync(function() return kind(Future.delay(5), 0.001) end):get()
            local outside = kind(Future.delay(5), 0.001)
            local fastIn = Future.supplyAsync(function() return kind(Future.delay(0.001), 5) end):get()
            local fastOut = kind(Future.delay(0.001), 5)
            local zero = kind(Future.new(), 0)
            return inTask, outside, fastIn, fastOut, zero"));
        // a timed-out wrapper does not settle the source; a completed source wins over the timer
        Assert.Equal("false|7|7", Run(@"
            local src = Future.new()
            local t = src:orTimeout(0.001)
            t:await()
            local wasDone = src:isDone()
            src:complete(7)
            return wasDone, src:get(), Future.completedFuture(7):orTimeout(0.001):get()"));
    }

    [Fact]
    public void StageArgumentsAndPassThrough()
    {
        Assert.Equal("0|1|2|nil|9|failed|failed|action failed", Run(@"
            local nargs = function(...) return select('#', ...) end
            local run = Future.completedFuture(9):thenApply(function(x) return x end)
            local a = Future.new()
            Future.completedFuture(9):thenRun(function(...) a:complete(nargs(...)) end)
            local b = Future.new()
            Future.completedFuture(9):thenAccept(function(...) b:complete(nargs(...)) end)
            local c = Future.new()
            Future.completedFuture(9):whenComplete(function(...) c:complete(nargs(...)) end)
            local acceptResult = Future.completedFuture(9):thenAccept(function(x) return x end):get()
            local seenErr
            local w = Future.failedFuture('e'):whenComplete(function(v, e) seenErr = e.kind end)
            local _, werr = w:await()
            local _, aerr = Future.completedFuture(1):whenComplete(function() error('action failed') end):await()
            return a:get(), b:get(), c:get(), acceptResult, run:get(), seenErr, werr.kind, (aerr.message:gsub('^.-:%d+: ', ''))"));
    }

    [Fact]
    public void ThenCombineWaitsForBothAndFailsFast()
    {
        Assert.Equal("3|failed|left|failed|right|false", Run(@"
            local slow = Future.delay(0.002, 2)
            local sum = Future.completedFuture(1):thenCombine(slow, function(x, y) return x + y end)
            local called = false
            local f = function() called = true end
            local _, e1 = Future.failedFuture('left'):thenCombine(Future.new(), f):await()
            local _, e2 = Future.new():thenCombine(Future.failedFuture('right'), f):await()
            return sum:get(), e1.kind, e1.cause, e2.kind, e2.cause, called"));
    }

    [Fact]
    public void AllOfAndAnyOfTakeListsOrArguments()
    {
        Assert.Equal("3|1,2,3|3|a|0|first|failed|1|nil|3", Run(@"
            local a, b, c = Future.completedFuture(1), Future.delay(0.002, 2), Future.completedFuture(3)
            local viaArgs = Future.allOf(a, b, c):get()
            local viaList = Future.allOf({a, b, c}):get()
            local any = Future.anyOf(Future.new(), Future.completedFuture('a'), Future.new()):get()
            local empty = Future.allOf({}):get()
            local late = Future.new()
            local all = Future.allOf(Future.failedFuture('first'), late)
            local _, e = all:await()
            late:completeExceptionally('second')
            local holes = Future.allOf(Future.completedFuture(1), Future.completedFuture(nil), Future.completedFuture(3)):get()
            return #viaArgs, table.concat(viaList, ','), #viaList, any, #empty, e.cause, e.kind, holes[1], holes[2], holes[3]"));
        Assert.Contains("element 2 is not a Future", Assert.Throws<LuaException>(() => Run("return Future.allOf({Future.new(), 5})")).Message);
    }

    [Fact]
    public void CancellingBeforeATaskStartsSkipsIt()
    {
        Assert.Equal("false|cancelled|true", Run(@"
            local ran = false
            local f = Future.supplyAsync(function() ran = true return 1 end)
            f:cancel()
            local _, e = f:await()
            Future.delay(0.001):get()
            local g = Future.new()
            return ran, e.kind, g:complete(1) and not g:complete(2) and not g:completeExceptionally('x')"));
    }

    [Fact]
    public void ProgramEndWaitsForTasksButNotForAbandonedTimers()
    {
        var host = new LuaHost();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        host.Run(@"
            Future.delay(30)                                            -- nobody waits on this
            Future.runAsync(function() Future.delay(0.005):await() print('task done') end)
            Future.completedFuture(1):thenAccept(function() print('callback done') end)");
        Assert.True(sw.Elapsed.TotalSeconds < 5, "an abandoned timer kept the program alive");
        Assert.Equal("callback done\ntask done\n", host.Out.ToString());
        Assert.Equal(0, host.L.Scheduler.LiveTasks);
    }

    [Fact]
    public void TimedGetCalledDirectlyInATaskSuspendsIt()
    {
        // get(timeout) straight from task code parks the task on a timeout wrapper; another
        // task runs meanwhile, and the failure is raised inside the waiting task
        Assert.Equal("timeout|other ran first|7", Run(@"
            local log = {}
            local waiter = Future.supplyAsync(function()
              local ok, e = pcall(function() return Future.new():get(0.002) end)
              log[#log + 1] = e.kind
              return Future.delay(0.001, 7):get(5)
            end)
            Future.runAsync(function() log[#log + 1] = 'other ran first' end)
            local v = waiter:get()
            return log[2], log[1], v"));
    }

    [Fact]
    public void LiveTaskCountAndMalformedErrorRecords()
    {
        var host = new LuaHost();
        Value[] r = host.Run(@"
            local f = Future.supplyAsync(function() return 1 end)
            f:cancel()
            Future.delay(0.001):get()
            local mt = getmetatable(select(2, Future.failedFuture('x'):await()))
            local _, e1 = Future.failedFuture(setmetatable({}, mt)):await()
            local _, e2 = Future.failedFuture(setmetatable({kind = 'timeout', message = 'slow'}, mt)):await()
            local _, e3 = Future.supplyAsync(function() error() end):await()
            return e1.kind, e2.kind, e2.message, e3.kind, e3.cause");
        Assert.Equal(0, host.L.Scheduler.LiveTasks);
        Assert.Equal("failed|timeout|slow|failed|nil", string.Join("|", r.Select(v => v.ToString())));
        // a record without a valid kind is not trusted: it is wrapped as the cause of a "failed" error
        var malformed = new Table { Metatable = host.L.FutureErrorMeta };
        var raised = FutureError.FromRaised(host.L, malformed);
        Assert.Equal(FutureErrorKind.Failed, raised.Kind);
        Assert.Same(malformed, raised.Cause.AsTable);
        malformed.SetStr("kind", "bogus");
        malformed.SetStr("message", "m");
        Assert.Same(malformed, FutureError.FromRaised(host.L, malformed).Cause.AsTable);
        malformed.SetStr("kind", "cancelled");
        Assert.Equal((FutureErrorKind.Cancelled, "m"), (FutureError.FromRaised(host.L, malformed).Kind, FutureError.FromRaised(host.L, malformed).Message));
    }

    [Fact]
    public void PlainYieldInsideATaskGivesOthersATurn()
    {
        Assert.Equal("a1 b1 a2 b2", Run(@"
            local log = {}
            local a = Future.runAsync(function() log[#log + 1] = 'a1' coroutine.yield() log[#log + 1] = 'a2' end)
            local b = Future.runAsync(function() log[#log + 1] = 'b1' coroutine.yield() log[#log + 1] = 'b2' end)
            Future.allOf(a, b):get()
            return table.concat(log, ' ')"));
    }

    [Fact]
    public void CallbacksRegisteredBeforeAndAfterCompletionAllRun()
    {
        var L = new LuaHost().L;
        var f = new LuaFuture(L);
        var seen = new List<int>();
        f.OnComplete(_ => seen.Add(1));
        f.OnComplete(_ => seen.Add(2));
        Assert.True(f.Complete(Value.FromInt(1)));
        f.OnComplete(_ => seen.Add(3));
        L.Scheduler.RunUntilIdle();
        Assert.Equal(new[] { 1, 2, 3 }, seen);
        Assert.True(L.Scheduler.IsIdle);
        // timers fire in due-time order, ties in registration order; external posts wake the loop
        var order = new List<string>();
        L.Scheduler.AddTimer(0.004, () => order.Add("late"));
        L.Scheduler.AddTimer(0.001, () => order.Add("early1"));
        L.Scheduler.AddTimer(0.001, () => order.Add("early2"));
        int epoch = L.Scheduler.BeginExternal();
        Task.Run(() => L.Scheduler.PostExternal(() => { L.Scheduler.EndExternal(epoch); order.Add("external"); }));
        Assert.False(L.Scheduler.IsIdle);
        L.Scheduler.RunUntilIdle();
        Assert.Equal(new[] { "early1", "early2", "late" }, order.Where(x => x != "external"));
        Assert.Contains("external", order);
        Assert.False(L.Scheduler.RunOne());
    }

    [Theory]
    [InlineData(5e-6)]
    [InlineData(-5e-6)]
    [InlineData(1e-8)]
    [InlineData(9.9e-6)]
    public void Expm1AndLog1pAreAccurateNearZero(double x)
    {
        // references from the Taylor series, exact to well below 1e-16 relative at this size
        double expm1 = x + x * x / 2 + x * x * x / 6 + x * x * x * x / 24;
        double log1p = x - x * x / 2 + x * x * x / 3 - x * x * x * x / 4;
        Assert.InRange(MathBuiltins.Apply(MathFn.Expm1, x) / expm1, 1 - 1e-13, 1 + 1e-13);
        Assert.InRange(MathBuiltins.Apply(MathFn.Log1p, x) / log1p, 1 - 1e-13, 1 + 1e-13);
    }

    [Fact]
    public void Expm1AndLog1pAwayFromZero()
    {
        Assert.Equal(Math.E - 1, MathBuiltins.Apply(MathFn.Expm1, 1), 14);
        Assert.Equal(Math.Exp(2e-5) - 1, MathBuiltins.Apply(MathFn.Expm1, 2e-5), 15);
        Assert.Equal(Math.Exp(-3) - 1, MathBuiltins.Apply(MathFn.Expm1, -3), 15);
        Assert.Equal(Math.Log(2), MathBuiltins.Apply(MathFn.Log1p, 1), 15);
        Assert.Equal(Math.Log(1.5), MathBuiltins.Apply(MathFn.Log1p, 0.5), 15);
        Assert.Equal(1e-20, MathBuiltins.Apply(MathFn.Log1p, 1e-20));
        Assert.Equal(0.0, MathBuiltins.Apply(MathFn.Log1p, 0));
        Assert.True(MathBuiltins.TryGet("log2", out MathFn fn) && fn == MathFn.Log2);
        Assert.False(MathBuiltins.TryGet("nosuch", out _));
        for (int i = 0; i < MathBuiltins.Names.Length; i++)
            Assert.True(MathBuiltins.TryGet(MathBuiltins.Names[i], out MathFn f) && (int)f == i);
    }
}
