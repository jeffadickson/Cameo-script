using CameoHost;
using CsCheck;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>Host completion represents completed work and completed resource cleanup.</summary>
public class CameoHostLifetimeTests
{
    [Fact]
    public async Task Generated_results_are_detached_only_after_owner_cleanup()
    {
        // Keep shrinking/replay attached to the assertion while each sample owns
        // its own resource. No LuaState is shared across generator threads.
        await Gen.Int.SampleAsync(async value =>
        {
            bool disposed = false;
            int owner = 0;
            Task<int> result = OwnerThread.Run(() =>
            {
                owner = Environment.CurrentManagedThreadId;
                return new Resource(() =>
                {
                    Assert.Equal(owner, Environment.CurrentManagedThreadId);
                    disposed = true;
                });
            }, _ => value);
            Assert.Equal(value, await result.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(disposed);
        }, seed: Prop.Seeds.CsCheckSeed, iter: 64, threads: 1);
    }

    [Fact]
    public async Task Completion_waits_for_disposal()
    {
        using var disposing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<int> result = OwnerThread.Run(() => new Resource(() =>
        {
            disposing.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test release");
        }), _ => 42);
        try
        {
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(result.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(42, await result.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Disposal_failure_cannot_publish_success()
    {
        var failure = new InvalidOperationException("cleanup failed");
        Task<int> result = OwnerThread.Run(() => new Resource(() => throw failure), _ => 42);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => result));
    }

    [Fact]
    public async Task Work_failure_disposes_on_the_owner_thread()
    {
        int owner = 0;
        int disposer = 0;
        var failure = new InvalidOperationException("work failed");
        Task<int> result = OwnerThread.Run<Resource, int>(() =>
        {
            owner = Environment.CurrentManagedThreadId;
            return new Resource(() => disposer = Environment.CurrentManagedThreadId);
        }, _ => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => result));
        Assert.Equal(owner, disposer);
        Assert.NotEqual(0, disposer);
    }

    [Fact]
    public async Task Acquisition_failure_is_observed()
    {
        var failure = new InvalidOperationException("open failed");
        Task<int> result = OwnerThread.Run<Resource, int>(() => throw failure, _ => 42);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => result));
    }

    private sealed class Resource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
