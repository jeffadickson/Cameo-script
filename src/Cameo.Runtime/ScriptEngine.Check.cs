using Cameo.Runtime.Syntax;
using Cameo.Runtime.Types;

namespace Cameo.Runtime;

// Type-checker wiring. Kept separate so the checker can be swapped or disabled.
public sealed partial class ScriptEngine
{
    private TypeChecker? _checker;
    private IUnitDimensionResolver? _unitDimensionResolver;
    private readonly List<(string Source, string Name, string? ReplaceGlobal)> _hostDeclarations = new();

    /// <summary>
    /// Installs a compile-time physical unit dimension resolver (used by <c>Cameo.Units</c> / <c>SharpUnits</c>)
    /// on this engine's type checker and any child worker engines.
    /// </summary>
    public void SetUnitDimensionResolver(IUnitDimensionResolver? resolver)
    {
        _unitDimensionResolver = resolver;
        _checker?.SetUnitDimensionResolver(resolver);
    }

    internal IUnitDimensionResolver? UnitDimensionResolver => _unitDimensionResolver;

    private void InitChecker()
    {
        // Created lazily: loading the standard-library declarations costs a little start-up
        // time that unchecked runs do not need to pay.
    }

    private TypeChecker Checker => _checker ??= CreateChecker();

    private TypeChecker CreateChecker()
    {
        var checker = new TypeChecker(FindModuleSource, _language.UsesTaggedResults, _unitDimensionResolver);
        foreach (var (src, name, replace) in _hostDeclarations)
            checker.AddDeclaration(src, name, replace);
        return checker;
    }

    internal void AddHostDeclaration(string declarationSource, string name = "<host>", string? replaceGlobal = null)
    {
        _hostDeclarations.Add((declarationSource, name, replaceGlobal));
        _checker?.AddDeclaration(declarationSource, name, replaceGlobal);
    }

    // Called by the checker when it meets require("name"). The same parsed chunk is later
    // compiled when the module is loaded at run time, so checker annotations carry over.
    private ModuleSource? FindModuleSource(string name)
    {
        Module? found = FindModule(name);
        if (!found.HasValue) return null;
        Module module = found.Value;
        return module switch
        {
            TealSource t => Checked(t),
            DeclaredModule d => new ModuleSource { Name = d.Name, Path = d.Path, Ast = d.Declarations, IsDeclarationFile = true },
            LuaSource l => new ModuleSource { Name = l.Name, Path = l.Path, IsUntyped = true },
        };

        static ModuleSource Checked(TealSource t)
        {
            t.Checked = true; // the checker checks every module it is handed
            return new ModuleSource { Name = t.Name, Path = t.Path, Ast = t.Ast };
        }
    }

    private IReadOnlyList<Diagnostic> CheckChunk(Chunk chunk)
    {
        CheckResult r = Checker.Check(chunk);
        Warnings.Clear();
        Warnings.AddRange(r.Warnings);
        return r.Errors;
    }

    private IReadOnlyList<Diagnostic> CheckModule(TealSource m)
    {
        m.Checked = true;
        CheckResult r = Checker.CheckModule(m.Name);
        Warnings.AddRange(r.Warnings);
        return r.Errors;
    }
}
