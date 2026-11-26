using System.Diagnostics;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;

namespace Cameo.Runtime;

// Railway-style entry points: a run never throws for an expected outcome; it returns a
// RunResult that the caller switches over.

/// <summary>The program ran to completion and returned these values.</summary>
public sealed record class RunCompleted(Value[] Values);
/// <summary>The source did not parse or type-check.</summary>
public sealed record class CompileFailed(IReadOnlyList<Diagnostic> Diagnostics);
/// <summary>The program raised an error that nothing caught.</summary>
public sealed record class RuntimeFailed(Value Error, string Message, string? Traceback);
/// <summary>The host stopped the program (watchdog timeout, cancellation or memory limit).</summary>
public sealed record class RunAborted(AbortReason Reason);
/// <summary>The program called <c>os.exit</c>.</summary>
public sealed record class RunExited(int Code);

/// <summary>Every way a run can end.</summary>
public union RunResult(RunCompleted, CompileFailed, RuntimeFailed, RunAborted, RunExited);

public sealed partial class ScriptEngine
{
    /// <summary>Runs a source file; never throws for compile errors, runtime errors, aborts or exit.</summary>
    public RunResult TryRunFile(string path, params string[] scriptArgs) => Guard(() => RunFile(path, scriptArgs));

    /// <summary>Runs source text; never throws for compile errors, runtime errors, aborts or exit.</summary>
    public RunResult TryRunString(string source, string chunkName = "chunk") => Guard(() => RunString(source, chunkName));

    /// <summary>Calls a function of the program (for example a global the script defined) under the
    /// same limits as a run; never throws for runtime errors, aborts or exit.</summary>
    public RunResult TryCall(Value function, params Value[] args) => Guard(() =>
    {
        using var watchdog = new Watchdog(State, Options);
        try
        {
            Value[] results = State.Call(function, args);
            State.Scheduler.RunPendingTasks();
            return results;
        }
        catch (LuaAbortException)
        {
            State.Scheduler.Clear();
            State.DropPendingWorkers();
            throw;
        }
    });

    /// <summary>Starts a function of the program as a task and returns at once with the future of
    /// its result. The task runs when the host calls <see cref="TryPump"/> (or runs or calls
    /// something else): a host with its own loop can keep several script tasks going this way.</summary>
    public LuaFuture Start(Value function, params Value[] args) => Stdlib.FutureLib.StartTask(State, function, args);

    /// <summary>Waits without polling for work, a timer deadline or a scheduler change.
    /// Call on the VM owner thread and resume pumping on that same owner/context. This
    /// neither executes scripts nor changes the cooperative budget of <see cref="TryPump"/>.</summary>
    public Task WaitForWorkAsync(CancellationToken cancellationToken = default) =>
        State.Scheduler.WaitForWorkAsync(cancellationToken);

    /// <summary>Runs the initial ready batch without waiting, then services work while
    /// <paramref name="budget"/> permits (zero: only the initial batch). The initial batch
    /// is not time-bounded and an executing callback is never preempted by this budget.
    /// <paramref name="idle"/> is true when nothing is left to do.</summary>
    public RunResult TryPump(TimeSpan budget, out bool idle)
    {
        RunResult result = Guard(() =>
        {
            using var watchdog = new Watchdog(State, Options);
            try
            {
                long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
                // what is ready runs whatever the budget; then wait for timers and I/O within it
                State.Scheduler.RunReady();
                while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline && State.Scheduler.RunOne(deadline)) { }
                return [];
            }
            catch (LuaAbortException)
            {
                State.Scheduler.Clear();
                State.DropPendingWorkers();
                throw;
            }
        });
        idle = State.Scheduler.IsIdle;
        return result;
    }


    public void Cancel() => State.RequestAbort(AbortReason.Cancelled);

    // Runs a compiled program under the watchdog, then lets the asynchronous tasks it started finish.
    private Value[] RunWatched(Proto p, ReadOnlySpan<Value> args)
    {
        using var watchdog = new Watchdog(State, Options);
        try
        {
            Value[] results = State.Run(p, args);
            State.Scheduler.RunPendingTasks();
            return results;
        }
        catch (LuaAbortException)
        {
            // an aborted run leaves nothing behind: its queued tasks and timers are dropped
            State.Scheduler.Clear();
            State.DropPendingWorkers();
            throw;
        }
    }

    private RunResult Guard(Func<Value[]> run)
    {
        try
        {
            return new RunCompleted(run());
        }
        catch (CameoCompileException e)
        {
            return new CompileFailed(e.Diagnostics);
        }
        catch (LuaException e)
        {
            return new RuntimeFailed(e.Value, e.Message, e.Traceback);
        }
        catch (LuaAbortException e)
        {
            return new RunAborted(e.Reason);
        }
        catch (LuaExitException e)
        {
            return new RunExited(e.Code);
        }
    }

    /// <summary>
    /// The watchdog: while a program runs, a thread checks the time and memory limits from
    /// <see cref="CameoRuntimeOptions"/> and requests an abort when one is exceeded.
    /// </summary>
    private sealed class Watchdog : IDisposable
    {
        private readonly ManualResetEventSlim? _stop;

        public Watchdog(LuaState state, CameoRuntimeOptions options)
        {
            state.ClearAbort();
            if (options.Timeout is not { } && options.MaxMemoryBytes == null) return;
            long started = Stopwatch.GetTimestamp();
            var stop = _stop = new ManualResetEventSlim();
            // A thread of its own rather than a timer: timers fire on the thread pool, and a
            // program (or host) that keeps the pool busy must not be able to delay its watchdog.
            new Thread(() =>
            {
                while (!stop.Wait(10))
                {
                    if (options.Timeout is { } limit && Stopwatch.GetElapsedTime(started) >= limit)
                        state.RequestAbort(AbortReason.Timeout);
                    else if (options.MaxMemoryBytes is { } max && GC.GetTotalMemory(false) > max)
                        state.RequestAbort(AbortReason.MemoryLimit);
                }
                stop.Dispose();
            }) { IsBackground = true, Name = "tlvm watchdog" }.Start();
        }

        public void Dispose() => _stop?.Set();
    }
}
