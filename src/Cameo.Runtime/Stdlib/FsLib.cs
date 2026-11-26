using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// <c>fs</c>: files, directories and paths. Operations that can fail return the result, or nil
/// and a typed <c>IoError</c> (the same record the <c>aio</c> library uses) — they do not raise.
/// These are the blocking counterparts of <c>aio</c>; file access is not a restricted
/// permission (as with <c>io.open</c>).
/// </summary>
public static class FsLib
{
    private delegate Value FsOp(LuaState L);

    public static void Open(LuaState L)
    {
        var fs = new Table();
        L.Register(fs, "exists", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            return c.Return(c.L.AuthorizeFile(p, write: false, out string cp) && (File.Exists(cp) || Directory.Exists(cp)));
        });
        L.Register(fs, "isfile", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            return c.Return(c.L.AuthorizeFile(p, write: false, out string cp) && File.Exists(cp));
        });
        L.Register(fs, "isdir", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            return c.Return(c.L.AuthorizeFile(p, write: false, out string cp) && Directory.Exists(cp));
        });
        L.Register(fs, "stat", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            if (!c.L.AuthorizeFile(p, write: false, out string cp)) return Denied(ref c, p);
            return Try(ref c, p, _ =>
            {
                FileSystemInfo info = Directory.Exists(cp) ? new DirectoryInfo(cp) : new FileInfo(cp);
                if (!info.Exists) throw new FileNotFoundException("no such file or directory");
                var t = new Table(0, 3);
                t.SetStr("size", info is FileInfo f ? f.Length : 0);
                t.SetStr("modified", (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds);
                t.SetStr("isdir", info is DirectoryInfo);
                return Value.FromTable(t);
            });
        });
        L.Register(fs, "mkdir", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            if (!c.L.AuthorizeFile(p, write: true, out string cp)) return Denied(ref c, p);
            return Try(ref c, p, _ => { Directory.CreateDirectory(cp); return Value.True; });
        });
        L.Register(fs, "remove", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            bool recursive = c[1].IsTruthy;
            if (!c.L.AuthorizeFile(p, write: true, out string cp)) return Denied(ref c, p);
            return Try(ref c, p, _ =>
            {
                if (Directory.Exists(cp)) Directory.Delete(cp, recursive);
                else if (File.Exists(cp)) File.Delete(cp);
                else throw new FileNotFoundException("no such file or directory");
                return Value.True;
            });
        });
        L.Register(fs, "list", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            if (!c.L.AuthorizeFile(p, write: false, out string cp)) return Denied(ref c, p);
            return Try(ref c, p, _ =>
            {
                string[] names = Directory.GetFileSystemEntries(cp).Select(e => Path.GetFileName(e)!).ToArray();
                Array.Sort(names, StringComparer.Ordinal);
                var t = new Table(names.Length, 0);
                foreach (string n in names) t.Append(Value.FromText(n));
                return Value.FromTable(t);
            });
        });
        L.Register(fs, "copy", static (ref CallContext c) =>
        {
            string from = c.CheckText(0), to = c.CheckText(1);
            bool overwrite = c[2].IsTruthy;
            if (!c.L.AuthorizeFile(from, write: false, out string cf)) return Denied(ref c, from);
            if (!c.L.AuthorizeFile(to, write: true, out string ct)) return Denied(ref c, to);
            return Try(ref c, from, _ => { File.Copy(cf, ct, overwrite); return Value.True; });
        });
        L.Register(fs, "move", static (ref CallContext c) =>
        {
            string from = c.CheckText(0), to = c.CheckText(1);
            if (!c.L.AuthorizeFile(from, write: true, out string cf)) return Denied(ref c, from);
            if (!c.L.AuthorizeFile(to, write: true, out string ct)) return Denied(ref c, to);
            return Try(ref c, from, _ =>
            {
                if (Directory.Exists(cf)) Directory.Move(cf, ct); else File.Move(cf, ct);
                return Value.True;
            });
        });
        L.Register(fs, "read", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            if (!c.L.AuthorizeFile(p, write: false, out string cp)) return Denied(ref c, p);
            return Try(ref c, p, _ => Value.FromString(LuaText.FromBytes(File.ReadAllBytes(cp))));
        });
        L.Register(fs, "write", static (ref CallContext c) =>
        {
            string p = c.CheckText(0);
            byte[] data = LuaText.ToBytes(c.CheckString(1));
            if (!c.L.AuthorizeFile(p, write: true, out string cp)) return Denied(ref c, p);
            return Try(ref c, p, _ => { File.WriteAllBytes(cp, data); return Value.True; });
        });

        // paths: pure text operations, no file access
        L.Register(fs, "join", static (ref CallContext c) =>
        {
            var parts = new string[c.Count];
            for (int i = 0; i < parts.Length; i++) parts[i] = c.CheckText(i);
            return c.Return(Value.FromText(parts.Length == 0 ? "" : Path.Combine(parts)));
        });
        L.Register(fs, "basename", static (ref CallContext c) => c.Return(Value.FromText(Path.GetFileName(Path.TrimEndingDirectorySeparator(c.CheckText(0))))));
        L.Register(fs, "dirname", static (ref CallContext c) => c.Return(Value.FromText(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(c.CheckText(0))) ?? "")));
        L.Register(fs, "extension", static (ref CallContext c) => c.Return(Value.FromText(Path.GetExtension(c.CheckText(0)))));
        L.Register(fs, "stem", static (ref CallContext c) => c.Return(Value.FromText(Path.GetFileNameWithoutExtension(c.CheckText(0)))));
        L.Register(fs, "absolute", static (ref CallContext c) => c.Return(Value.FromText(Path.GetFullPath(c.CheckText(0)))));
        L.Register(fs, "cwd", static (ref CallContext c) => c.Return(Value.FromText(Directory.GetCurrentDirectory())));
        L.Register(fs, "tempdir", static (ref CallContext c) => c.Return(Value.FromText(Path.TrimEndingDirectorySeparator(Path.GetTempPath()))));
        fs.SetStr("separator", Path.DirectorySeparatorChar.ToString());

        L.Globals.SetStr("fs", fs);
        L.LoadedModules.SetStr("fs", fs);
    }

    private static int Denied(ref CallContext c, string target) =>
        c.Return(Value.Nil, new IoError(IoErrorKind.PermissionDenied, "file access is not permitted by host file policy", target).ToValue(c.L));

    // Runs the operation; an I/O failure becomes `nil, IoError` instead of a raised error.
    private static int Try(ref CallContext c, string target, FsOp op)
    {
        try
        {
            return c.Return(op(c.L), Value.Nil);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            IoError err = IoError.From(e, target, timedOut: false);
            // "already exists", "directory not empty" and the like have no kind of their own
            if (e is IOException and not (FileNotFoundException or DirectoryNotFoundException)) err = err with { Kind = IoErrorKind.Failed };
            return c.Return(Value.Nil, err.ToValue(c.L));
        }
    }
}
