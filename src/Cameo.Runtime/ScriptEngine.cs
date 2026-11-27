using Cameo.Runtime.Compiler;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Stdlib;
using Cameo.Runtime.Syntax;

namespace Cameo.Runtime;

public sealed class CameoRuntimeOptions
{
    /// <summary>Type-check before compiling (default). When false, code is compiled unchecked.</summary>
    public bool TypeCheck = true;
    /// <summary>Directories searched by <c>require</c>, in addition to the main script's directory and the working directory.</summary>
    public List<string> ModulePaths = new();
    /// <summary>Watchdog: stop a program that runs longer than this (null = no limit).</summary>
    public TimeSpan? Timeout;
    /// <summary>Watchdog: stop a program when the process's managed heap exceeds this many bytes
    /// (null = no limit). Sampled every 10 ms, so it is a safety net, not an exact cap.</summary>
    public long? MaxMemoryBytes;
    /// <summary>Which libraries the program gets (default: all of them). The type checker knows
    /// every library either way; using one that is not there fails at run time.</summary>
    public Stdlib.CameoLibraries Libraries = Stdlib.CameoLibraries.All;
    /// <summary>Let the program make network requests (<c>aio.httpGet</c>, <c>aio.httpRequest</c>). Off by default.</summary>
    public bool AllowNetwork;
    /// <summary>Let the program start other programs (<c>aio.exec</c>). Off by default.</summary>
    public bool AllowProcesses;
    /// <summary>Optional immutable exact executable/argument allowlist. This narrows
    /// <see cref="AllowProcesses"/> and is inherited by workers; null keeps coarse permission behavior.</summary>
    public ProcessPolicy? ProcessPolicy;
    /// <summary>Let the program open databases (<c>db.open</c>). Off by default; the databases
    /// themselves come from <c>State.DatabaseFactory</c> (see Cameo.Runtime.Sqlite for a ready-made one).</summary>
    public bool AllowDatabase;
    /// <summary>Optional immutable filesystem allowlist restricting <c>io</c>, <c>fs</c>, <c>os</c>,
    /// <c>aio</c>, and <c>dofile</c>. Inherited by workers and sandboxes; null keeps unrestricted file access.</summary>
    public FilePolicy? FilePolicy;
    /// <summary>Optional immutable HTTP/HTTPS egress allowlist (hosts, schemes, private-IP blocking),
    /// consulted in addition to <see cref="AllowNetwork"/> and inherited by workers and sandboxes.</summary>
    public NetworkPolicy? NetworkPolicy;
    /// <summary>Whether to expose the <c>debug</c> library. Defaults to false when
    /// <see cref="Libraries"/> is <see cref="Stdlib.CameoLibraries.Sandboxed"/> and true otherwise.</summary>
    public bool? AllowDebug;
    /// <summary>Optional deterministic bytecode step budget (loop back-edges and calls per run;
    /// null = unlimited). Exceeding it aborts with <see cref="AbortReason.StepLimit"/>.</summary>
    public long? MaxSteps;
    /// <summary>Optional call-count threshold for automatic IL tiering when <c>Cameo.Jit</c> is
    /// enabled (0 or null = manual <c>jit.compile</c> only).</summary>
    public int? JitTierThreshold;
}

/// <summary>Raised when source code fails to parse or type-check.</summary>
public sealed class CameoCompileException : Exception
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public CameoCompileException(IReadOnlyList<Diagnostic> diagnostics)
        : base(LuaText.Decode(string.Join("\n", diagnostics.Select(d => d.ToString()))))
    {
        Diagnostics = diagnostics;
    }
}

/// <summary>
/// Front door of the implementation: parses, type-checks and compiles Teal source directly to
/// bytecode and runs it on the VM.
/// </summary>
public sealed partial class ScriptEngine
{
    // What `require` can resolve a module name to. A C# 15 union: the three shapes share no
    // fields worth a base class, and every consumer must handle each of them (the compiler
    // checks the switches below for exhaustiveness).
    private sealed class TealSource(string name, string path, Chunk ast)
    {
        public string Name => name;
        public string Path => path;
        public Chunk Ast => ast;
        public bool Checked;
    }

    /// <summary>A <c>.d.tl</c> declaration file, with the plain Lua file implementing it if there is one.</summary>
    private sealed class DeclaredModule(string name, string path, Chunk declarations, string? luaPath)
    {
        public string Name => name;
        public string Path => path;
        public Chunk Declarations => declarations;
        public string? LuaPath => luaPath;
    }

    /// <summary>A plain Lua module without declarations: compiled unchecked, typed as unknown.</summary>
    private sealed class LuaSource(string name, string path)
    {
        public string Name => name;
        public string Path => path;
    }

    private union Module(TealSource, DeclaredModule, LuaSource);

    public LuaState State { get; } = new();
    public CameoRuntimeOptions Options { get; }
    /// <summary>Warnings produced by the most recent check.</summary>
    public List<Diagnostic> Warnings { get; } = new();

    private readonly ISourceLanguage _language;
    private readonly string _sourceExtension;
    private readonly Dictionary<string, Module?> _modules = new();
    private readonly List<string> _searchDirs = new();

    public ScriptEngine(CameoRuntimeOptions? options = null) : this(options, CameoSourceLanguage.Instance) { }

    /// <summary>Creates an engine using an explicit immutable source-language front end.
    /// Existing constructors and Teal defaults remain unchanged.</summary>
    public ScriptEngine(CameoRuntimeOptions? options, ISourceLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        _language = language;
        _sourceExtension = language.SourceExtension;
        if (_sourceExtension.Length < 2 || _sourceExtension[0] != '.' ||
            _sourceExtension.AsSpan(1).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"))
            throw new ArgumentException("Language source suffix must be a dot followed by ASCII letters/digits", nameof(language));
        Options = options ?? new CameoRuntimeOptions();
        bool allowDebug = Options.AllowDebug ?? (Options.Libraries is not (Stdlib.CameoLibraries.Sandboxed or Stdlib.CameoLibraries.Hermetic));
        State.AllowDebug = allowDebug;
        StdlibRegistry.OpenAll(State, Options.Libraries, allowDebug);
        if (_language.UsesTaggedResults)
        {
            ResultLib.Open(State);
            State.SnapshotStdlib();
        }
        State.ModuleLoader = LoadModule;
        State.ChunkLoader = LoadChunk;
        if (State.Globals.GetStr("package").O is Table package)
            package.SetStr("path", $"./?{_sourceExtension};./?/init{_sourceExtension}");
        _searchDirs.AddRange(Options.ModulePaths);
        State.AllowNetwork = Options.AllowNetwork;
        State.AllowProcesses = Options.AllowProcesses;
        State.ProcessPolicy = Options.ProcessPolicy;
        State.AllowDatabase = Options.AllowDatabase;
        State.FilePolicy = Options.FilePolicy;
        State.NetworkPolicy = Options.NetworkPolicy;
        if (Options.MaxSteps is { } maxSteps)
        {
            if (maxSteps <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxSteps must be positive");
            State.StepBudget = maxSteps;
            State.ResetStepBudget();
        }
        if (Options.JitTierThreshold is { } tier)
        {
            if (tier < 0) throw new ArgumentOutOfRangeException(nameof(options), "JitTierThreshold must be non-negative");
            State.JitTierThreshold = tier;
        }
        // A parallel worker is an engine of its own, so that it can `require` the same modules.
        // The watchdog limits are not copied: stopping this engine's program stops its workers.
        State.WorkerFactory = () =>
        {
            var w = new ScriptEngine(new CameoRuntimeOptions
            {
                TypeCheck = Options.TypeCheck,
                Libraries = Options.Libraries,
                AllowDebug = allowDebug,
                ModulePaths = [.. _searchDirs],
                AllowNetwork = Options.AllowNetwork,
                AllowProcesses = Options.AllowProcesses,
                ProcessPolicy = Options.ProcessPolicy,
                AllowDatabase = Options.AllowDatabase,
                FilePolicy = Options.FilePolicy,
                NetworkPolicy = Options.NetworkPolicy,
            }, _language);
            w.SetUnitDimensionResolver(_unitDimensionResolver);
            return w.State;
        };
        State.SandboxedWorkerFactory = () =>
        {
            var w = new ScriptEngine(new CameoRuntimeOptions
            {
                TypeCheck = Options.TypeCheck,
                Libraries = Stdlib.CameoLibraries.Sandboxed,
                AllowDebug = false,
                ModulePaths = [.. _searchDirs],
                AllowNetwork = false,
                AllowProcesses = false,
                ProcessPolicy = Options.ProcessPolicy,
                AllowDatabase = false,
                FilePolicy = Options.FilePolicy ?? FilePolicy.DenyAll,
                NetworkPolicy = Options.NetworkPolicy,
            }, _language);
            w.SetUnitDimensionResolver(_unitDimensionResolver);
            return w.State;
        };
        State.HermeticWorkerFactory = () =>
        {
            var w = new ScriptEngine(new CameoRuntimeOptions
            {
                TypeCheck = Options.TypeCheck,
                Libraries = Stdlib.CameoLibraries.Hermetic,
                AllowDebug = false,
                ModulePaths = [.. _searchDirs],
                AllowNetwork = false,
                AllowProcesses = false,
                ProcessPolicy = Options.ProcessPolicy,
                AllowDatabase = false,
                FilePolicy = Options.FilePolicy ?? FilePolicy.DenyAll,
                NetworkPolicy = Options.NetworkPolicy,
            }, _language);
            w.SetUnitDimensionResolver(_unitDimensionResolver);
            return w.State;
        };
        InitChecker();
    }

    // ------------------------------------------------------------------ typed host interop

    /// <summary>
    /// Registers a typed .NET delegate as a global script function and declares its signature to
    /// the static type checker. Pass <paramref name="asFuture"/> = <see langword="true"/> for
    /// <see cref="Task"/> / <see cref="Task{TResult}"/>-returning delegates so calls return a
    /// script <c>Future</c>. Pass <paramref name="threadSafe"/> = <see langword="true"/> to also
    /// make the function available inside <c>parallel.map</c> / <c>Future.parallel</c> workers.
    /// </summary>
    public void Register(string name, Delegate fn, bool asFuture = false, bool threadSafe = false)
    {
        var (native, decl) = Interop.HostInterop.BindDelegate(State, name, fn, asFuture, _unitDimensionResolver);
        Value val = Value.FromFunction(native);
        State.Globals.SetStr(name, val);
        if (threadSafe)
            State.RegisterExtension(s => s.Globals.SetStr(name, val));
        AddHostDeclaration(decl, $"<host:{name}>", name);
    }

    /// <summary>
    /// Exposes a .NET object as a typed host global: its public properties become live fields
    /// and its public methods become bound functions (camelCase by default; override with
    /// <see cref="Interop.ExposeAttribute"/> or exclude with <see cref="Interop.HideAttribute"/>).
    /// Its generated record signature is registered automatically with the static type checker.
    /// </summary>
    public void Expose(string name, object target)
    {
        var (hostObj, decl) = Interop.HostInterop.BindObject(State, name, target, _unitDimensionResolver);
        State.Globals.SetStr(name, Value.FromUserdata(hostObj));
        AddHostDeclaration(decl, $"<host:{name}>", name);
    }

    /// <summary>
    /// Converts a .NET value (primitive, string, <c>FloatArray</c>, sequence, or dictionary)
    /// to a script global and declares its inferred type to the static type checker.
    /// </summary>
    public void DefineValue(string name, object? value)
    {
        var (val, decl) = Interop.HostInterop.BindValue(State, name, value);
        State.Globals.SetStr(name, val);
        AddHostDeclaration(decl, $"<host:{name}>", name);
    }

    /// <summary>
    /// Registers custom Teal declaration source (e.g. a <c>global record</c> or <c>global</c>
    /// function signature) directly with the engine's static type checker.
    /// </summary>
    public void DeclareGlobal(string declarationSource, string name = "<host>")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationSource);
        AddHostDeclaration(declarationSource, name);
    }

    // ------------------------------------------------------------------ compile

    private Chunk ParseOrThrow(string source, string fileName) => ParseWith(source, fileName, _language);

    private static Chunk ParseLegacyOrThrow(string source, string fileName) => ParseWith(source, fileName, CameoSourceLanguage.Instance);

    private static Chunk ParseWith(string source, string fileName, ISourceLanguage language)
    {
        try
        {
            return language.Parse(source, fileName);
        }
        catch (CameoSyntaxException e)
        {
            throw new CameoCompileException(new[] { e.Diagnostic });
        }
    }

    /// <summary>Parses and type-checks; returns all errors (empty when the program is valid).</summary>
    public IReadOnlyList<Diagnostic> Check(string source, string fileName) => CheckBytes(LuaText.Encode(source), fileName);

    // Source text travels through the front end in the VM's byte-string form, so that string
    // literals come out as the bytes that are in the source file.
    private IReadOnlyList<Diagnostic> CheckBytes(string source, string fileName)
    {
        // A parse/module-preload failure is still a new check. Never expose diagnostics
        // left by a previous successful parse as warnings for this source.
        Warnings.Clear();
        try
        {
            PreloadModuleGraph(source);
            Chunk chunk = _language.Parse(source, fileName);
            return CheckChunk(chunk);
        }
        catch (CameoSyntaxException e)
        {
            return new[] { e.Diagnostic };
        }
    }

    public IReadOnlyList<Diagnostic> CheckFile(string path)
    {
        AddSearchDir(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
        return CheckBytes(LuaText.ReadFile(path), path);
    }

    /// <summary>Compiles source text to a function prototype.</summary>
    public Proto Compile(string source, string fileName) => CompileBytes(LuaText.Encode(source), fileName);

    /// <summary>Compiles a source file to a function prototype.</summary>
    public Proto CompileFile(string path) => CompileBytes(LuaText.ReadFile(path), path);

    private Proto CompileBytes(string source, string fileName)
    {
        PreloadModuleGraph(source);
        Chunk chunk = ParseOrThrow(source, fileName);
        return CompileChunk(chunk, Options.TypeCheck);
    }

    private static readonly System.Text.RegularExpressions.Regex RequireLiteral =
        new(@"\brequire\s*\(?\s*(?:""([\w./-]+)""|'([\w./-]+)')", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses, in parallel, the modules a source text requires by literal name — and theirs,
    /// transitively — so that the (sequential) type check and the run find them already parsed.
    /// Parsing is independent per file; everything stateful stays on the calling thread.
    /// </summary>
    private void PreloadModuleGraph(string source)
    {
        var pending = new List<string> { source };
        while (pending.Count > 0)
        {
            // names not seen yet, with the file each resolves to
            List<(string Name, string? Tl, string? Decl, string? Lua)> found = [with(capacity: 8)];
            foreach (string text in pending)
            {
                foreach (System.Text.RegularExpressions.Match m in RequireLiteral.Matches(text))
                {
                    string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (_modules.ContainsKey(name) || found.Exists(f => f.Name == name)) continue;
                    var (tl, decl, lua) = LocateModule(name);
                    if (tl != null || decl != null || lua != null) found.Add((name, tl, decl, lua));
                }
            }
            pending.Clear();
            if (found.Count == 0) return;
            var parsed = new (Module? Module, string? Source)[found.Count];
            Parallel.For(0, found.Count, i =>
            {
                var (name, tl, decl, lua) = found[i];
                try
                {
                    if (tl != null)
                    {
                        string src = LuaText.ReadFile(tl);
                        parsed[i] = (new TealSource(name, tl, ParseOrThrow(src, tl)), src);
                    }
                    else if (decl != null)
                    {
                        string src = LuaText.ReadFile(decl);
                        parsed[i] = (new DeclaredModule(name, decl, ParseLegacyOrThrow(src, decl), lua), src);
                    }
                    else parsed[i] = (new LuaSource(name, lua!), null);
                }
                catch (Exception e) when (e is CameoCompileException or IOException)
                {
                    // leave it to the ordinary (lazy) path, which reports the error where it belongs
                }
            });
            for (int index = 0; index < parsed.Length; index++)
            {
                var (module, src) = parsed[index];
                if (!module.HasValue) continue;
                _modules[found[index].Name] = module;
                if (src != null) pending.Add(src);
            }
        }
    }

    private Proto CompileChunk(Chunk chunk, bool typeCheck)
    {
        if (typeCheck)
        {
            var errors = CheckChunk(chunk);
            if (errors.Count > 0) throw new CameoCompileException(errors);
        }
        try
        {
            return BytecodeCompiler.Compile(chunk, intrinsics: typeCheck);
        }
        catch (InsufficientExecutionStackException)
        {
            throw new CameoCompileException(new[] { new Diagnostic(chunk.FileName, 1, 1, "program is too deeply nested for the available stack") });
        }
        catch (CameoSyntaxException e)
        {
            throw new CameoCompileException(new[] { e.Diagnostic });
        }
    }

    // ------------------------------------------------------------------ run

    public Value[] RunString(string source, string chunkName = "chunk", params ReadOnlySpan<Value> args)
    {
        Proto p = Compile(source, chunkName);
        return RunWatched(p, args);
    }

    public Value[] RunFile(string path, params string[] scriptArgs)
    {
        AddSearchDir(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
        var argt = new Table();
        argt.SetInt(0, Value.FromText(path));
        var argv = new Value[scriptArgs.Length];
        for (int i = 0; i < scriptArgs.Length; i++)
        {
            argt.SetInt(i + 1, Value.FromText(scriptArgs[i]));
            argv[i] = Value.FromText(scriptArgs[i]);
        }
        State.Globals.SetStr("arg", argt);
        State.ScriptArgs = scriptArgs; // what cli.parse reads
        Proto p = CompileFile(path);
        return RunWatched(p, argv);
    }

    // ------------------------------------------------------------------ modules

    private void AddSearchDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) dir = ".";
        if (!_searchDirs.Contains(dir)) _searchDirs.Insert(0, dir);
    }

    /// <summary>Locates and parses a module (cached). Returns null when it cannot be found.</summary>
    private Module? FindModule(string name)
    {
        if (_modules.TryGetValue(name, out Module? cached)) return cached;
        var (tl, decl, lua) = LocateModule(name);
        Module? found = null;
        if (tl != null) found = new TealSource(name, tl, ParseOrThrow(LuaText.ReadFile(tl), tl));
        else if (decl != null) found = new DeclaredModule(name, decl, ParseLegacyOrThrow(LuaText.ReadFile(decl), decl), lua);
        else if (lua != null) found = new LuaSource(name, lua);
        _modules[name] = found;
        return found;
    }

    // Per search directory: Teal source first; otherwise a Lua implementation (compiled
    // unchecked), typed by a declaration file when there is one.
    private (string? Tl, string? Decl, string? Lua) LocateModule(string name)
    {
        string rel = LuaText.Decode(name).Replace('.', System.IO.Path.DirectorySeparatorChar);
        List<string> dirs = [with(capacity: _searchDirs.Count + 1), .. _searchDirs];
        if (!dirs.Contains(".")) dirs.Add(".");
        foreach (string dir in dirs)
        {
            string? tl = FirstExisting(dir, rel + _sourceExtension, System.IO.Path.Combine(rel, "init" + _sourceExtension));
            string? decl = FirstExisting(dir, rel + ".d.tl");
            string? lua = FirstExisting(dir, rel + ".lua", System.IO.Path.Combine(rel, "init.lua"));
            if (tl != null || decl != null || lua != null) return (tl, decl, lua);
        }
        return (null, null, null);
    }

    private static string? FirstExisting(string dir, params string[] candidates)
    {
        foreach (string c in candidates)
        {
            string path = System.IO.Path.Combine(dir, c);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private Value LoadModule(LuaState L, string name)
    {
        Module? m;
        try
        {
            m = FindModule(name);
        }
        catch (CameoCompileException e)
        {
            throw new LuaException($"error loading module '{name}':\n\t{e.Message}");
        }
        if (!m.HasValue) throw new LuaException($"module '{name}' not found");
        Module module = m.Value;
        Proto p;
        string path;
        try
        {
            (p, path) = module switch
            {
                TealSource t => (CompileModule(t), t.Path),
                // Lua source is (nearly) a subset of Teal's syntax: compile it without type checking.
                DeclaredModule { LuaPath: { } lua } => (BytecodeCompiler.Compile(ParseLegacyOrThrow(LuaText.ReadFile(lua), lua)), lua),
                DeclaredModule d => throw new LuaException($"module '{name}' is a declaration file ({d.Path}) with no implementation"),
                LuaSource l => (BytecodeCompiler.Compile(ParseLegacyOrThrow(LuaText.ReadFile(l.Path), l.Path)), l.Path),
            };
        }
        catch (CameoCompileException e)
        {
            throw new LuaException($"error loading module '{name}':\n\t{e.Message}");
        }
        catch (CameoSyntaxException e)
        {
            throw new LuaException($"error loading module '{name}':\n\t{e.Message}");
        }
        Value[] r = L.Call(L.MakeFunction(p), name, Value.FromText(path));
        return r.Length > 0 ? r[0] : Value.Nil;
    }

    private Proto CompileModule(TealSource t)
    {
        if (Options.TypeCheck && !t.Checked)
        {
            var errors = CheckModule(t);
            if (errors.Count > 0) throw new CameoCompileException(errors);
        }
        return BytecodeCompiler.Compile(t.Ast, intrinsics: Options.TypeCheck);
    }

    // `load`: dynamically supplied code is compiled without static checking.
    private Value LoadChunk(LuaState L, string source, string chunkName)
    {
        try
        {
            string name = chunkName.StartsWith('@') || chunkName.StartsWith('=') ? chunkName.Substring(1) : "[string]";
            Chunk chunk = _language.Parse(source, name);
            return L.MakeFunction(BytecodeCompiler.Compile(chunk));
        }
        catch (CameoSyntaxException e)
        {
            throw new LuaException(Value.FromString(e.Diagnostic.ToString()), true);
        }
    }
}
