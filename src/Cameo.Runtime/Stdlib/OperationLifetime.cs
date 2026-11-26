using System.Diagnostics;

namespace Cameo.Runtime.Stdlib;

/// <summary>Cancellation registrations belong to both the producer and its run/future.
/// Release them only after the producer stopped and the operation settled or was aborted.</summary>
internal sealed class OperationLifetime
{
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationTokenSource _timeout = new();
    private readonly CancellationTokenRegistration _onTimeout, _onAbort;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _timedOut;
    internal CancellationToken Token { get; }
    internal bool TimedOut => Volatile.Read(ref _timedOut);
    internal Task Completion { get; }

    internal OperationLifetime(CancellationToken lifetime, double timeoutSeconds = -1)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Token = _cancellation.Token;
        _onTimeout = _timeout.Token.Register(() => { Volatile.Write(ref _timedOut, true); Cancel(); });
        _onAbort = lifetime.Register(Settle);
        if (timeoutSeconds >= 0)
            _timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(timeoutSeconds, int.MaxValue / 1000)));
        Completion = DisposeWhenFinished();
    }

    private void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { } // a late manual settlement after an ended run
        catch (AggregateException error) { Trace.TraceError("Cameo.Runtime operation cancellation callback failed: {0}", error); }
    }

    internal void Settle() { Cancel(); _settled.TrySetResult(); }
    internal void ProducerStopped() => _stopped.TrySetResult();

    private async Task DisposeWhenFinished()
    {
        await Task.WhenAll(_stopped.Task, _settled.Task).ConfigureAwait(false);
        // Await callbacks before disposing their targets: timeout/abort may be concurrent
        // with a worker finishing. No VM pump is needed after an aborted operation.
        await _onTimeout.DisposeAsync().ConfigureAwait(false);
        await _onAbort.DisposeAsync().ConfigureAwait(false);
        _timeout.Dispose();
        _cancellation.Dispose();
    }
}
