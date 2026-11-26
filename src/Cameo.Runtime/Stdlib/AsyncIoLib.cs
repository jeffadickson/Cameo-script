using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>Why an asynchronous I/O operation failed.</summary>
public enum IoErrorKind
{
    NotFound,
    PermissionDenied,
    Timeout,
    Network,
    Invalid,
    Cancelled,
    Failed,
    /// <summary>The server answered with an error status (only from the helpers that expect success, such as getJson).</summary>
    Http,
    /// <summary>The output ended before what was being waited for arrived (expect).</summary>
    Eof,
}

/// <summary>The typed error of the <c>aio</c> library: carried as the <c>cause</c> of the FutureError.</summary>
public sealed record class IoError(IoErrorKind Kind, string Message, string Target)
{
    public static string KindName(IoErrorKind kind) => kind switch
    {
        IoErrorKind.NotFound => "not_found",
        IoErrorKind.PermissionDenied => "permission_denied",
        IoErrorKind.Timeout => "timeout",
        IoErrorKind.Network => "network",
        IoErrorKind.Invalid => "invalid",
        IoErrorKind.Cancelled => "cancelled",
        IoErrorKind.Failed => "failed",
        IoErrorKind.Http => "http",
        IoErrorKind.Eof => "eof",
        _ => "failed",
    };

    public Value ToValue(LuaState L)
    {
        var t = new Table(0, 3);
        t.SetStr("kind", KindName(Kind));
        t.SetStr("message", LuaText.Encode(Message));
        t.SetStr("target", LuaText.Encode(Target));
        t.Metatable = L.IoErrorMeta;
        return Value.FromTable(t);
    }

    /// <summary>Classifies an exception thrown by a .NET I/O call.</summary>
    public static IoError From(Exception e, string target, bool timedOut) => new(e switch
    {
        OperationCanceledException => timedOut ? IoErrorKind.Timeout : IoErrorKind.Cancelled,
        FileNotFoundException or DirectoryNotFoundException => IoErrorKind.NotFound,
        Win32Exception { NativeErrorCode: 2 or 3 } => IoErrorKind.NotFound,       // ENOENT / file or path not found
        Win32Exception { NativeErrorCode: 13 or 5 } => IoErrorKind.PermissionDenied, // EACCES / access denied
        UnauthorizedAccessException or System.Security.SecurityException => IoErrorKind.PermissionDenied,
        HttpRequestException => IoErrorKind.Network,
        ArgumentException or UriFormatException or NotSupportedException or InvalidOperationException => IoErrorKind.Invalid,
        _ => IoErrorKind.Failed,
    }, timedOut ? "timed out" : e.Message, target);
}

/// <summary>
/// <c>aio</c>: asynchronous I/O. Each function starts the operation on the .NET thread pool and
/// returns a Future at once; the VM keeps running other tasks and picks the result up through
/// the scheduler's inbox. Failures are typed: the future fails with a FutureError whose
/// <c>cause</c> is an <c>IoError</c> record (kind, message, target).
///
/// Network and process access are permissions of the VM (<see cref="LuaState.AllowNetwork"/>,
/// <see cref="LuaState.AllowProcesses"/>), off unless the host turns them on.
/// </summary>
public static partial class AsyncIoLib
{
    /// <summary>Thrown by a result builder (on the VM thread) to fail the future with a typed error.</summary>
    private sealed class IoFailure(IoError error) : Exception(error.Message)
    {
        public IoError Error => error;
    }

    /// <summary>Thrown by the work itself (off the VM thread) when it already knows the typed error.</summary>
    private sealed class IoFailureCarrier(IoError error) : Exception(error.Message)
    {
        public IoError Error => error;
    }

    // Shared by every VM in the process, so it must not remember anything between requests:
    // no cookies (aio.httpSession has its own client for that).
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), UseCookies = false })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan, // each request carries its own
    };

    private const double DefaultHttpTimeout = 30;

    public static void Open(LuaState L)
    {
        var aio = new Table();
        L.Register(aio, "readFile", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            if (!c.L.AuthorizeFile(path, write: false, out string canonical))
                return Denied(ref c, path, "file access is not permitted by host file policy");
            return Start(ref c, path, async ct =>
            {
                byte[] bytes = await File.ReadAllBytesAsync(canonical, ct).ConfigureAwait(false);
                string s = LuaText.FromBytes(bytes);
                return _ => Value.FromString(s);
            });
        });
        L.Register(aio, "writeFile", static (ref CallContext c) => Write(ref c, append: false));
        L.Register(aio, "appendFile", static (ref CallContext c) => Write(ref c, append: true));
        L.Register(aio, "listDir", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            if (!c.L.AuthorizeFile(path, write: false, out string canonical))
                return Denied(ref c, path, "file access is not permitted by host file policy");
            return Start(ref c, path, ct => Task.Run<Func<LuaState, Value>>(() =>
            {
                string[] names = Directory.GetFileSystemEntries(canonical).Select(p => Path.GetFileName(p)!).ToArray();
                Array.Sort(names, StringComparer.Ordinal);
                return _ =>
                {
                    var t = new Table(names.Length, 0);
                    foreach (string n in names) t.Append(Value.FromText(n));
                    return Value.FromTable(t);
                };
            }, ct));
        });
        L.Register(aio, "exec", Exec);
        OpenHttp(L, aio);
        OpenStreams(L, aio);
        OpenFiles(L, aio);

        L.Globals.SetStr("aio", aio);
        L.LoadedModules.SetStr("aio", aio);

        Table emt = L.IoErrorMeta;
        emt.SetStr("__name", "IoError");
        L.Register(emt, "__tostring", static (ref CallContext c) =>
        {
            Table t = c.CheckTable(0);
            return c.Return($"IoError({t.GetStr("kind")}): {t.GetStr("message")}");
        });
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool and returns the Future for it. The work
    /// returns a function that builds the VM value; that function runs on the VM thread.
    /// The work is cancelled when the future is cancelled or the run is aborted.
    /// </summary>
    private static int Start(ref CallContext c, string target, Func<CancellationToken, Task<Func<LuaState, Value>>> work, double timeoutSeconds = -1) =>
        c.Return(Value.FromUserdata(StartFuture(c.L, target, work, timeoutSeconds)));

    // onSettled runs on the VM thread just before the future is completed or failed (not if
    // the run was aborted meanwhile).
    private static LuaFuture StartFuture(LuaState L, string target, Func<CancellationToken, Task<Func<LuaState, Value>>> work, double timeoutSeconds = -1, Action? onSettled = null)
    {
        var fut = new LuaFuture(L);
        Scheduler s = L.Scheduler;
        var operation = new OperationLifetime(s.Lifetime, timeoutSeconds);
        fut.OnComplete(_ => operation.Settle());
        int epoch = s.BeginExternal();
        s.LiveTasks++;
        Task.Run(async () =>
        {
            Func<LuaState, Value>? build = null;
            IoError? error = null;
            try
            {
                build = await work(operation.Token).ConfigureAwait(false);
            }
            catch (IoFailureCarrier f)
            {
                error = f.Error; // the work classified its own failure
            }
            catch (Exception e)
            {
                error = IoError.From(e is AggregateException a ? a.GetBaseException() : e, target, operation.TimedOut);
            }
            finally { operation.ProducerStopped(); }
            s.PostExternal(() =>
            {
                if (!s.EndExternal(epoch)) return;
                s.LiveTasks--;
                onSettled?.Invoke();
                if (fut.IsDone) return; // cancelled work must not publish a new owned handle
                if (error != null) { Fail(L, fut, error); return; }
                try
                {
                    fut.Complete(build!(L));
                }
                catch (IoFailure f)
                {
                    Fail(L, fut, f.Error);
                }
                catch (OperationCanceledException error)
                {
                    Fail(L, fut, IoError.From(error, target, operation.TimedOut));
                }
            });
        });
        return fut;
    }

    private static void Fail(LuaState L, LuaFuture fut, IoError error)
    {
        FutureErrorKind kind = error.Kind switch
        {
            IoErrorKind.Timeout => FutureErrorKind.Timeout,
            IoErrorKind.Cancelled => FutureErrorKind.Cancelled,
            _ => FutureErrorKind.Failed,
        };
        fut.Fail(new FutureError(kind, LuaText.Encode($"{error.Target}: {error.Message}"), error.ToValue(L)));
    }

    private static int Denied(ref CallContext c, string target, string what)
    {
        var fut = new LuaFuture(c.L);
        Fail(c.L, fut, new IoError(IoErrorKind.PermissionDenied, what, target));
        return c.Return(Value.FromUserdata(fut));
    }

    // ------------------------------------------------------------------ files

    private static int Write(ref CallContext c, bool append)
    {
        string path = c.CheckText(0);
        byte[] data = LuaText.ToBytes(c.CheckString(1));
        if (!c.L.AuthorizeFile(path, write: true, out string canonical))
            return Denied(ref c, path, "file access is not permitted by host file policy");
        return Start(ref c, path, async ct =>
        {
            if (append) await File.AppendAllBytesAsync(canonical, data, ct).ConfigureAwait(false);
            else await File.WriteAllBytesAsync(canonical, data, ct).ConfigureAwait(false);
            return _ => Value.True;
        });
    }

    // ------------------------------------------------------------------ processes

    // aio.exec(program, {args}, stdin): runs a program directly (no shell) and collects its output.
    private static int Exec(ref CallContext c)
    {
        string program = c.CheckText(0);
        List<string> args = ProgramArgs(ref c, 1);
        byte[]? stdin = c.IsNoneOrNil(2) ? null : LuaText.ToBytes(c.CheckString(2));
        if (!AuthorizeProcess(c.L, ref program, args, pseudoTerminal: false))
            return Denied(ref c, program, c.L.AllowProcesses
                ? "the requested program or arguments are not permitted by the host process policy"
                : "starting programs is not enabled for this VM");
        return Start(ref c, program, async ct =>
        {
            var psi = new ProcessStartInfo(program)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = new Process { StartInfo = psi };
            p.Start();
            try
            {
                Task<byte[]> stdout = ReadAll(p.StandardOutput.BaseStream, ct);
                Task<byte[]> stderr = ReadAll(p.StandardError.BaseStream, ct);
                try
                {
                    if (stdin != null) await p.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
                    p.StandardInput.Close();
                }
                catch (IOException) { } // the program closed its input early
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                string o = LuaText.FromBytes(await stdout.ConfigureAwait(false));
                string e = LuaText.FromBytes(await stderr.ConfigureAwait(false));
                int code = p.ExitCode;
                return _ =>
                {
                    var t = new Table(0, 3);
                    t.SetStr("code", code);
                    t.SetStr("stdout", o);
                    t.SetStr("stderr", e);
                    return Value.FromTable(t);
                };
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
        });
    }

    // Every process adapter passes this gate before acquiring OS resources. Under a
    // selective policy the normalized path returned by authorization is also launched.
    private static bool AuthorizeProcess(LuaState state, ref string program, IReadOnlyList<string> arguments, bool pseudoTerminal)
    {
        if (!state.AllowProcesses) return false;
        if (state.ProcessPolicy is not { } policy) return true;
        if (!policy.TryAuthorize(program, arguments, pseudoTerminal, out string normalized)) return false;
        program = normalized;
        return true;
    }

    private static async Task<byte[]> ReadAll(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }
}
