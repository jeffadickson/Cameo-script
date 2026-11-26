using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;

namespace Cameo;

/// <summary>Managed Cameo entry point. Syntax, module selection and workers share one language;
/// AST execution, libraries and lifetime services reuse Cameo.Runtime. Access on the owning thread.</summary>
public sealed class CameoEngine : IDisposable
{
    /// <summary>Creates a checked Cameo engine with the standard permission defaults.</summary>
    public CameoEngine() : this(new CameoRuntimeOptions()) { }
    /// <summary>Creates a Cameo engine with the supplied execution options.</summary>
    public CameoEngine(CameoRuntimeOptions options)
    {
        Runtime = new ScriptEngine(options, CameoLanguage.Instance);
        Cameo.Units.UnitsLibrary.Enable(Runtime);
        Cameo.SafeNumerics.SafeNumericsLibrary.Enable(Runtime);
        if (options.Libraries is not (Cameo.Runtime.Stdlib.CameoLibraries.Sandboxed or Cameo.Runtime.Stdlib.CameoLibraries.Hermetic))
        {
            Cameo.Jit.JitLibrary.Enable(Runtime);
            Cameo.Hdf5.Hdf5Library.Enable(Runtime);
            Cameo.Fst.FstLibrary.Enable(Runtime);
        }
    }

    /// <summary>Creates a strictly deterministic Starlark-style hermetic Cameo engine.</summary>
    public static CameoEngine CreateHermetic(long? maxSteps = null, IEnumerable<string>? modulePaths = null, bool typeCheck = true)
    {
        var opts = new CameoRuntimeOptions
        {
            TypeCheck = typeCheck,
            Libraries = Cameo.Runtime.Stdlib.CameoLibraries.Hermetic,
            AllowDebug = false,
            FilePolicy = FilePolicy.DenyAll,
            MaxSteps = maxSteps,
        };
        if (modulePaths != null) opts.ModulePaths.AddRange(modulePaths);
        return new CameoEngine(opts);
    }
    /// <summary>Shared engine, configured for Cameo; its low-level VM rules still apply.</summary>
    public ScriptEngine Runtime { get; }
    /// <summary>The owning VM state, for explicit host registration and immutable result transfer.</summary>
    public LuaState State => Runtime.State;
    /// <summary>Registers a typed .NET delegate as a global Cameo function and declares its type to the checker.</summary>
    public void Register(string name, Delegate fn, bool asFuture = false, bool threadSafe = false) =>
        Runtime.Register(name, fn, asFuture, threadSafe);
    /// <summary>Exposes a .NET object as a typed Cameo global (<c>[Expose]</c>/<c>[Hide]</c>) and registers its type with the checker.</summary>
    public void Expose(string name, object target) => Runtime.Expose(name, target);
    /// <summary>Converts a .NET value to a Cameo global and declares its inferred type to the checker.</summary>
    public void DefineValue(string name, object? value) => Runtime.DefineValue(name, value);
    /// <summary>Registers custom Teal/Cameo global type declarations directly with the static checker.</summary>
    public void DeclareGlobal(string declarationSource, string name = "<host>") => Runtime.DeclareGlobal(declarationSource, name);
    /// <summary>Checks UTF-16 host source, converting to the VM's byte-string representation.</summary>
    public IReadOnlyList<Diagnostic> Check(string source, string fileName = "chunk.cameo") => Runtime.Check(source, fileName);
    /// <summary>Checks a source file using Cameo module resolution.</summary>
    public IReadOnlyList<Diagnostic> CheckFile(string path) => Runtime.CheckFile(path);
    /// <summary>Compiles host source with Cameo syntax.</summary>
    public Proto Compile(string source, string fileName = "chunk.cameo") => Runtime.Compile(source, fileName);
    /// <summary>Runs source; throwing convenience API over the shared engine.</summary>
    public Value[] RunString(string source, string fileName = "chunk.cameo") => Runtime.RunString(source, fileName);
    /// <summary>Runs a Cameo file and supplies its script arguments.</summary>
    public Value[] RunFile(string path, params string[] args) => Runtime.RunFile(path, args);
    /// <summary>Runs source with explicit compile/runtime/abort/exit outcomes.</summary>
    public RunResult TryRunString(string source, string fileName = "chunk.cameo") => Runtime.TryRunString(source, fileName);
    /// <summary>Runs a source file with explicit outcomes.</summary>
    public RunResult TryRunFile(string path, params string[] args) => Runtime.TryRunFile(path, args);
    /// <summary>Releases run-owned resources; engine disposal is terminal.</summary>
    public void Dispose() => Runtime.Dispose();
}