using System.Diagnostics;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// <c>Future</c>: Java-style completable futures (supplyAsync, thenApply, thenCompose, allOf,
/// get, ...) on the VM's cooperative scheduler.
///
/// Concurrency model: asynchronous functions run as tasks (coroutines) on the VM's own thread,
/// interleaved at the points where they wait on a future; nothing runs in parallel inside one
/// VM. Parallelism comes from <c>Future.parallel</c>, which runs a function in a worker VM on
/// another thread (values are copied across, never shared), and from operations completed on
/// other threads: the <c>aio</c> library and the host (<see cref="LuaState.FutureFromTask"/>,
/// <see cref="LuaFuture.CompleteFromAnyThread"/>).
///
/// Errors are typed and travel on the failure track: every failed future carries a
/// <c>FutureError</c> record (kind = "failed" | "cancelled" | "timeout" | "deadlock", message,
/// cause). <c>await</c> returns <c>value, err</c> for future outcomes; <c>get</c>/<c>join</c> are
/// the throwing convenience on top and raise that same record.
/// </summary>
public static class FutureLib
{
    // The marker belongs to the registered identity, never to a field name or a bound wrapper.
    private static void RegisterInstance(Table table, string name, NativeFn function) =>
        table.SetStr(name, Value.FromFunction(new NativeFunction(name, function) { FutureInstanceMethod = true }));

    public static void Open(LuaState L)
    {
        var f = new Table();
        L.Register(f, "new", static (ref CallContext c) => Ret(ref c, new LuaFuture(c.L)));
        L.Register(f, "completedFuture", static (ref CallContext c) =>
        {
            var fut = new LuaFuture(c.L);
            fut.Complete(c[0]);
            return Ret(ref c, fut);
        });
        L.Register(f, "failedFuture", static (ref CallContext c) =>
        {
            var fut = new LuaFuture(c.L);
            fut.Fail(FutureError.FromRaised(c.L, c[0]));
            return Ret(ref c, fut);
        });
        L.Register(f, "supplyAsync", static (ref CallContext c) =>
        {
            Value fn = c.CheckFunction(0);
            var fut = new LuaFuture(c.L);
            Spawn(c.L, fut, fn, c.ArgsFrom(1), keepResult: true);
            return Ret(ref c, fut);
        });
        L.Register(f, "runAsync", static (ref CallContext c) =>
        {
            Value fn = c.CheckFunction(0);
            var fut = new LuaFuture(c.L);
            Spawn(c.L, fut, fn, c.ArgsFrom(1), keepResult: false);
            return Ret(ref c, fut);
        });
        L.Register(f, "delay", static (ref CallContext c) =>
        {
            double seconds = c.CheckNumber(0);
            Value value = c.Count > 1 ? c[1] : Value.True;
            var fut = new LuaFuture(c.L);
            c.L.Scheduler.AddTimer(seconds, () => fut.Complete(value));
            return Ret(ref c, fut);
        });
        L.Register(f, "parallel", Parallel);
        L.Register(f, "processors", static (ref CallContext c) => c.Return(Environment.ProcessorCount));
        L.Register(f, "allOf", AllOf);
        L.Register(f, "anyOf", AnyOf);

        RegisterInstance(f, "await", Await);
        RegisterInstance(f, "get", static (ref CallContext c) => Wait(ref c, throws: true));
        RegisterInstance(f, "join", static (ref CallContext c) => Wait(ref c, throws: true));
        RegisterInstance(f, "getNow", static (ref CallContext c) =>
        {
            LuaFuture fut = Check(ref c, 0);
            if (!fut.IsDone) return c.Return(c[1]);
            if (fut.Error is { } e) throw new LuaException(e.ToValue(c.L), true);
            return c.Return(fut.Value);
        });
        RegisterInstance(f, "isDone", static (ref CallContext c) => c.Return(Check(ref c, 0).IsDone));
        RegisterInstance(f, "isCompletedExceptionally", static (ref CallContext c) => c.Return(Check(ref c, 0).Error != null));
        RegisterInstance(f, "isCancelled", static (ref CallContext c) => c.Return(Check(ref c, 0).Error is { Kind: FutureErrorKind.Cancelled }));
        RegisterInstance(f, "complete", static (ref CallContext c) => c.Return(Check(ref c, 0).Complete(c[1])));
        RegisterInstance(f, "completeExceptionally", static (ref CallContext c) => c.Return(Check(ref c, 0).Fail(FutureError.FromRaised(c.L, c[1]))));
        RegisterInstance(f, "cancel", static (ref CallContext c) => c.Return(Check(ref c, 0).Fail(new FutureError(FutureErrorKind.Cancelled, "cancelled"))));

        RegisterInstance(f, "thenApply", static (ref CallContext c) => Then(ref c, ThenKind.Apply));
        RegisterInstance(f, "thenAccept", static (ref CallContext c) => Then(ref c, ThenKind.Accept));
        RegisterInstance(f, "thenRun", static (ref CallContext c) => Then(ref c, ThenKind.Run));
        RegisterInstance(f, "thenCompose", static (ref CallContext c) => Then(ref c, ThenKind.Compose));
        RegisterInstance(f, "exceptionally", static (ref CallContext c) => Then(ref c, ThenKind.Exceptionally));
        RegisterInstance(f, "handle", static (ref CallContext c) => Then(ref c, ThenKind.Handle));
        RegisterInstance(f, "whenComplete", static (ref CallContext c) => Then(ref c, ThenKind.WhenComplete));
        RegisterInstance(f, "thenCombine", ThenCombine);
        RegisterInstance(f, "orTimeout", static (ref CallContext c) => Ret(ref c, WithTimeout(c.L, Check(ref c, 0), c.CheckNumber(1), null)));
        RegisterInstance(f, "completeOnTimeout", static (ref CallContext c) => Ret(ref c, WithTimeout(c.L, Check(ref c, 0), c.CheckNumber(2), c[1])));

        var sandbox = new Table();
        L.Register(sandbox, "run", Sandbox);
        L.Globals.SetStr("sandbox", sandbox);
        L.LoadedModules.SetStr("sandbox", sandbox);

        L.Globals.SetStr("Future", f);
        L.LoadedModules.SetStr("Future", f);

        Table mt = L.FutureMeta;
        mt.SetStr("__index", f);
        mt.SetStr("__name", "Future");
        L.Register(mt, "__tostring", static (ref CallContext c) =>
        {
            LuaFuture fut = Check(ref c, 0);
            string state = !fut.IsDone ? "pending" : fut.Error is { } e ? FutureError.KindName(e.Kind) : "completed";
            return c.Return($"Future({state})");
        });

        Table emt = L.FutureErrorMeta;
        emt.SetStr("__name", "FutureError");
        L.Register(emt, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"FutureError({t.GetStr("kind")}): {t.GetStr("message")}");
        });
    }

    private static LuaFuture Check(ref CallContext c, int i) =>
        c[i].O as LuaFuture ?? throw c.TypeError(i, "Future");

    private static int Ret(ref CallContext c, LuaFuture f) => c.Return(Value.FromUserdata(f));

    // ------------------------------------------------------------------ tasks

    /// <summary>Runs fn(args) as a task; its result (or typed error) settles <paramref name="target"/>.</summary>
    /// <summary>Schedules a function as a task and returns the future of its (first) result.
    /// Nothing runs until the scheduler does.</summary>
    internal static LuaFuture StartTask(LuaState L, Value fn, Value[] args)
    {
        var fut = new LuaFuture(L);
        Spawn(L, fut, fn, args, keepResult: true, flatten: true);
        return fut;
    }

    private static void Spawn(LuaState L, LuaFuture target, Value fn, Value[] args, bool keepResult, bool flatten = false)
    {
        LuaThread co = L.NewThread(fn);
        co.IsTask = true;
        L.Scheduler.LiveTasks++;
        L.Scheduler.Post(() => Step(L, co, target, args, default, false, keepResult, flatten));
    }

    // Resumes a task and, when it stops, either settles its future or parks it on the future it awaits.
    private static void Step(LuaState L, LuaThread co, LuaFuture target, Value[] args, Value error, bool raise, bool keepResult, bool flatten)
    {
        if (target.IsDone && co.Status == CoStatus.Suspended && !co.Started)
        {
            L.Scheduler.LiveTasks--; // cancelled before it started
            return;
        }
        bool ok = raise ? L.ResumeWithError(co, error, out Value[] results) : L.Resume(co, args, out results);
        if (co.Status == CoStatus.Dead)
        {
            L.Scheduler.LiveTasks--;
            if (!ok) target.Fail(FutureError.FromRaised(L, results.Length > 0 ? results[0] : Value.Nil));
            else
            {
                Value v = keepResult && results.Length > 0 ? results[0] : Value.Nil;
                if (flatten && v.O is LuaFuture inner) Mirror(inner, target, L);
                else target.Complete(v);
            }
            return;
        }
        // The task yielded: it is waiting on a future, or simply giving others a turn.
        LuaFuture? awaited = co.Awaiting;
        co.Awaiting = null;
        if (awaited == null)
        {
            L.Scheduler.Post(() => Step(L, co, target, Array.Empty<Value>(), default, false, keepResult, flatten));
            return;
        }
        bool throws = co.AwaitThrows;
        awaited.OnComplete(done =>
        {
            if (done.Error is { } e)
            {
                if (throws) Step(L, co, target, Array.Empty<Value>(), e.ToValue(L), true, keepResult, flatten);
                else Step(L, co, target, new[] { Value.Nil, e.ToValue(L) }, default, false, keepResult, flatten);
            }
            else Step(L, co, target, new[] { done.Value }, default, false, keepResult, flatten);
        });
    }

    private static void Mirror(LuaFuture from, LuaFuture to, LuaState L) =>
        from.OnComplete(d =>
        {
            if (d.Error is { } e) to.Fail(e); else to.Complete(d.Value);
        });

    // ------------------------------------------------------------------ parallel workers

    // How a worker's run ended.
    private sealed record class WorkerReturned(Value Value);
    private sealed record class WorkerRaised(Value Error);
    private sealed record class WorkerAborted(AbortReason Reason);
    private sealed record class WorkerCrashed(string Message);
    private union WorkerOutcome(WorkerReturned, WorkerRaised, WorkerAborted, WorkerCrashed);

    // Future.parallel(fn, ...): runs fn(...) in a VM of its own on another thread. The function,
    // what it captures and its arguments are copied into that VM and its result is copied back
    // (see ValueTransfer), so the two VMs never share mutable state.
    private static int Parallel(ref CallContext c)
    {
        Value fn = c.CheckFunction(0);
        Value[] args = c.ArgsFrom(1);
        LuaState L = c.L;
        var fut = new LuaFuture(L);

        LuaState W = L.NewWorker();
        var send = new ValueTransfer(L, W);
        string? problem = null;
        if (!send.TryCopy(fn, out Value wfn, out string? err)) problem = $"function: {err}";
        var wargs = new Value[args.Length];
        for (int i = 0; i < args.Length && problem == null; i++)
            if (!send.TryCopy(args[i], out wargs[i], out err)) problem = $"argument {i + 1}: {err}";
        if (problem != null)
        {
            L.WorkerFinished(W);
            fut.Fail(new FutureError(FutureErrorKind.Failed, "Future.parallel: " + problem));
            return Ret(ref c, fut);
        }

        Launch(L, W, fut, wfn, wargs, timeoutSeconds: -1);
        return Ret(ref c, fut);
    }

    // Queues the worker and settles the future with its outcome. A positive timeout stops the
    // worker when it passes (the future then fails with kind "timeout").
    private static void Launch(LuaState L, LuaState W, LuaFuture fut, Value wfn, Value[] wargs, double timeoutSeconds)
    {
        Scheduler s = L.Scheduler;
        int epoch = s.BeginExternal();
        s.LiveTasks++;
        // cancelling (or otherwise settling) the future stops the worker
        fut.OnComplete(_ => W.RequestAbort(AbortReason.Cancelled));
        L.StartWorker(W, () =>
        {
            // the clock starts when the worker starts running, not while it waits for a slot
            using Timer? limit = timeoutSeconds > 0
                ? new Timer(_ => W.RequestAbort(AbortReason.Timeout), null, TimeSpan.FromSeconds(Math.Min(timeoutSeconds, 1e6)), Timeout.InfiniteTimeSpan)
                : null;
            WorkerOutcome outcome = RunWorker(W, wfn, wargs);
            s.PostExternal(() =>
            {
                if (!s.EndExternal(epoch)) return;
                s.LiveTasks--;
                Settle(L, W, fut, outcome);
            });
        });
    }

    // sandbox.run(source [, {allow = {...}, timeout =, args = {...}, quiet =}]): compiles and
    // runs a piece of code in a VM of its own with no more rights than it is given. `allow`
    // lists the permissions to pass on ("net", "run", "db"); each must be one this VM has.
    // The code receives `args` as `...`, and what it returns settles the future. Values are
    // copied in and out, as with Future.parallel.
    private static int Sandbox(ref CallContext c)
    {
        string source = c.CheckString(0);
        LuaState L = c.L;
        var fut = new LuaFuture(L);
        bool net = false, run = false, db = false, quiet = false, isolated = false;
        double timeout = -1;
        Value[] args = Array.Empty<Value>();
        if (!c.IsNoneOrNil(1))
        {
            Table o = c.CheckTable(1);
            if (o.GetStr("allow").O is Table allow)
            {
                for (int i = 1; i <= allow.Length; i++)
                {
                    string name = allow.GetInt(i).IsString ? allow.GetInt(i).AsString : "";
                    switch (name)
                    {
                        case "net": net = true; break;
                        case "run": run = true; break;
                        case "db": db = true; break;
                        default: throw c.ArgError(1, $"unknown permission '{name}' in 'allow' (permissions are \"net\", \"run\", \"db\")");
                    }
                }
            }
            if (o.GetStr("timeout").IsNumber) timeout = o.GetStr("timeout").AsDouble;
            long? maxSteps = null;
            if (o.GetStr("maxSteps").IsNumber)
            {
                long s = (long)o.GetStr("maxSteps").AsDouble;
                if (s <= 0) throw c.ArgError(1, "field 'maxSteps' must be a positive integer");
                maxSteps = s;
            }
            quiet = o.GetStr("quiet").IsTruthy;
            isolated = o.GetStr("isolated").IsTruthy;
            if (o.GetStr("args").O is Table list) args = Enumerable.Range(1, list.Length).Select(i => list.GetInt(i)).ToArray();
            // Apply maxSteps below after NewWorker creates W (narrowing any inherited parent step budget).
            _ = maxSteps;
        }
        // a sandbox can only narrow: asking for a right this VM lacks is refused, not ignored
        string? refused = net && !L.AllowNetwork ? "net" : run && !L.AllowProcesses ? "run" : db && !L.AllowDatabase ? "db" : null;
        if (refused != null)
        {
            fut.Fail(new FutureError(FutureErrorKind.Failed, $"sandbox.run: cannot grant \"{refused}\": this VM does not have that permission itself"));
            return Ret(ref c, fut);
        }
        if (isolated && (net || run || db))
        {
            fut.Fail(new FutureError(FutureErrorKind.Failed, "sandbox.run: isolated sandboxes cannot grant external permissions"));
            return Ret(ref c, fut);
        }
        LuaState W = L.NewWorker(isolated);
        if (!c.IsNoneOrNil(1) && c[1].O is Table optsForSteps && optsForSteps.GetStr("maxSteps").IsNumber)
        {
            long s = (long)optsForSteps.GetStr("maxSteps").AsDouble;
            W.StepBudget = W.StepBudget > 0 ? Math.Min(W.StepBudget, s) : s;
            W.ResetStepBudget();
        }
        W.AllowNetwork = net;
        W.AllowProcesses = run;
        W.AllowDatabase = db;
        if (quiet) { W.Stdout = TextWriter.Null; W.Stderr = TextWriter.Null; W.StdoutBytes = null; W.StderrBytes = null; }

        string? problem = null;
        Value wfn = default;
        if (W.ChunkLoader == null) problem = "this VM cannot compile code at run time";
        else
        {
            try { wfn = W.ChunkLoader(W, source, "=sandbox"); }
            catch (LuaException e) { problem = e.Message; }
        }
        var send = new ValueTransfer(L, W);
        var wargs = new Value[args.Length];
        for (int i = 0; i < args.Length && problem == null; i++)
            if (!send.TryCopy(args[i], out wargs[i], out string? err)) problem = $"argument {i + 1}: {err}";
        if (problem != null)
        {
            L.WorkerFinished(W);
            fut.Fail(new FutureError(FutureErrorKind.Failed, "sandbox.run: " + problem));
            return Ret(ref c, fut);
        }
        Launch(L, W, fut, wfn, wargs, timeout);
        return Ret(ref c, fut);
    }

    // On the worker's thread: nothing here may touch the parent VM.
    private static WorkerOutcome RunWorker(LuaState W, Value fn, Value[] args)
    {
        try
        {
            Value[] results = W.Call(fn, args);
            Value result = results.Length > 0 ? results[0] : Value.Nil;
            // a worker that returns one of its own futures stands for that future's outcome
            if (result.O is LuaFuture inner)
            {
                W.Scheduler.RunUntil(() => inner.IsDone);
                if (!inner.IsDone)
                    return new WorkerRaised(new FutureError(FutureErrorKind.Deadlock, "nothing left to run that could complete this future").ToValue(W));
                if (inner.Error is { } e) return new WorkerRaised(e.ToValue(W));
                result = inner.Value;
            }
            W.Scheduler.RunPendingTasks();
            return new WorkerReturned(result);
        }
        catch (LuaException e)
        {
            return new WorkerRaised(e.Value);
        }
        catch (LuaAbortException e)
        {
            return new WorkerAborted(e.Reason);
        }
        catch (LuaExitException)
        {
            return new WorkerCrashed("os.exit was called in a parallel worker");
        }
        catch (Exception e)
        {
            return new WorkerCrashed(LuaText.Encode(e.Message)); // a VM bug must not take the host down
        }
    }

    // Back on the parent's thread; the worker has stopped, so its values can be read freely.
    private static void Settle(LuaState L, LuaState W, LuaFuture fut, WorkerOutcome outcome)
    {
        if (fut.IsDone) return;
        var receive = new ValueTransfer(W, L);
        switch (outcome)
        {
            case WorkerReturned r:
                if (receive.TryCopy(r.Value, out Value v, out string? err)) fut.Complete(v);
                else fut.Fail(new FutureError(FutureErrorKind.Failed, "Future.parallel: result: " + err));
                break;
            case WorkerRaised r:
                // an error value that cannot cross is reported by its text
                Value raised = receive.TryCopy(r.Error, out Value copy, out _) ? copy : Value.FromString(W.ToStringMeta(r.Error));
                fut.Fail(FutureError.FromRaised(L, raised));
                break;
            case WorkerAborted a:
                fut.Fail(a.Reason switch
                {
                    AbortReason.Timeout => new FutureError(FutureErrorKind.Timeout, "timed out"),
                    AbortReason.MemoryLimit => new FutureError(FutureErrorKind.Cancelled, "memory limit exceeded"),
                    AbortReason.StepLimit => new FutureError(FutureErrorKind.Cancelled, "step limit exceeded"),
                    _ => new FutureError(FutureErrorKind.Cancelled, "cancelled"),
                });
                break;
            case WorkerCrashed x:
                fut.Fail(new FutureError(FutureErrorKind.Failed, x.Message));
                break;
        }
    }

    // ------------------------------------------------------------------ waiting

    // await (returns value, err) / get, join (raise the FutureError). Inside a task the task is
    // suspended; elsewhere the scheduler is run until the future is done.
    private static int Await(ref CallContext c)
    {
        LuaFuture source = Check(ref c, 0);
        bool cancel = false;
        if (!c.IsNoneOrNil(2))
        {
            if (!c[2].IsBoolean) throw c.TypeError(2, "boolean");
            cancel = c[2].IsTruthy;
        }
        if (c.IsNoneOrNil(1))
        {
            if (cancel) throw c.ArgError(1, "cancelOnTimeout requires a timeout");
            return WaitFor(ref c, source, throws: false, timeout: -1);
        }

        double seconds = c.CheckNumber(1);
        // Bound scheduler tick conversion and its intermediate millisecond arithmetic.
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > int.MaxValue / 1000.0)
            throw c.ArgError(1, "timeout must be finite and between 0 and 2147483.647 seconds");
        if (source.IsDone) return WaitFor(ref c, source, throws: false, timeout: -1);

        var deadline = new LuaFuture(c.L);
        Mirror(source, deadline, c.L);
        c.L.Scheduler.AddTimer(seconds, () =>
        {
            // The source may have settled while its queued mirror callback is still pending.
            if (source.IsDone)
            {
                if (source.Error is { } error) deadline.Fail(error);
                else deadline.Complete(source.Value);
            }
            else if (deadline.Fail(new FutureError(FutureErrorKind.Timeout, "timed out")) && cancel)
            {
                source.Fail(new FutureError(FutureErrorKind.Cancelled, "cancelled"));
            }
        });
        return WaitFor(ref c, deadline, throws: false, timeout: -1);
    }

    private static int Wait(ref CallContext c, bool throws)
    {
        LuaFuture fut = Check(ref c, 0);
        double timeout = throws && !c.IsNoneOrNil(1) ? c.CheckNumber(1) : -1;
        return WaitFor(ref c, fut, throws, timeout);
    }

    /// <summary>Waits for <paramref name="fut"/> on behalf of the running native function
    /// (which must return what this returns): suspends the task, or runs the scheduler.</summary>
    internal static int WaitFor(ref CallContext c, LuaFuture fut, bool throws, double timeout)
    {
        LuaState L = c.L;
        if (!fut.IsDone)
        {
            LuaThread th = c.Thread;
            // Suspend the task only when this function was called straight from task code; when it
            // is reached through another native (pcall(f.get, f), a sort comparator, ...) the task
            // cannot yield through that native, so wait by running the scheduler instead.
            if (th.IsTask && th.ExecDepth == 1 && L.DirectNativeCall)
            {
                th.Awaiting = timeout >= 0 ? WithTimeout(L, fut, timeout, null) : fut;
                th.AwaitThrows = throws;
                return c.YieldNone();
            }
            long deadline = timeout >= 0 ? Stopwatch.GetTimestamp() + (long)(timeout * Stopwatch.Frequency) : long.MaxValue;
            if (!L.Scheduler.RunUntil(() => fut.IsDone, deadline))
            {
                FutureError err = timeout >= 0 && Stopwatch.GetTimestamp() >= deadline
                    ? new FutureError(FutureErrorKind.Timeout, "timed out")
                    : new FutureError(FutureErrorKind.Deadlock, "nothing left to run that could complete this future");
                if (throws) throw new LuaException(err.ToValue(L), true);
                return c.Return(Value.Nil, err.ToValue(L));
            }
        }
        if (fut.Error is { } e)
        {
            if (throws) throw new LuaException(e.ToValue(L), true);
            return c.Return(Value.Nil, e.ToValue(L));
        }
        return throws ? c.Return(fut.Value) : c.Return(fut.Value, Value.Nil);
    }

    private static LuaFuture WithTimeout(LuaState L, LuaFuture source, double seconds, Value? fallback)
    {
        var result = new LuaFuture(L);
        Mirror(source, result, L);
        L.Scheduler.AddTimer(seconds, () =>
        {
            if (fallback is { } v) result.Complete(v);
            else result.Fail(new FutureError(FutureErrorKind.Timeout, "timed out"));
        });
        return result;
    }

    // ------------------------------------------------------------------ combinators

    private enum ThenKind { Apply, Accept, Run, Compose, Exceptionally, Handle, WhenComplete }

    private static int Then(ref CallContext c, ThenKind kind)
    {
        LuaFuture source = Check(ref c, 0);
        Value fn = c.CheckFunction(1);
        LuaState L = c.L;
        var result = new LuaFuture(L);
        source.OnComplete(done =>
        {
            FutureError? err = done.Error;
            switch (kind)
            {
                case ThenKind.Apply:
                case ThenKind.Accept:
                case ThenKind.Run:
                case ThenKind.Compose:
                    if (err != null) { result.Fail(err); return; }
                    Spawn(L, result, fn, kind == ThenKind.Run ? Array.Empty<Value>() : new[] { done.Value },
                        keepResult: kind is ThenKind.Apply or ThenKind.Compose, flatten: kind == ThenKind.Compose);
                    return;
                case ThenKind.Exceptionally:
                    if (err == null) { result.Complete(done.Value); return; }
                    Spawn(L, result, fn, new[] { err.ToValue(L) }, keepResult: true);
                    return;
                case ThenKind.Handle:
                    Spawn(L, result, fn, new[] { done.Value, err?.ToValue(L) ?? Value.Nil }, keepResult: true);
                    return;
                default: // WhenComplete: run the action, then pass the original outcome through
                    {
                        var action = new LuaFuture(L);
                        Spawn(L, action, fn, new[] { done.Value, err?.ToValue(L) ?? Value.Nil }, keepResult: false);
                        action.OnComplete(a =>
                        {
                            if (err != null) result.Fail(err);
                            else if (a.Error is { } ae) result.Fail(ae);
                            else result.Complete(done.Value);
                        });
                        return;
                    }
            }
        });
        return Ret(ref c, result);
    }

    private static int ThenCombine(ref CallContext c)
    {
        LuaFuture a = Check(ref c, 0), b = Check(ref c, 1);
        Value fn = c.CheckFunction(2);
        LuaState L = c.L;
        var result = new LuaFuture(L);
        void Settle(LuaFuture _)
        {
            if (result.IsDone) return;
            if (a.Error is { } ea) { result.Fail(ea); return; }
            if (b.Error is { } eb) { result.Fail(eb); return; }
            if (a.IsDone && b.IsDone) Spawn(L, result, fn, new[] { a.Value, b.Value }, keepResult: true);
        }
        a.OnComplete(Settle);
        b.OnComplete(Settle);
        return Ret(ref c, result);
    }

    // Futures given either as an array table or as separate arguments.
    private static List<LuaFuture> Futures(ref CallContext c)
    {
        List<LuaFuture> list = [with(capacity: 8)];
        if (c.Count == 1 && c[0].IsTable)
        {
            Table t = c[0].AsTable;
            for (int i = 1; i <= t.Length; i++)
                list.Add(t.GetInt(i).O as LuaFuture ?? throw c.ArgError(0, $"element {i} is not a Future"));
        }
        else
        {
            for (int i = 0; i < c.Count; i++) list.Add(Check(ref c, i));
        }
        return list;
    }

    // Completes with the array of all results (in argument order); fails as soon as one fails.
    private static int AllOf(ref CallContext c)
    {
        List<LuaFuture> all = Futures(ref c);
        var result = new LuaFuture(c.L);
        int remaining = all.Count;
        if (remaining == 0) result.Complete(Value.FromTable(new Table()));
        foreach (LuaFuture f in all)
        {
            f.OnComplete(d =>
            {
                if (d.Error is { } e) { result.Fail(e); return; } // Fail is a no-op once settled
                if (--remaining == 0)
                {
                    // built in one go, in argument order, so nil results keep their positions
                    var ordered = new Table(all.Count, 0);
                    ordered.SetRange(0, all.Select(x => x.Value).ToArray());
                    result.Complete(Value.FromTable(ordered));
                }
            });
        }
        return Ret(ref c, result);
    }

    // Settles like the first future to finish (value or error).
    private static int AnyOf(ref CallContext c)
    {
        List<LuaFuture> all = Futures(ref c);
        LuaState L = c.L;
        var result = new LuaFuture(L);
        foreach (LuaFuture f in all) Mirror(f, result, L);
        return Ret(ref c, result);
    }
}
