using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>How much library a VM is given. A host embedding the VM as a small scripting
/// engine can stop at <see cref="Standard"/> or <see cref="Sandboxed"/>; the command-line tool uses <see cref="All"/>.</summary>
public enum CameoLibraries
{
    /// <summary>Strictly deterministic Starlark-style hermetic profile: pure compute, strings,
    /// tables, List, Dict, freeze/isFrozen, Query, FloatArray, JSON, CSV, regex, deterministic
    /// hashes (base64/hex/sha256/hmac), and chunked deterministic <c>parallel.map</c>.
    /// Excludes wall-clock (<c>os</c>, <c>time</c>), RNG (<c>math.random</c>, <c>crypto.random*</c>/<c>uuid</c>),
    /// <c>io</c>, <c>fs</c>, <c>aio</c>, <c>db</c>, <c>cli</c>, <c>debug</c>, and <c>dofile</c>;
    /// module exports loaded via <c>require</c> are deeply frozen.</summary>
    Hermetic = -2,
    /// <summary>Pure-compute sandboxed profile: Lua core, extended data/text/regex/JSON/CSV/Query/FloatArray/List/Dict,
    /// cooperative futures/timers, and safe read-only <c>os.time</c>/<c>date</c>/<c>clock</c>.
    /// Excludes <c>io</c>, <c>fs</c>, <c>aio</c>, <c>db</c>, <c>cli</c>, <c>debug</c>, <c>dofile</c>,
    /// and filesystem/env <c>os</c> functions.</summary>
    Sandboxed = -1,
    /// <summary>The Lua 5.4 standard library (plus utf16), and nothing else.</summary>
    Standard = 0,
    /// <summary>Standard, plus the additions that only compute: string/table/utf8 helpers,
    /// json, csv, regex, base64/hex/crypto, Query, FloatArray, List, Dict, time, the math built-ins.</summary>
    Extended = 1,
    /// <summary>Extended, plus what reaches outside the VM: futures and workers, aio, fs, db,
    /// cli, sandbox. (Network, process and database access still need their permissions.)</summary>
    All = 2,
}

/// <summary>Opens the libraries into a state.</summary>
public static class StdlibRegistry
{
    public static void OpenAll(LuaState L, CameoLibraries libraries = CameoLibraries.All, bool? allowDebug = null)
    {
        bool hermetic = libraries == CameoLibraries.Hermetic;
        bool sandboxed = libraries == CameoLibraries.Sandboxed || hermetic;
        bool openDebug = allowDebug ?? !sandboxed;
        L.AllowDebug = openDebug;
        L.IsHermetic = hermetic;
        L.IsSandboxed = sandboxed;
        BaseLib.Open(L, sandboxed);
        CoroutineLib.Open(L);
        StringLib.Open(L);
        TableLib.Open(L);
        MathLib.Open(L, hermetic);
        if (!hermetic) OsLib.Open(L, sandboxed);
        if (!sandboxed) IoLib.Open(L);
        Utf8Lib.Open(L);
        Utf16Lib.Open(L);
        if (openDebug) DebugLib.Open(L);
        if (sandboxed || libraries >= CameoLibraries.Extended)
        {
            StringExtLib.Open(L);
            TableExtLib.Open(L);
            JsonLib.Open(L);
            if (!hermetic) TimeLib.Open(L);
            RegexLib.Open(L);
            CodecLib.Open(L, hermetic);
            FloatArrayLib.Open(L);
            QueryLib.Open(L);
            CollectionsLib.Open(L);
        }
        if (hermetic)
        {
            TextToolsLib.Open(L); // pure csv / table.render / string.tr (fs is absent)
        }
        else if (sandboxed)
        {
            FutureLib.Open(L);
            TextToolsLib.Open(L); // pure csv / table.render / string.tr (fs is absent)
        }
        else
        {
            if (libraries >= CameoLibraries.All)
            {
                FutureLib.Open(L);
                AsyncIoLib.Open(L);
                FsLib.Open(L);
                DbLib.Open(L);
                CliLib.Open(L);
            }
            if (libraries >= CameoLibraries.Extended) TextToolsLib.Open(L); // adds to fs when fs is there
        }
        L.SnapshotStdlib();
    }
}
