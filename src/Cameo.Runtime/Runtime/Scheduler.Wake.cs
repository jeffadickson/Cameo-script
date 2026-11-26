using System.Diagnostics;

namespace Cameo.Runtime.Vm;

public sealed partial class Scheduler
{
    private readonly object _wakeGate = new();
    private TaskCompletionSource? _wake;
    private bool _shutdown;
    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Broadcast generations never consume the semaphore used by synchronous VM waits.
    // Complete outside the lock; no host continuation may run while the lock is held.
    private void NotifyHost()
    {
        if (Volatile.Read(ref _wake) == null) return;
        TaskCompletionSource? previous;
        lock (_wakeGate) { previous = _wake; _wake = null; }
        previous?.TrySetResult();
    }

    /// <summary>Asynchronously waits for ready work, the next timer, or a scheduler change.
    /// Call on the VM owner thread; awaiting does not execute callbacks or transfer ownership.
    /// Notifications may coalesce: pump and reread authoritative state after every wake.
    /// Several waiters may observe the same wake. Cancellation affects only that wait.</summary>
    public Task WaitForWorkAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (Volatile.Read(ref _shutdown)) return Task.FromException(new ObjectDisposedException(nameof(Scheduler)));
        Task change;
        lock (_wakeGate) change = (_wake ??= NewWake()).Task;
        // Snapshot the generation before readiness: an external post racing this check
        // is either already visible in the inbox or completes this generation afterward.
        if (_ready.Count != 0 || !_inbox.IsEmpty) return Task.CompletedTask;
        if (!_timers.TryPeek(out _, out var timer)) return change.WaitAsync(cancellationToken);
        long now = Stopwatch.GetTimestamp();
        if (timer.Due <= now) return Task.CompletedTask;
        // Widen before subtraction/multiplication. Long waits are chunked; wakes are hints.
        double milliseconds = Math.Clamp(Math.Ceiling((timer.Due - (double)now) * 1000 / Stopwatch.Frequency), 1, int.MaxValue);
        return WaitForChangeOrTimer(change, TimeSpan.FromMilliseconds(milliseconds), cancellationToken);
    }

    // Closing is terminal; Clear alone remains reusable after an ordinary aborted run.
    internal void Shutdown()
    {
        Volatile.Write(ref _shutdown, true);
        Clear();
    }

    private static async Task WaitForChangeOrTimer(Task change, TimeSpan delay, CancellationToken cancellationToken)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task signal = change.WaitAsync(race.Token);
        Task timer = Task.Delay(delay, race.Token);
        try
        {
            Task winner = await Task.WhenAny(signal, timer).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
        }
        finally
        {
            // Remove both losing waits, including the proxy subscribed to the generation.
            race.Cancel();
            try { await signal.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await timer.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
}
