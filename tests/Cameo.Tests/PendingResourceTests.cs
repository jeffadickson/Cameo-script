using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Xunit;

namespace Cameo.Tests;

/// <summary>Deterministic ownership arbitration independent of real driver timing.</summary>
public class PendingResourceTests
{
    private sealed class Probe
    {
        public int Disposals;
        public ValueTask Dispose() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
    private static Task Finished(Func<LuaState, Value> builder) => ((PendingResource.IObservation)builder.Target!).Completion;

    [Fact]
    public async Task Cancellation_before_publication_disposes_once_and_refuses_adoption()
    {
        var probe = new Probe();
        using var cancel = new CancellationTokenSource();
        var build = PendingResource.Transfer(probe, cancel.Token, (_, _) => Value.True, p => p.Dispose());
        cancel.Cancel();
        await Finished(build).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, probe.Disposals);
        Assert.ThrowsAny<OperationCanceledException>(() => build(new LuaState()));
    }

    [Fact]
    public async Task Successful_adoption_transfers_ownership_despite_later_cancellation()
    {
        var probe = new Probe();
        using var cancel = new CancellationTokenSource();
        var build = PendingResource.Transfer(probe, cancel.Token, (_, _) => Value.FromInt(17), p => p.Dispose());
        Assert.Equal(17, build(new LuaState()).I);
        cancel.Cancel();
        await Finished(build).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, probe.Disposals);
        await probe.Dispose();
        Assert.Equal(1, probe.Disposals);
    }

    [Fact]
    public async Task Failed_builder_disposes_without_needing_a_cancellation()
    {
        var probe = new Probe();
        var build = PendingResource.Transfer(probe, CancellationToken.None,
            (_, _) => throw new InvalidOperationException("builder failed"), p => p.Dispose());
        Assert.Throws<InvalidOperationException>(() => build(new LuaState()));
        // This is a runaway guard, not a cleanup-latency promise. Full-suite CPU/worker
        // stress can delay thread-pool continuations that this isolated test uses.
        await Finished(build).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, probe.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_never_disposes_under_a_running_builder(bool failBuilder)
    {
        var probe = new Probe();
        using var cancel = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var build = PendingResource.Transfer(probe, cancel.Token, (_, _) =>
        {
            entered.Set();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(5)));
            if (failBuilder) throw new InvalidOperationException("builder failed");
            return Value.True;
        }, p => p.Dispose());
        Task<Value> publication = Task.Run(() => build(new LuaState()));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cancel.Cancel();
        Assert.Equal(0, probe.Disposals);
        finish.Set();
        if (failBuilder) await Assert.ThrowsAsync<InvalidOperationException>(() => publication);
        else Assert.True((await publication).IsTruthy);
        await Finished(build).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(failBuilder ? 1 : 0, probe.Disposals);
    }

    [Fact]
    public async Task Timed_out_resource_publication_reports_a_typed_error()
    {
        using var engine = new ScriptEngine();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new TaskCompletionSource<Func<LuaState, Value>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Probe();
        Func<CancellationToken, Task<Func<LuaState, Value>>> work = token =>
        {
            token.Register(() => cancelled.TrySetResult());
            var builder = PendingResource.Transfer(probe, token, (_, _) => Value.True, p => p.Dispose());
            created.TrySetResult(builder);
            return Task.FromResult(builder);
        };
        var start = typeof(AsyncIoLib).GetMethod("StartFuture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var future = (LuaFuture)start.Invoke(null, [engine.State, "tracked", work, 0.01, null])!;
        var pending = await created.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Finished(pending).WaitAsync(TimeSpan.FromSeconds(30));
        // A wake/readiness wait does not execute the queued builder; publication is owner work.
        await engine.WaitForWorkAsync().WaitAsync(TimeSpan.FromSeconds(30));
        engine.State.Scheduler.RunUntilIdle();
        Assert.Equal(FutureErrorKind.Timeout, future.Error!.Kind);
        Assert.Equal(1, probe.Disposals);
    }
}
