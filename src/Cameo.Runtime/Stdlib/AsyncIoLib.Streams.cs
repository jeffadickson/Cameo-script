using System.Diagnostics;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// A source of bytes read piece by piece (userdata): a file, an HTTP response body, or a
/// program's output. One read at a time; reads return nil at the end.
/// </summary>
public sealed class LuaStream : IUserdata
{
    private readonly LuaState _state;
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    internal readonly string Target;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start, _end;
    private bool _eof;
    private int _busy, _closed;
    private CancellationTokenRegistration _abort;

    internal LuaStream(LuaState state, Stream stream, string target, IDisposable? owner = null)
    {
        _state = state;
        _stream = stream;
        Target = target;
        _owner = owner;
        // Do not root an otherwise unreachable script stream until the next abort.
        _abort = state.Scheduler.Lifetime.Register(static value =>
        {
            if (((WeakReference<LuaStream>)value!).TryGetTarget(out LuaStream? stream)) stream.Close();
        }, new WeakReference<LuaStream>(this));
    }

    public string TypeName => "Stream";
    public Table? Metatable { get => _state.MetaFor("Stream"); set { } }
    internal bool Closed => Volatile.Read(ref _closed) != 0;

    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _stream.Dispose(); } catch (IOException) { }
        finally
        {
            try { _owner?.Dispose(); }
            finally { _abort.Dispose(); }
        }
    }

    internal void Enter()
    {
        if (Closed) throw new LuaException("the stream is closed");
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new LuaException("a read on this stream has not finished (wait for its future first)");
    }

    internal void Leave() => Volatile.Write(ref _busy, 0);

    // A read that was started and not yet consumed. It survives a cancelled wait (a timeout),
    // so the bytes it delivers later are not lost and two reads never run at once.
    private Task<int>? _read;
    /// <summary>Treat an I/O error as the end (a terminal reports the program's exit that way).</summary>
    internal bool EndOnError;

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_eof) return false;
        if (_read == null)
        {
            if (_start == _end) _start = _end = 0;
            else if (_end == _buffer.Length)
            {
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                else Array.Resize(ref _buffer, _buffer.Length * 2); // one very long line
            }
            _read = _stream.ReadAsync(_buffer.AsMemory(_end), CancellationToken.None).AsTask();
        }
        int n;
        try
        {
            n = await _read.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // _read stays: the next call picks it up
        }
        catch (Exception e) when (EndOnError && e is IOException or ObjectDisposedException)
        {
            n = 0;
        }
        _read = null;
        if (n == 0) { _eof = true; return false; }
        _end += n;
        return true;
    }

    /// <summary>Waits until one of the patterns matches what has arrived, and consumes the
    /// output up to the end of that match. Null when the output ends first.</summary>
    internal async Task<(int Index, string Text, string Before, string?[] Groups)?> ExpectAsync(System.Text.RegularExpressions.Regex[] patterns, long maxLength, CancellationToken ct)
    {
        while (true)
        {
            var subject = new RegexLib.Subject(LuaText.FromBytes(_buffer.AsSpan(_start, _end - _start)));
            System.Text.RegularExpressions.Match? best = null;
            int which = -1;
            for (int i = 0; i < patterns.Length; i++)
            {
                System.Text.RegularExpressions.Match m = patterns[i].Match(subject.Text);
                if (m.Success && (best == null || m.Index < best.Index)) { best = m; which = i; }
            }
            if (best != null)
            {
                string Out(string text) => subject.Out(text).AsString;
                var groups = new string?[best.Groups.Count - 1];
                for (int g = 1; g < best.Groups.Count; g++) groups[g - 1] = best.Groups[g].Success ? Out(best.Groups[g].Value) : null;
                var result = (which + 1, Out(best.Value), Out(subject.Text.Substring(0, best.Index)), groups);
                _start += subject.ByteAt(best.Index + best.Length);
                return result;
            }
            if (_end - _start > maxLength) throw new IOException("too much output without a match");
            if (!await FillAsync(ct).ConfigureAwait(false)) return null;
        }
    }

    /// <summary>Up to <paramref name="max"/> bytes: whatever is available first; null at the end.</summary>
    internal async Task<string?> ReadAsync(int max, CancellationToken ct)
    {
        if (_start == _end && !await FillAsync(ct).ConfigureAwait(false)) return null;
        int n = Math.Min(max, _end - _start);
        string s = LuaText.FromBytes(_buffer.AsSpan(_start, n));
        _start += n;
        return s;
    }

    /// <summary>The next line without its line ending ("\n" or "\r\n"); null at the end.</summary>
    internal async Task<string?> ReadLineAsync(long maxLength, CancellationToken ct)
    {
        int scanned = 0;
        while (true)
        {
            int nl = Array.IndexOf(_buffer, (byte)'\n', _start + scanned, _end - _start - scanned);
            if (nl >= 0)
            {
                int stop = nl > _start && _buffer[nl - 1] == '\r' ? nl - 1 : nl;
                string line = LuaText.FromBytes(_buffer.AsSpan(_start, stop - _start));
                _start = nl + 1;
                return line;
            }
            scanned = _end - _start;
            if (scanned > maxLength) throw new IOException("line too long");
            if (await FillAsync(ct).ConfigureAwait(false)) continue;
            if (_start == _end) return null;
            string last = LuaText.FromBytes(_buffer.AsSpan(_start, _end - _start)); // the last line has no line ending
            _start = _end;
            return last;
        }
    }

    internal async Task<string> ReadAllAsync(long maxLength, CancellationToken ct)
    {
        using var all = new MemoryStream();
        do
        {
            all.Write(_buffer, _start, _end - _start);
            _start = _end;
            if (all.Length > maxLength) throw new IOException("resulting string too large");
        } while (await FillAsync(ct).ConfigureAwait(false));
        return LuaText.FromBytes(all.GetBuffer().AsSpan(0, (int)all.Length));
    }
}

/// <summary>A destination written piece by piece (userdata). Writes are applied in the order they were made.</summary>
public sealed class LuaSink : IUserdata
{
    private readonly LuaState _state;
    internal readonly Stream Stream;
    internal readonly string Target;
    /// <summary>Completes when every operation queued so far has finished. Read and replaced on
    /// the VM thread at the moment an operation is asked for, which is what fixes the order.</summary>
    internal Task Tail = Task.CompletedTask;
    // Owner-thread cached acknowledgement: repeated close observes the same disposal/error.
    internal LuaFuture? CloseFuture;
    private int _closed;
    private int _disposed;
    private CancellationTokenRegistration _abort;
    internal LuaSink(LuaState state, Stream stream, string target)
    {
        _state = state; Stream = stream; Target = target;
        _abort = state.Scheduler.Lifetime.Register(static value =>
        {
            if (((WeakReference<LuaSink>)value!).TryGetTarget(out LuaSink? sink))
                _ = sink.AbortCloseAsync();
        }, new WeakReference<LuaSink>(this));
    }
    internal async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Stream.DisposeAsync().ConfigureAwait(false); }
        finally { _abort.Dispose(); }
    }
    private async Task AbortCloseAsync()
    {
        MarkClosed();
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceError("Cameo.Runtime sink abort cleanup failed: {0}", error); }
    }
    public string TypeName => "Sink";
    public Table? Metatable { get => _state.MetaFor("Sink"); set { } }
    internal bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;
    internal bool Closed => Volatile.Read(ref _closed) != 0;
}

/// <summary>A running program started with <c>aio.spawn</c> (userdata), on pipes or on a pseudo-terminal.</summary>
public sealed class LuaProcess : IUserdata
{
    private readonly LuaState _state;
    internal readonly LuaStream Stdout, Stderr;
    internal readonly LuaSink Stdin;
    internal readonly string Target;
    internal readonly int Pid;
    internal readonly Func<CancellationToken, Task<int>> WaitAsync;
    private readonly Action _kill;

    internal LuaProcess(LuaState state, Process process, string target)
    {
        _state = state;
        Target = target;
        Pid = process.Id;
        Stdout = new LuaStream(state, process.StandardOutput.BaseStream, target);
        Stderr = new LuaStream(state, process.StandardError.BaseStream, target);
        Stdin = new LuaSink(state, process.StandardInput.BaseStream, target);
        WaitAsync = async ct => { await process.WaitForExitAsync(ct).ConfigureAwait(false); return process.ExitCode; };
        _kill = () =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        };
    }

    internal LuaProcess(LuaState state, PtyChild child, string target)
    {
        _state = state;
        Target = target;
        Pid = child.Pid;
        // a terminal has one output: what would be stderr arrives on stdout, in order
        Stdout = new LuaStream(state, child.Output, target) { EndOnError = true };
        Stderr = new LuaStream(state, Stream.Null, target);
        Stdin = new LuaSink(state, child.Input, target);
        WaitAsync = ct => child.Exit.WaitAsync(ct);
        _kill = child.Kill;
    }

    public string TypeName => "Process";
    public Table? Metatable { get => _state.MetaFor("Process"); set { } }
    internal void Kill() => _kill();
}

// Streaming: files, HTTP bodies and program output read (or written) piece by piece.
public static partial class AsyncIoLib
{
    private static void OpenStreams(LuaState L, Table aio)
    {
        // ---- Stream
        var stream = new Table();
        // read([n]): the next piece, at most n bytes (default 64 KiB); nil at the end
        L.RegisterInstance<LuaStream>(stream, "read", static (ref CallContext c) =>
        {
            LuaStream s = CheckStream(ref c);
            long max = c.OptInt(1, 64 * 1024);
            if (max < 1) throw c.ArgError(1, "count must be positive");
            return c.Return(Value.FromUserdata(Read(c.L, s, ct => s.ReadAsync((int)Math.Min(max, int.MaxValue), ct))));
        });
        L.RegisterInstance<LuaStream>(stream, "readLine", static (ref CallContext c) => c.Return(Value.FromUserdata(ReadLine(c.L, CheckStream(ref c)))));
        L.RegisterInstance<LuaStream>(stream, "readAll", static (ref CallContext c) =>
        {
            LuaStream s = CheckStream(ref c);
            long limit = c.L.Limits.MaxStringLength;
            return c.Return(Value.FromUserdata(Read(c.L, s, async ct => await s.ReadAllAsync(limit, ct).ConfigureAwait(false))));
        });
        // lines(): for line in s:lines() do ... end — waits for each line (other tasks run meanwhile)
        L.RegisterInstance<LuaStream>(stream, "lines", static (ref CallContext c) =>
        {
            LuaStream s = CheckStream(ref c);
            return c.Return(Value.FromFunction((ref CallContext it) =>
                s.Closed ? it.Return(Value.Nil) : FutureLib.WaitFor(ref it, ReadLine(it.L, s), throws: true, timeout: -1), "lines"));
        });
        L.RegisterInstance<LuaStream>(stream, "close", static (ref CallContext c) => { CheckStream(ref c).Close(); return c.Return(); });
        // expect(pattern or {patterns} [, timeout]): waits until one of the regular expressions
        // matches what arrives, consumes the output up to the end of the match, and gives
        // {index =, text =, before =, groups =}. Fails with kind "timeout" after `timeout`
        // seconds (default 10; math.huge for no limit) and "eof" if the output ends first.
        L.RegisterInstance<LuaStream>(stream, "expect", static (ref CallContext c) => c.Return(Value.FromUserdata(Expect(ref c, CheckStream(ref c), 1))));
        Table smt = L.MetaFor("Stream");
        smt.SetStr("__index", stream);
        smt.SetStr("__close", stream.GetStr("close"));
        smt.SetStr("__gc", stream.GetStr("close"));

        // ---- Sink
        var sink = new Table();
        L.RegisterInstance<LuaSink>(sink, "write", static (ref CallContext c) =>
        {
            LuaSink k = CheckSink(ref c);
            byte[] data = LuaText.ToBytes(c.CheckString(1));
            if (k.Closed) throw new LuaException("the sink is closed");
            return Queue(ref c, k, async ct => await k.Stream.WriteAsync(data, ct).ConfigureAwait(false));
        });
        L.RegisterInstance<LuaSink>(sink, "close", static (ref CallContext c) =>
        {
            LuaSink k = CheckSink(ref c);
            if (k.CloseFuture is { } closing) return c.Return(Value.FromUserdata(closing));
            k.MarkClosed();
            // After queued writes; every close caller observes this same acknowledgement.
            k.CloseFuture = QueueFuture(c.L, k, async ct =>
            {
                await PendingResource.FinallyAsync(() => k.Stream.FlushAsync(ct), () => k.DisposeAsync()).ConfigureAwait(false);
            });
            return c.Return(Value.FromUserdata(k.CloseFuture));
        });
        L.MetaFor("Sink").SetStr("__index", sink);

        // ---- Process
        var process = new Table();
        L.RegisterInstance<LuaProcess>(process, "write", (ref CallContext c) =>
        {
            LuaProcess p = CheckProcess(ref c);
            return c.L.Call(sink.GetStr("write"), Value.FromUserdata(p.Stdin), c[1]) is { Length: > 0 } r ? c.Return(r[0]) : c.Return();
        });
        L.RegisterInstance<LuaProcess>(process, "closeInput", (ref CallContext c) =>
            c.L.Call(sink.GetStr("close"), Value.FromUserdata(CheckProcess(ref c).Stdin)) is { Length: > 0 } r ? c.Return(r[0]) : c.Return());
        // wait(): the exit code, once the program has ended
        L.RegisterInstance<LuaProcess>(process, "wait", static (ref CallContext c) =>
        {
            LuaProcess p = CheckProcess(ref c);
            return Start(ref c, p.Target, async ct =>
            {
                int code = await p.WaitAsync(ct).ConfigureAwait(false);
                return _ => Value.FromInt(code);
            });
        });
        L.RegisterInstance<LuaProcess>(process, "kill", static (ref CallContext c) => { CheckProcess(ref c).Kill(); return c.Return(); });
        // expect on the program's output, and send / sendLine to its input: the three verbs of Tcl's expect
        L.RegisterInstance<LuaProcess>(process, "expect", static (ref CallContext c) => c.Return(Value.FromUserdata(Expect(ref c, CheckProcess(ref c).Stdout, 1))));
        foreach (bool line in new[] { false, true })
        {
            L.RegisterInstance<LuaProcess>(process, line ? "sendLine" : "send", (ref CallContext c) =>
            {
                LuaProcess p = CheckProcess(ref c);
                Value text = Value.FromString(c.CheckString(1) + (line ? "\n" : ""));
                return c.L.Call(sink.GetStr("write"), Value.FromUserdata(p.Stdin), text) is { Length: > 0 } r ? c.Return(r[0]) : c.Return();
            });
        }
        Table pmt = L.MetaFor("Process");
        L.Register(pmt, "__index", (ref CallContext c) =>
        {
            LuaProcess p = CheckProcess(ref c);
            return (c[1].IsString ? c[1].AsString : "") switch
            {
                "stdout" => c.Return(Value.FromUserdata(p.Stdout)),
                "stderr" => c.Return(Value.FromUserdata(p.Stderr)),
                "pid" => c.Return(p.Pid),
                _ => c.Return(process.Get(c[1])),
            };
        });

        // ---- openers
        L.Register(aio, "openRead", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            if (!c.L.AuthorizeFile(path, write: false, out string canonical))
                return Denied(ref c, path, "file access is not permitted by host file policy");
            return Start(ref c, path, ct => Task.Run<Func<LuaState, Value>>(() =>
            {
                var file = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: true);
                return PendingResource.Transfer(file, ct,
                    (L, f) => Value.FromUserdata(new LuaStream(L, f, path)), f => f.DisposeAsync());
            }, ct));
        });
        // openWrite(path [, append])
        L.Register(aio, "openWrite", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            bool append = c[1].IsTruthy;
            if (!c.L.AuthorizeFile(path, write: true, out string canonical))
                return Denied(ref c, path, "file access is not permitted by host file policy");
            return Start(ref c, path, ct => Task.Run<Func<LuaState, Value>>(() =>
            {
                var file = new FileStream(canonical, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                return PendingResource.Transfer(file, ct,
                    (L, f) => Value.FromUserdata(new LuaSink(L, f, path)), f => f.DisposeAsync());
            }, ct));
        });
        // spawn(program [, args]): starts a program and returns at once; read its output as it comes
        L.Register(aio, "spawn", static (ref CallContext c) =>
        {
            string program = c.CheckText(0);
            List<string> args = ProgramArgs(ref c, 1);
            bool pty = false;
            int cols = 80, rows = 24;
            if (!c.IsNoneOrNil(2))
            {
                Table o = c.CheckTable(2);
                pty = o.GetStr("pty").IsTruthy;
                if (o.GetStr("cols").TryToInteger(out long cw)) cols = (int)Math.Clamp(cw, 1, 1000);
                if (o.GetStr("rows").TryToInteger(out long rh)) rows = (int)Math.Clamp(rh, 1, 1000);
            }
            if (!AuthorizeProcess(c.L, ref program, args, pty))
                return Denied(ref c, program, c.L.AllowProcesses
                    ? "the requested program or arguments are not permitted by the host process policy"
                    : "starting programs is not enabled for this VM");
            if (pty)
            {
                // On a pseudo-terminal the program behaves as if a person were typing: it
                // prompts, echoes what is sent, and line-buffers its output.
                return Start(ref c, program, ct => Task.Run<Func<LuaState, Value>>(() =>
                {
                    PtyChild child = Pty.Start(program, args, cols, rows);
                    return PendingResource.Transfer(child, ct, (L, owned) =>
                    {
                        var handle = new LuaProcess(L, owned, program);
                        CancellationTokenRegistration onAbort = L.Scheduler.Lifetime.Register(handle.Kill);
                        owned.Exit.ContinueWith(_ => onAbort.Dispose(), TaskScheduler.Default);
                        return Value.FromUserdata(handle);
                    }, async owned =>
                    {
                        try { owned.Kill(); }
                        finally
                        {
                            await PendingResource.FinallyAsync(() => owned.Input.DisposeAsync().AsTask(),
                                () => owned.Output.DisposeAsync()).ConfigureAwait(false);
                        }
                    });
                }, ct));
            }
            return Start(ref c, program, ct => Task.Run<Func<LuaState, Value>>(() =>
            {
                var psi = new ProcessStartInfo(program)
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (string a in args) psi.ArgumentList.Add(a);
                var p = new Process { StartInfo = psi };
                p.Start();
                return PendingResource.Transfer(p, ct, (L, owned) =>
                {
                    var handle = new LuaProcess(L, owned, program);
                    // a run that is stopped takes the programs it started with it
                    CancellationTokenRegistration onAbort = L.Scheduler.Lifetime.Register(handle.Kill);
                    owned.EnableRaisingEvents = true;
                    owned.Exited += (_, _) => onAbort.Dispose();
                    return Value.FromUserdata(handle);
                }, owned =>
                {
                    try { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
                    finally { owned.Dispose(); }
                    return ValueTask.CompletedTask;
                });
            }, ct));
        });
    }

    private static LuaStream CheckStream(ref CallContext c) => c[0].O as LuaStream ?? throw c.TypeError(0, "Stream");
    private static LuaSink CheckSink(ref CallContext c) => c[0].O as LuaSink ?? throw c.TypeError(0, "Sink");
    private static LuaProcess CheckProcess(ref CallContext c) => c[0].O as LuaProcess ?? throw c.TypeError(0, "Process");

    private static List<string> ProgramArgs(ref CallContext c, int arg)
    {
        var args = new List<string>();
        if (c.IsNoneOrNil(arg)) return args;
        Table t = c.CheckTable(arg);
        for (int i = 1; i <= t.Length; i++)
        {
            Value a = t.GetInt(i);
            if (!a.IsString && !a.IsNumber) throw c.ArgError(arg, $"element {i} is not a string");
            args.Add(LuaText.Decode(a.ToString()));
        }
        return args;
    }

    // One read on a stream: a future for the piece (nil at the end).
    private static LuaFuture Read(LuaState L, LuaStream s, Func<CancellationToken, Task<string?>> read)
    {
        // The stream is busy from here until the future settles — as the program sees it, not
        // as the clock does — so whether a second read is refused never depends on timing.
        s.Enter();
        return StartFuture(L, s.Target, async ct =>
        {
            string? piece = await read(ct).ConfigureAwait(false);
            return _ => piece == null ? Value.Nil : Value.FromString(piece);
        }, onSettled: s.Leave);
    }

    private static LuaFuture Expect(ref CallContext c, LuaStream s, int arg)
    {
        var patterns = new List<System.Text.RegularExpressions.Regex>();
        void Add(ref CallContext ctx, in Value v)
        {
            if (v.O is LuaRegex compiled) { patterns.Add(compiled.Regex); return; }
            if (!v.IsString) throw ctx.ArgError(arg, "a pattern must be a string or a Regex");
            patterns.Add(RegexLib.TryCompile(v.AsString, "", out string? problem) ?? throw ctx.ArgError(arg, problem!));
        }
        if (c[arg].O is Table list && list.Metatable == null)
            for (int i = 1; i <= list.Length; i++) Add(ref c, list.GetInt(i));
        else Add(ref c, c[arg]);
        if (patterns.Count == 0) throw c.ArgError(arg, "no patterns given");
        double timeout = c.IsNoneOrNil(arg + 1) ? 10 : c.CheckNumber(arg + 1);
        if (double.IsPositiveInfinity(timeout)) timeout = -1;
        else if (timeout < 0) throw c.ArgError(arg + 1, "timeout must not be negative");
        System.Text.RegularExpressions.Regex[] all = patterns.ToArray();
        long limit = c.L.Limits.MaxStringLength;
        string target = s.Target;
        s.Enter();
        return StartFuture(c.L, target, async ct =>
        {
            var m = await s.ExpectAsync(all, limit, ct).ConfigureAwait(false);
            if (m is not { } hit) throw new IoFailureCarrier(new IoError(IoErrorKind.Eof, "the output ended before a match", target));
            return _ =>
            {
                var t = new Table(0, 4);
                t.SetStr("index", hit.Index);
                t.SetStr("text", hit.Text);
                t.SetStr("before", hit.Before);
                var groups = new Table(hit.Groups.Length, 0);
                for (int g = 0; g < hit.Groups.Length; g++) if (hit.Groups[g] != null) groups.SetInt(g + 1, hit.Groups[g]);
                t.SetStr("groups", groups);
                return Value.FromTable(t);
            };
        }, timeout, onSettled: s.Leave);
    }

    private static LuaFuture ReadLine(LuaState L, LuaStream s)
    {
        long limit = L.Limits.MaxStringLength;
        return Read(L, s, ct => s.ReadLineAsync(limit, ct));
    }

    // Queues an operation on a sink behind the ones already asked for, whether or not the
    // program waited for those. The place in the queue is taken here, on the VM thread.
    private static int Queue(ref CallContext c, LuaSink k, Func<CancellationToken, Task> operation) =>
        c.Return(Value.FromUserdata(QueueFuture(c.L, k, operation)));

    private static LuaFuture QueueFuture(LuaState state, LuaSink k, Func<CancellationToken, Task> operation)
    {
        Task before = k.Tail;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        k.Tail = done.Task;
        return StartFuture(state, k.Target, async ct =>
        {
            try
            {
                await before.ConfigureAwait(false); // never faults: see the finally below
                await operation(ct).ConfigureAwait(false);
                return _ => Value.True;
            }
            finally
            {
                done.SetResult(); // the next one runs even if this one failed
            }
        });
    }
}
