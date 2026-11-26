using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>CsCheck properties for futures: combinator chains against a plain model, typed
/// errors on the failure track, ordering of allOf, and completion from other threads.</summary>
public class FutureProperties
{
    private enum Step { Add, Mul, Fail, Recover, Compose, Delay }

    private static readonly Gen<(Step Op, int K)> GenStep =
        Gen.Select(Gen.Enum<Step>(), Gen.Int[-9, 9]);

    // The model: a value, or the kind of error currently on the failure track.
    private static (long Value, string? Err) Model(long start, List<(Step Op, int K)> steps)
    {
        long v = start;
        string? err = null;
        foreach (var (op, k) in steps)
        {
            switch (op)
            {
                case Step.Add or Step.Compose or Step.Delay:
                    if (err == null) v = unchecked(v + k);
                    break;
                case Step.Mul:
                    if (err == null) v = unchecked(v * k);
                    break;
                case Step.Fail:
                    if (err == null) err = "failed";
                    break;
                case Step.Recover:
                    if (err != null) { err = null; v = k; }
                    break;
            }
        }
        return (v, err);
    }

    private static string Program(long start, List<(Step Op, int K)> steps)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"local f = Future.supplyAsync(function() return {start} end)\n");
        foreach (var (op, k) in steps)
        {
            sb.Append(op switch
            {
                Step.Add => $"f = f:thenApply(function(x) return x + ({k}) end)\n",
                Step.Mul => $"f = f:thenApply(function(x) return x * ({k}) end)\n",
                Step.Fail => $"f = f:thenApply(function(x) error('step failed') end)\n",
                Step.Recover => $"f = f:exceptionally(function(e) assert(e.kind == 'failed') return {k} end)\n",
                Step.Compose => $"f = f:thenCompose(function(x) return Future.supplyAsync(function() return x + ({k}) end) end)\n",
                _ => $"f = f:thenCompose(function(x) return Future.delay(0, x + ({k})) end)\n",
            });
        }
        sb.Append("local v, err = f:await()\nreturn v, err and err.kind, f:isDone(), f:isCompletedExceptionally()\n");
        return sb.ToString();
    }

    [Fact]
    public void CombinatorChainsFollowTheRailwayModel()
    {
        var host = new LuaHost();
        Prop.Cs(Gen.Select(Gen.Long[-1000, 1000], GenStep.List[0, 12]), p =>
        {
            var (start, steps) = p;
            var (value, err) = Model(start, steps);
            Value[] r = host.Run(Program(start, steps));
            Assert.True(r[2].IsTruthy);
            if (err == null)
            {
                Assert.Equal(value, r[0].I);
                Assert.True(r[1].IsNil);
                Assert.False(r[3].IsTruthy);
            }
            else
            {
                Assert.True(r[0].IsNil);
                Assert.Equal(err, r[1].AsString);
                Assert.True(r[3].IsTruthy);
            }
        }, iter: 150);
    }

    [Fact]
    public void AllOfKeepsArgumentOrderWhateverTheCompletionOrder()
    {
        var host = new LuaHost();
        Prop.Cs(Gen.Int[0, 3].List[0, 8], delays =>
        {
            var sb = new System.Text.StringBuilder("local fs = {}\n");
            for (int i = 0; i < delays.Count; i++)
                sb.Append($"fs[{i + 1}] = Future.delay({delays[i]} * 0.001, {i * 10})\n");
            sb.Append("local all = Future.allOf(fs):get()\nreturn #all, table.concat(all, ',')\n");
            Value[] r = host.Run(sb.ToString());
            Assert.Equal(delays.Count, r[0].I);
            Assert.Equal(string.Join(",", Enumerable.Range(0, delays.Count).Select(i => i * 10)), r[1].AsString);
        }, iter: 40);
    }

    [Fact]
    public void TasksInterleaveInFifoOrderAtAwaits()
    {
        var host = new LuaHost();
        Prop.Cs(Gen.Int[1, 6], n =>
        {
            // n tasks each append their id, suspend on a timer, append again (awaiting a future that is already done does not suspend)
            string src = $@"
                local log = {{}}
                local fs = {{}}
                for id = 1, {n} do
                  fs[id] = Future.runAsync(function()
                    log[#log + 1] = 'a' .. id
                    Future.delay(0):await()
                    log[#log + 1] = 'b' .. id
                  end)
                end
                Future.allOf(fs):get()
                return table.concat(log, ' ')";
            string expected = string.Join(" ", Enumerable.Range(1, n).Select(i => "a" + i).Concat(Enumerable.Range(1, n).Select(i => "b" + i)));
            Assert.Equal(expected, host.Run(src)[0].AsString);
        }, iter: 6);
    }

    [Fact]
    public void HostTasksCompleteFuturesFromOtherThreads()
    {
        var host = new LuaHost();
        Prop.Cs(Gen.Long[-100, 100].List[1, 6], values =>
        {
            var tbl = new Table();
            for (int i = 0; i < values.Count; i++)
            {
                long v = values[i];
                LuaFuture f = host.L.FutureFromTask(Task.Run(async () =>
                {
                    await Task.Delay(1);
                    return Value.FromInt(v);
                }));
                tbl.SetInt(i + 1, Value.FromUserdata(f));
            }
            host.L.SetGlobal("pending", tbl);
            Value[] r = host.Run("local s = 0 for _, v in ipairs(Future.allOf(pending):get()) do s = s + v end return s");
            Assert.Equal(values.Sum(), r[0].I);
        }, iter: 10);
    }

    [Fact]
    public void ResultUnionReportsValueOrTypedError()
    {
        var L = new LuaHost().L;
        var ok = new LuaFuture(L);
        Assert.Null(ok.Result);
        ok.Complete(Value.FromInt(7));
        var bad = new LuaFuture(L);
        bad.Fail(new FutureError(FutureErrorKind.Timeout, "timed out"));
        string Describe(LuaFuture f) => f.Result switch
        {
            null => "pending",
            { } r => r switch
            {
                Completed c => "value " + c.Value,
                FutureError e => "error " + e.Kind,
            },
        };
        Assert.Equal("value 7", Describe(ok));
        Assert.Equal("error Timeout", Describe(bad));
        Assert.False(ok.Complete(Value.FromInt(8)));
        Assert.False(bad.Fail(new FutureError(FutureErrorKind.Cancelled, "cancelled")));
    }
}
