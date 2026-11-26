using System.Collections.Concurrent;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>Watches a directory for changes (userdata).</summary>
public sealed class LuaWatcher : IUserdata
{
    private readonly LuaState _state;
    private readonly FileSystemWatcher _watcher;
    internal readonly string Target;
    internal readonly ConcurrentQueue<(string Kind, string Path, string? OldPath)> Events = new();
    internal readonly SemaphoreSlim Available = new(0);
    private int _closed;

    internal LuaWatcher(LuaState state, string path, bool recursive, string filter)
    {
        _state = state;
        Target = path;
        _watcher = new FileSystemWatcher(path, filter)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Add("created", e.FullPath, null);
        _watcher.Changed += (_, e) => Add("changed", e.FullPath, null);
        _watcher.Deleted += (_, e) => Add("deleted", e.FullPath, null);
        _watcher.Renamed += (_, e) => Add("renamed", e.FullPath, e.OldFullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void Add(string kind, string path, string? oldPath)
    {
        Events.Enqueue((kind, path, oldPath));
        Available.Release();
    }

    public string TypeName => "Watcher";
    public Table? Metatable { get => _state.MetaFor("Watcher"); set { } }
    internal bool Closed => Volatile.Read(ref _closed) != 0;

    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _watcher.Dispose();
        Available.Release(); // wake a waiting next(): it then returns nil
    }
}

// Asynchronous file-system operations and directory watching.
public static partial class AsyncIoLib
{
    private static void OpenFiles(LuaState L, Table aio)
    {
        L.Register(aio, "exists", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            if (!c.L.AuthorizeFile(p, write: false, out string cp))
                return Denied(ref c, p, "file access is not permitted by host file policy");
            return Run(ref c, p, () => { bool e = File.Exists(cp) || Directory.Exists(cp); return _ => Value.FromBool(e); });
        });
        L.Register(aio, "stat", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            if (!c.L.AuthorizeFile(p, write: false, out string cp))
                return Denied(ref c, p, "file access is not permitted by host file policy");
            return Run(ref c, p, () =>
            {
                FileSystemInfo info = Directory.Exists(cp) ? new DirectoryInfo(cp) : new FileInfo(cp);
                if (!info.Exists) throw new FileNotFoundException("no such file or directory");
                long size = info is FileInfo f ? f.Length : 0;
                double modified = (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
                bool isDir = info is DirectoryInfo;
                return _ =>
                {
                    var t = new Table(0, 3);
                    t.SetStr("size", size);
                    t.SetStr("modified", modified);
                    t.SetStr("isdir", isDir);
                    return Value.FromTable(t);
                };
            });
        });
        L.Register(aio, "mkdir", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            if (!c.L.AuthorizeFile(p, write: true, out string cp))
                return Denied(ref c, p, "file access is not permitted by host file policy");
            return Run(ref c, p, () => { Directory.CreateDirectory(cp); return _ => Value.True; });
        });
        // remove(path [, recursive])
        L.Register(aio, "remove", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            bool recursive = c[1].IsTruthy;
            if (!c.L.AuthorizeFile(p, write: true, out string cp))
                return Denied(ref c, p, "file access is not permitted by host file policy");
            return Run(ref c, p, () =>
            {
                if (Directory.Exists(cp)) Directory.Delete(cp, recursive);
                else if (File.Exists(cp)) File.Delete(cp);
                else throw new FileNotFoundException("no such file or directory");
                return _ => Value.True;
            });
        });
        // copy(from, to [, overwrite])
        L.Register(aio, "copy", static (ref CallContext c) =>
        {
            string from = c.CheckText(0), to = c.CheckText(1);
            bool overwrite = c[2].IsTruthy;
            if (!c.L.AuthorizeFile(from, write: false, out string cf))
                return Denied(ref c, from, "file access is not permitted by host file policy");
            if (!c.L.AuthorizeFile(to, write: true, out string ct))
                return Denied(ref c, to, "file access is not permitted by host file policy");
            return Run(ref c, from, () => { File.Copy(cf, ct, overwrite); return _ => Value.True; });
        });
        L.Register(aio, "move", static (ref CallContext c) =>
        {
            string from = c.CheckText(0), to = c.CheckText(1);
            if (!c.L.AuthorizeFile(from, write: true, out string cf))
                return Denied(ref c, from, "file access is not permitted by host file policy");
            if (!c.L.AuthorizeFile(to, write: true, out string ct))
                return Denied(ref c, to, "file access is not permitted by host file policy");
            return Run(ref c, from, () =>
            {
                if (Directory.Exists(cf)) Directory.Move(cf, ct); else File.Move(cf, ct);
                return _ => Value.True;
            });
        });

        // watch(dir [, {recursive =, filter =}]): reports files created, changed, deleted or renamed
        L.Register(aio, "watch", static (ref CallContext c) =>
        {
            string path = c.CheckText(0);
            bool recursive = false;
            string filter = "*";
            if (!c.IsNoneOrNil(1))
            {
                Table o = c.CheckTable(1);
                recursive = o.GetStr("recursive").IsTruthy;
                if (o.GetStr("filter").IsString) filter = LuaText.Decode(o.GetStr("filter").AsString);
            }
            if (!c.L.AuthorizeFile(path, write: false, out string canonical))
                return Denied(ref c, path, "file access is not permitted by host file policy");
            return Run(ref c, path, () =>
            {
                if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException("no such directory");
                return state =>
                {
                    var w = new LuaWatcher(state, canonical, recursive, filter);
                    state.Scheduler.Lifetime.Register(w.Close);
                    return Value.FromUserdata(w);
                };
            });
        });
        var watcher = new Table();
        // next([timeout]): the next event; fails with kind "timeout" if none arrives in time; nil once closed
        L.RegisterInstance<LuaWatcher>(watcher, "next", static (ref CallContext c) =>
            c.Return(Value.FromUserdata(NextEvent(c.L, CheckWatcher(ref c), c.IsNoneOrNil(1) ? -1 : c.CheckNumber(1)))));
        // events(): for ev in w:events() do ... end — runs until the watcher is closed
        L.RegisterInstance<LuaWatcher>(watcher, "events", static (ref CallContext c) =>
        {
            LuaWatcher w = CheckWatcher(ref c);
            return c.Return(Value.FromFunction((ref CallContext it) =>
                w.Closed ? it.Return(Value.Nil) : FutureLib.WaitFor(ref it, NextEvent(it.L, w, -1), throws: true, timeout: -1), "events"));
        });
        L.RegisterInstance<LuaWatcher>(watcher, "close", static (ref CallContext c) => { CheckWatcher(ref c).Close(); return c.Return(); });
        Table wmt = L.MetaFor("Watcher");
        wmt.SetStr("__index", watcher);
        wmt.SetStr("__close", watcher.GetStr("close"));
        wmt.SetStr("__gc", watcher.GetStr("close"));
    }

    private static LuaWatcher CheckWatcher(ref CallContext c) => c[0].O as LuaWatcher ?? throw c.TypeError(0, "Watcher");

    // A blocking file-system call moved to the thread pool.
    private static int Run(ref CallContext c, string target, Func<Func<LuaState, Value>> work) =>
        Start(ref c, target, ct => Task.Run(work, ct));

    private static LuaFuture NextEvent(LuaState L, LuaWatcher w, double timeout) =>
        StartFuture(L, w.Target, async ct =>
        {
            while (true)
            {
                if (w.Closed) return _ => Value.Nil; // a closed watcher reports nothing more, even what it had queued
                if (w.Events.TryDequeue(out var e))
                {
                    return _ =>
                    {
                        var t = new Table(0, 3);
                        t.SetStr("kind", e.Kind);
                        t.SetStr("path", Value.FromText(e.Path));
                        if (e.OldPath != null) t.SetStr("oldPath", Value.FromText(e.OldPath));
                        return Value.FromTable(t);
                    };
                }
                await w.Available.WaitAsync(ct).ConfigureAwait(false);
            }
        }, timeout);
}
