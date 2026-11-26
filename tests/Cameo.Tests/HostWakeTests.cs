using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Wake hints do not consume accepted work or execute VM callbacks.</summary>
public class HostWakeTests
{
    // Reflection lets the initial test fail on its missing public contract, not at compile time.
    private static Task Wait(Scheduler scheduler, CancellationToken token = default)
    {
        var method = typeof(Scheduler).GetMethod("WaitForWorkAsync", [typeof(CancellationToken)]);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(scheduler, [token]));
    }

    [Fact]
    public async Task External_work_wakes_all_waiters_without_running_it()
    {
        var scheduler = new Scheduler();
        Task a = Wait(scheduler), b = Wait(scheduler);
        Assert.False(a.IsCompleted);
        int calls = 0;
        await Task.Run(() => scheduler.PostExternal(() => calls++));
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, calls);
        Assert.Equal(1, scheduler.RunReady());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Ready_work_before_wait_is_not_lost()
    {
        var scheduler = new Scheduler();
        scheduler.Post(() => { });
        await Wait(scheduler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scheduler.RunReady());
        using var cancel = new CancellationTokenSource();
        Task idle = Wait(scheduler, cancel.Token);
        Assert.False(idle.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle);
    }

    [Fact]
    public async Task Timer_deadline_wakes_without_executing_the_timer()
    {
        var scheduler = new Scheduler();
        int calls = 0;
        scheduler.AddTimer(0.025, () => calls++);
        await Wait(scheduler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, calls);
        // Early hints are allowed; the owner always rereads the authoritative deadline.
        while (scheduler.RunReady() == 0) await Wait(scheduler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Earlier_timer_and_clear_wake_existing_waits()
    {
        var scheduler = new Scheduler();
        scheduler.AddTimer(3600, () => { });
        Task wait = Wait(scheduler);
        scheduler.AddTimer(0, () => { });
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scheduler.RunReady());
        Task reset = Wait(scheduler);
        scheduler.Clear();
        await reset.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        Task idle = Wait(scheduler, cancel.Token);
        Assert.False(idle.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_wait_does_not_steal_a_later_signal(bool timed)
    {
        var scheduler = new Scheduler();
        if (timed) scheduler.AddTimer(3600, () => { });
        for (int i = 0; i < 20; i++)
        {
            using var cancellation = new CancellationTokenSource();
            Task abandoned = Wait(scheduler, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        }
        Task current = Wait(scheduler);
        scheduler.PostExternal(() => { });
        await current.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, scheduler.RunReady());
    }

    [Fact]
    public async Task Burst_notifications_preserve_every_accepted_callback()
    {
        var scheduler = new Scheduler();
        Task wake = Wait(scheduler);
        int calls = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => scheduler.PostExternal(() => calls++))));
        await wake.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, scheduler.RunReady());
        Assert.Equal(100, calls);
    }

    [Fact]
    public async Task Engine_exposes_same_non_consuming_host_wait()
    {
        using var engine = new ScriptEngine();
        var method = typeof(ScriptEngine).GetMethod("WaitForWorkAsync", [typeof(CancellationToken)]);
        Assert.NotNull(method);
        Task wait = Assert.IsAssignableFrom<Task>(method.Invoke(engine, [CancellationToken.None]));
        engine.State.Scheduler.PostExternal(() => { });
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<RunCompleted>(engine.TryPump(TimeSpan.Zero, out bool idle).Value);
        Assert.True(idle);
    }

    [Fact]
    public async Task Engine_close_wakes_existing_wait_and_refuses_new_waits()
    {
        using var engine = new ScriptEngine();
        Task wait = engine.WaitForWorkAsync();
        engine.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.WaitForWorkAsync());
    }
}
