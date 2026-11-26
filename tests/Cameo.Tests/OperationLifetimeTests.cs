using Cameo.Runtime.Stdlib;
using Xunit;

namespace Cameo.Tests;

/// <summary>Producer completion and future/run termination are separate lifetime events.</summary>
public class OperationLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_terminal_events_are_required_before_registrations_are_released(bool producerFirst)
    {
        using var run = new CancellationTokenSource();
        var lifetime = new OperationLifetime(run.Token);
        if (producerFirst) lifetime.ProducerStopped(); else lifetime.Settle();
        Assert.False(lifetime.Completion.IsCompleted);
        if (producerFirst) lifetime.Settle(); else lifetime.ProducerStopped();
        await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(lifetime.Token.IsCancellationRequested);
        lifetime.Settle(); // a late manual settlement must remain harmless
    }

    [Fact]
    public async Task Abort_releases_registrations_after_producer_stops_without_a_vm_pump()
    {
        using var run = new CancellationTokenSource();
        var lifetime = new OperationLifetime(run.Token);
        run.Cancel();
        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.False(lifetime.Completion.IsCompleted);
        lifetime.ProducerStopped();
        await lifetime.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
