using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>Owns an acquired resource until its VM-thread builder adopts it. Cancellation
/// releases abandoned acquisitions even when abort drops the scheduler's queued builder.</summary>
internal static class PendingResource
{
    /// <summary>Internal observation of publication/abandonment completion, including cleanup.</summary>
    internal interface IObservation { Task Completion { get; } }
    internal static Func<LuaState, Value> Transfer<T>(T resource, CancellationToken cancellation,
        Func<LuaState, T, Value> publish, Func<T, ValueTask> release)
    {
        var pending = new Publication<T>(resource, publish, release);
        pending.Completion = pending.Observe(cancellation); // handles and traces cleanup failures
        return pending.Publish;
    }

    private sealed class Publication<T>(T resource, Func<LuaState, T, Value> publish, Func<T, ValueTask> release) : IObservation
    {
        public Task Completion { get; set; } = Task.CompletedTask;
        // 0 pending, 1 builder owns it, 2 adopted, 3 abandoned/build failed.
        private int _state;
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Value Publish(LuaState state)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) throw new OperationCanceledException();
            try
            {
                Value value = publish(state, resource);
                Volatile.Write(ref _state, 2);
                return value;
            }
            finally
            {
                Interlocked.CompareExchange(ref _state, 3, 1);
                _finished.TrySetResult();
            }
        }

        internal async Task Observe(CancellationToken cancellation)
        {
            try
            {
                try { await _finished.Task.WaitAsync(cancellation).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // If publication already started, let its synchronous builder finish
                    // before deciding ownership; never dispose underneath that builder.
                    if (Interlocked.CompareExchange(ref _state, 3, 0) != 0)
                        await _finished.Task.ConfigureAwait(false);
                }
                if (Volatile.Read(ref _state) == 3) await release(resource).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // An abandoned run has no script result channel. Keep failed cleanup
                // observable to host Trace listeners without touching VM state off-thread.
                Trace.TraceError("Cameo.Runtime abandoned resource cleanup failed: {0}", error);
            }
        }
    }

    /// <summary>Always attempts final disposal; reports both failures if each stage fails.</summary>
    internal static async Task FinallyAsync(Func<Task> operation, Func<ValueTask> dispose)
    {
        Exception? primary = null;
        try { await operation().ConfigureAwait(false); }
        catch (Exception error) { primary = error; }
        try { await dispose().ConfigureAwait(false); }
        catch (Exception cleanup) when (primary != null)
        {
            throw new AggregateException("The operation and its cleanup both failed.", primary, cleanup);
        }
        if (primary != null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
