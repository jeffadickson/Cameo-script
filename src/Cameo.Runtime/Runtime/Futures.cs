using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cameo.Runtime.Vm;

/// <summary>Why a future did not produce a value. A closed set, so consumers can switch
/// over it exhaustively.</summary>
public enum FutureErrorKind
{
    /// <summary>The computation raised an error (see <see cref="FutureError.Cause"/>).</summary>
    Failed,
    Cancelled,
    Timeout,
    /// <summary>A blocking wait found nothing left that could ever complete the future.</summary>
    Deadlock,
}

/// <summary>The typed error carried on a future's failure track.</summary>
public sealed class FutureError
{
    public FutureErrorKind Kind { get; }
    /// <summary>Human-readable description (a VM byte string).</summary>
    public string Message { get; }
    /// <summary>The original error value for <see cref="FutureErrorKind.Failed"/>, else nil.</summary>
    public Value Cause { get; }
    private Table? _table;

    public FutureError(FutureErrorKind kind, string message, Value cause = default)
    {
        Kind = kind;
        Message = message;
        Cause = cause;
    }

    public static string KindName(FutureErrorKind kind) => kind switch
    {
        FutureErrorKind.Failed => "failed",
        FutureErrorKind.Cancelled => "cancelled",
        FutureErrorKind.Timeout => "timeout",
        FutureErrorKind.Deadlock => "deadlock",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The error as the Teal record <c>FutureError</c> (kind, message, cause).</summary>
    public Value ToValue(LuaState L)
    {
        if (_table == null)
        {
            _table = new Table(0, 4) { Metatable = L.FutureErrorMeta };
            _table.SetStr("kind", KindName(Kind));
            _table.SetStr("message", Message);
            _table.SetStr("cause", Cause);
        }
        return Value.FromTable(_table);
    }

    /// <summary>Wraps an error value raised by a computation. A value that already is a
    /// FutureError record (e.g. re-raised by <c>get</c>) is passed through unchanged.</summary>
    public static FutureError FromRaised(LuaState L, Value raised)
    {
        if (raised.IsTable && ReferenceEquals(raised.AsTable.Metatable, L.FutureErrorMeta))
        {
            Table t = raised.AsTable;
            Value kind = t.GetStr("kind"), msg = t.GetStr("message");
            FutureErrorKind? k = !kind.IsString ? null : kind.AsString switch
            {
                "failed" => FutureErrorKind.Failed,
                "cancelled" => FutureErrorKind.Cancelled,
                "timeout" => FutureErrorKind.Timeout,
                "deadlock" => FutureErrorKind.Deadlock,
                _ => null,
            };
            // Only a well-formed record is passed through; anything else is an ordinary error
            // value and gets wrapped, so every FutureError a program sees has a valid kind.
            if (k is { } valid && msg.IsString)
                return new FutureError(valid, msg.AsString, t.GetStr("cause")) { _table = t };
        }
        return new FutureError(FutureErrorKind.Failed, L.ToStringMeta(raised), raised);
    }
}

/// <summary>A value a future completed with.</summary>
public sealed record class Completed(Value Value);

/// <summary>The outcome of a finished future: railway-style, a value or a typed error.</summary>
public union FutureResult(Completed, FutureError);

/// <summary>
/// A Java-style completable future living in one VM. All members must be used on the VM's
/// thread, except <see cref="CompleteFromAnyThread"/> / <see cref="FailFromAnyThread"/>.
/// </summary>
public sealed class LuaFuture : IUserdata
{
    private readonly LuaState _L;
    private readonly int _epoch;
    private List<Action<LuaFuture>>? _callbacks;

    public LuaFuture(LuaState state) { _L = state; _epoch = state.Scheduler.Epoch; }

    public string TypeName => "Future";
    public Table? Metatable { get => _L.FutureMeta; set { } }

    public bool IsDone { get; private set; }
    /// <summary>The value (when completed normally).</summary>
    public Value Value { get; private set; }
    /// <summary>The error (when completed exceptionally), else null.</summary>
    public FutureError? Error { get; private set; }

    /// <summary>The outcome, or null while the future is still pending.</summary>
    public FutureResult? Result => !IsDone ? null : Error is { } e ? e : new Completed(Value);

    /// <summary>Completes with a value. Returns false if the future was already done.</summary>
    public bool Complete(Value value)
    {
        if (IsDone) return false;
        Value = value;
        Finish();
        return true;
    }

    /// <summary>Completes on the error track. Returns false if the future was already done.</summary>
    public bool Fail(FutureError error)
    {
        if (IsDone) return false;
        Error = error;
        Finish();
        return true;
    }

    private void Finish()
    {
        IsDone = true;
        var cbs = _callbacks;
        _callbacks = null;
        if (cbs == null) return;
        foreach (var cb in cbs) _L.Scheduler.Post(() => cb(this));
    }

    /// <summary>Runs <paramref name="callback"/> on the scheduler once the future is done.</summary>
    public void OnComplete(Action<LuaFuture> callback)
    {
        if (IsDone) _L.Scheduler.Post(() => callback(this));
        else (_callbacks ??= new List<Action<LuaFuture>>()).Add(callback);
    }

    /// <summary>Thread-safe, run-owned completion. A completion from before an abort/reset is ignored.</summary>
    public void CompleteFromAnyThread(Value value) => _L.Scheduler.PostExternal(() =>
    {
        if (_L.Scheduler.Epoch == _epoch) Complete(value);
    });
    /// <summary>Thread-safe failure with the same run ownership as <see cref="CompleteFromAnyThread"/>.</summary>
    public void FailFromAnyThread(FutureError error) => _L.Scheduler.PostExternal(() =>
    {
        if (_L.Scheduler.Epoch == _epoch) Fail(error);
    });
}

/// <summary>
/// The VM's cooperative scheduler: a FIFO queue of ready work, timers, and a thread-safe inbox
/// for completions arriving from other threads. Everything runs on the VM's own thread.
/// </summary>
public sealed partial class Scheduler
{
    private readonly Queue<Action> _ready = new();
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly PriorityQueue<Action, (long Due, long Seq)> _timers = new();
    private readonly SemaphoreSlim _signal = new(0);
    private long _seq;
    private int _externalPending;
    /// <summary>Called while waiting, so that a host abort also interrupts an idle wait.</summary>
    internal Action? Poll;

    /// <summary>Queues work to run on the VM thread (call from the VM thread).</summary>
    public void Post(Action work)
    {
        _ready.Enqueue(work);
        NotifyHost();
    }

    /// <summary>Queues work from any thread.</summary>
    public void PostExternal(Action work)
    {
        if (Volatile.Read(ref _shutdown)) return;
        _inbox.Enqueue(work);
        if (Volatile.Read(ref _shutdown))
        {
            while (_inbox.TryDequeue(out _)) { }
            return;
        }
        _signal.Release();
        NotifyHost();
    }

    /// <summary>Announces a host operation that will later call <see cref="PostExternal"/>;
    /// blocking waits keep waiting while any are outstanding. Pair with <see cref="EndExternal"/>.</summary>
    /// Returns the scheduler's epoch, to be passed back to <see cref="EndExternal"/>.
    public int BeginExternal()
    {
        Interlocked.Increment(ref _externalPending);
        return Epoch;
    }

    /// <summary>Ends an operation announced with <see cref="BeginExternal"/> (call on the VM
    /// thread, from the posted completion). False if the run that started it has been aborted
    /// since: its result must then be dropped.</summary>
    public bool EndExternal(int epoch)
    {
        if (epoch != Epoch) return false;
        Interlocked.Decrement(ref _externalPending);
        return true;
    }

    /// <summary>Incremented by <see cref="Clear"/>: completions of an earlier epoch are stale.</summary>
    public int Epoch { get; private set; }

    private CancellationTokenSource _lifetime = new();
    /// <summary>Cancelled when the current run is aborted; host I/O started for it should observe it.</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    public void AddTimer(double seconds, Action work)
    {
        long due = Stopwatch.GetTimestamp() + (long)(Math.Max(seconds, 0) * Stopwatch.Frequency);
        _timers.Enqueue(work, (due, _seq++));
        NotifyHost();
    }

    private void Drain()
    {
        while (_inbox.TryDequeue(out Action? a)) _ready.Enqueue(a);
        long now = Stopwatch.GetTimestamp();
        while (_timers.TryPeek(out Action? t, out var key) && key.Due <= now)
        {
            _timers.Dequeue();
            _ready.Enqueue(t);
        }
    }

    /// <summary>True if nothing is queued, timed or outstanding: no further progress is possible.</summary>
    public bool IsIdle => _ready.Count == 0 && _inbox.IsEmpty && _timers.Count == 0 && Volatile.Read(ref _externalPending) == 0;

    /// <summary>Runs one unit of work, waiting for a timer or an external completion if needed.
    /// Returns false when the scheduler is idle.</summary>
    public bool RunOne(long deadline = long.MaxValue)
    {
        while (true)
        {
            Poll?.Invoke();
            Drain();
            if (_ready.TryDequeue(out Action? work))
            {
                work();
                return true;
            }
            if (IsIdle) return false;
            long now = Stopwatch.GetTimestamp();
            if (now >= deadline) return false;
            long until = _timers.TryPeek(out _, out var key) ? Math.Min(key.Due, deadline) : deadline;
            int ms = until == long.MaxValue ? 50 : (int)Math.Clamp((until - now) * 1000 / Stopwatch.Frequency + 1, 1, 50);
            _signal.Wait(ms);
        }
    }

    /// <summary>Runs the work that is ready now (and nothing that becomes ready meanwhile),
    /// without waiting. Returns how many units ran.</summary>
    public int RunReady()
    {
        Poll?.Invoke();
        Drain();
        int n = _ready.Count;
        for (int i = 0; i < n && _ready.TryDequeue(out Action? work); i++) work();
        return n;
    }

    /// <summary>Runs work until <paramref name="done"/> holds; false if the scheduler went idle
    /// (or the deadline passed) first.</summary>
    public bool RunUntil(Func<bool> done, long deadline = long.MaxValue)
    {
        while (!done())
            if (!RunOne(deadline)) return done();
        return true;
    }

    /// <summary>Number of asynchronous tasks that have been started and have not finished.</summary>
    public int LiveTasks { get; internal set; }

    /// <summary>What a program does when its main chunk returns: keeps running while started
    /// tasks are unfinished and can still make progress. A timer nobody is waiting on (an
    /// abandoned <c>Future.delay</c>) does not keep the program alive.</summary>
    public void RunPendingTasks()
    {
        while (LiveTasks > 0 && RunOne()) { }
        // finish callbacks that are already due, without waiting for anything
        Drain();
        while (_ready.TryDequeue(out Action? work))
        {
            work();
            Drain();
        }
    }

    /// <summary>Drops all queued work, timers and tasks (used after a run was aborted, whose
    /// tasks must not carry over into the next run).</summary>
    public void Clear()
    {
        _ready.Clear();
        _timers.Clear();
        while (_inbox.TryDequeue(out _)) { }
        LiveTasks = 0;
        Epoch++;
        Volatile.Write(ref _externalPending, 0);
        CancellationTokenSource ended = _lifetime;
        _lifetime = new CancellationTokenSource();
        if (_shutdown) _lifetime.Cancel();
        try { ended.Cancel(); }
        catch (AggregateException error) { Trace.TraceError("Cameo.Runtime run cancellation callback failed: {0}", error); }
        finally { ended.Dispose(); }
        NotifyHost();
    }

    /// <summary>Runs until there is nothing left to do.</summary>
    public void RunUntilIdle()
    {
        while (RunOne()) { }
    }
}
