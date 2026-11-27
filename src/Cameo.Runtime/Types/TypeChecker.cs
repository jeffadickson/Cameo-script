using System.Reflection;
using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Types;

internal sealed class VarInfo
{
    public string Name = "";
    /// <summary>Type of the variable as a value; null when the name is only a type.</summary>
    public TType? Type;
    /// <summary>Non-null when the name denotes a type.</summary>
    public TType? TypeDef;
    public string? Attrib;
    public bool IsGlobal;
    /// <summary>For flow-narrowed shadows: the declared (un-narrowed) type.</summary>
    public TType? Declared;
    public bool IsMacro;
    public bool IsForward;
    /// <summary>The declaring name node, for usage tracking (unused/shadowing warnings); null for
    /// flow-narrowed copies and untracked names.</summary>
    public Node? DeclNode;
    public int Line, Col;
    /// <summary>Parameter whose (union) type was taken from the expected callback type rather than
    /// written by the programmer; indexing it is not reported.</summary>
    public bool LooseUnion;
    // lazily resolved `type X = ...` alias
    public TypeNode? AliasNode;
    public Scope? AliasScope;
    public string? AliasFile;
    public bool Resolving;
    public List<TypeParam>? AliasParamNodes;
    public List<TypeVar>? AliasParams;
}

internal sealed class Scope
{
    public readonly Dictionary<string, VarInfo> Vars = new();
    public readonly Scope? Parent;
    public HashSet<string>? Labels;
    public HashSet<string>? UsedLabels;
    public bool FuncBoundary;
    public Scope(Scope? parent) { Parent = parent; }
    public VarInfo? Lookup(string name)
    {
        for (Scope? s = this; s != null; s = s.Parent)
            if (s.Vars.TryGetValue(name, out var v)) return v;
        return null;
    }
    public void Declare(VarInfo v)
    {
        // A flow-narrowed copy of a variable stays tied to the variable's declaration
        // (new declarations are tracked, and so already carry their own DeclNode).
        if (v.DeclNode == null && Lookup(v.Name) is { DeclNode: not null } prev && !v.IsMacro && v.TypeDef == null)
        {
            v.DeclNode = prev.DeclNode;
            v.Line = prev.Line;
            v.Col = prev.Col;
        }
        Vars[v.Name] = v;
    }
}

internal sealed class FuncCtx
{
    public FuncType Sig = null!;
    public bool Infer;
    public bool IsModule;
    public List<TType>? Inferred;
    // Receiving context is separate from inferred results: nested expression functions
    // need its parameter types without using it to erase source return arity.
    public TType? ExpressionExpected;
    public TType? VarArg;
    public bool HasVarArg;
    public TType? ModuleType;
}

/// <summary>
/// Type checker for Teal. Holds a global environment (preloaded with the standard prelude)
/// that persists across <see cref="Check"/> calls.
/// </summary>
public sealed partial class TypeChecker
{
    private readonly Func<string, ModuleSource?>? _finder;
    private readonly Scope _globals = new(null);
    private readonly Dictionary<string, CheckResult> _modules = new();
    private readonly HashSet<string> _modulesInProgress = new();

    // per-check state
    private string _file = "?";
    private bool _isCameo;
    private List<Diagnostic> _errors = new();
    private List<Diagnostic> _warnings = new();
    private Scope _scope;
    private readonly Stack<FuncCtx> _funcs = new();
    private HashSet<string> _reportedModules = new();
    private readonly Dictionary<IsExpr, TType> _isTypes = new(ReferenceEqualityComparer.Instance);
    private int _silent;

    // Identity captured from the prelude, unaffected by user types with the same spelling.
    private RecordType? _futureDefinition;
    private RecordType? _resultDefinition;
    private RecordType? _listDefinition, _dictDefinition, _queryDefinition, _floatArrayDefinition;
    private VarInfo? _okConstructor, _errConstructor;
    private RecordType? _stringLib;
    private RecordType? _metatableRec;
    private FuncType? _strMatch, _strFind, _strGmatch, _strUnpack, _strFormat;

    private readonly bool _cameoMode;
    private readonly HashSet<(RecordType, RecordType)> _structuralPairs = new();

    public TypeChecker(Func<string, ModuleSource?>? moduleFinder = null) : this(moduleFinder, false) { }

    internal TypeChecker(Func<string, ModuleSource?>? moduleFinder, bool taggedResults)
    {
        _finder = moduleFinder;
        _cameoMode = taggedResults;
        _scope = _globals;
        LoadPrelude(taggedResults);
    }

    /// <summary>Errors found while loading the built-in prelude (expected to be empty).</summary>
    public List<Diagnostic> PreludeErrors { get; } = new();

    private void LoadPrelude(bool taggedResults)
    {
        Assembly asm = typeof(TypeChecker).Assembly;
        var names = asm.GetManifestResourceNames().Where(n => n.EndsWith(".d.tl")).ToList();
        // prelude.d.tl -> stdlib.d.tl -> dotnet.d.tl -> extension declarations (units, safenum, cameo-result)
        static int PreludeOrder(string name) =>
            name.EndsWith("prelude.d.tl", StringComparison.Ordinal) ? 0
            : name.EndsWith("dotnet.d.tl", StringComparison.Ordinal) ? 1
            : name.EndsWith("stdlib.d.tl", StringComparison.Ordinal) ? 2
            : 3;
        names.Sort((a, b) =>
        {
            int c = PreludeOrder(a).CompareTo(PreludeOrder(b));
            return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
        });
        foreach (string n in names)
        {
            if (!taggedResults && n.EndsWith(".cameo-result.d.tl", StringComparison.Ordinal)) continue;
            using Stream s = asm.GetManifestResourceStream(n)!;
            using var r = new StreamReader(s);
            string src = r.ReadToEnd();
            string fname = n.Contains("prelude.d.tl") ? "prelude.d.tl" : "stdlib.d.tl";
            try
            {
                Chunk c = Parser.Parse(src, fname);
                CheckResult res = Check(c);
                PreludeErrors.AddRange(res.Errors);
            }
            catch (CameoSyntaxException e)
            {
                PreludeErrors.Add(e.Diagnostic);
            }
        }
        _futureDefinition = _globals.Lookup("Future")?.TypeDef as RecordType;
        _listDefinition = _globals.Lookup("List")?.TypeDef as RecordType;
        _dictDefinition = _globals.Lookup("Dict")?.TypeDef as RecordType;
        _queryDefinition = _globals.Lookup("Query")?.TypeDef as RecordType;
        _floatArrayDefinition = _globals.Lookup("FloatArray")?.TypeDef as RecordType;
        if (taggedResults)
        {
            _resultDefinition = _globals.Lookup("Result")?.TypeDef as RecordType;
            _okConstructor = _globals.Lookup("ok");
            _errConstructor = _globals.Lookup("err");
        }
        _stringLib = _globals.Lookup("string")?.TypeDef as RecordType;
        if (_stringLib != null)
        {
            _strMatch = LookupField(_stringLib, "match") as FuncType;
            _strFind = LookupField(_stringLib, "find") as FuncType;
            _strGmatch = LookupField(_stringLib, "gmatch") as FuncType;
            _strUnpack = LookupField(_stringLib, "unpack") as FuncType;
            _strFormat = LookupField(_stringLib, "format") as FuncType;
        }
        _metatableRec = _globals.Lookup("metatable")?.TypeDef as RecordType;
    }

    internal void AddDeclaration(string declarationSource, string fileName = "<host>", string? replaceGlobal = null)
    {
        if (replaceGlobal != null) _globals.Vars.Remove(replaceGlobal);
        Chunk chunk = Parser.Parse(declarationSource, fileName);
        CheckResult res = Check(chunk);
        if (res.Errors.Count > 0)
            throw new CameoCompileException(res.Errors);
    }

    // ------------------------------------------------------------------ public API

    public CheckResult Check(Chunk chunk)
    {
        // save state (Check may be re-entered through require)
        string sFile = _file;
        bool sIsCameo = _isCameo;
        var sErr = _errors;
        var sWarn = _warnings;
        Scope sScope = _scope;
        var sReported = _reportedModules;
        var sPragmas = _arityPragmas;
        var sTracked = _tracked;
        var sTrackedOrder = _trackedOrder;
        var sUsed = _usedDecls;
        var sAssigned = _assignedDecls;
        var sLabels = _labelDecls;
        _tracked = new(ReferenceEqualityComparer.Instance);
        _trackedOrder = new();
        _usedDecls = new(ReferenceEqualityComparer.Instance);
        _assignedDecls = new(ReferenceEqualityComparer.Instance);
        _labelDecls = new();
        int sSilent = _silent;
        var sFuncs = _funcs.ToArray();
        bool outer = _funcs.Count == 0;
        _funcs.Clear();

        var result = new CheckResult();
        _file = chunk.FileName;
        _isCameo = chunk.IsCameo;
        _errors = result.Errors;
        _warnings = result.Warnings;
        LoadPragmas(chunk);
        _scope = new Scope(_globals);
        _silent = 0;
        if (outer) _reportedModules = new HashSet<string>();
        var ctx = new FuncCtx { IsModule = true, HasVarArg = true, VarArg = PrimType.Any, Sig = new FuncType() };
        _funcs.Push(ctx);
        try
        {
            CheckBlockIn(chunk.Body, _scope);
            if (chunk.CameoExports is { Count: > 0 } exports)
            {
                var modRec = new RecordType { Name = chunk.FileName, File = _file, Filled = true };
                foreach (CameoExportItem exp in exports)
                {
                    MarkUsed(_scope, exp.Name);
                    VarInfo? vi = _scope.Lookup(exp.Name);
                    if (vi == null) continue;
                    if (vi.Type != null)
                    {
                        modRec.Fields[exp.Name] = vi.Type;
                        modRec.FieldOrder.Add(exp.Name);
                    }
                    if (vi.TypeDef != null)
                        modRec.Nested[exp.Name] = vi.TypeDef;
                }
                ctx.ModuleType = modRec;
            }
        }
        catch (Exception e) when (e is not CameoSyntaxException)
        {
            // A checker bug must never block a valid program from running.
            result.Warnings.Add(new Diagnostic(_file, 1, 1, "internal type checker error: " + e.GetType().Name + ": " + e.Message, true));
        }
        result.ModuleType = ctx.ModuleType;
        try
        {
            // declaration files only describe things: nothing in them is "unused"
            if (!chunk.FileName.EndsWith(".d.tl", StringComparison.Ordinal)) ReportUnused();
        }
        catch (Exception e) when (e is not CameoSyntaxException)
        {
            result.Warnings.Add(new Diagnostic(_file, 1, 1, "internal type checker error: " + e.GetType().Name + ": " + e.Message, true));
        }
        _tracked = sTracked;
        _trackedOrder = sTrackedOrder;
        _usedDecls = sUsed;
        _assignedDecls = sAssigned;
        _labelDecls = sLabels;

        _funcs.Clear();
        for (int i = sFuncs.Length - 1; i >= 0; i--) _funcs.Push(sFuncs[i]);
        _file = sFile;
        _isCameo = sIsCameo;
        _errors = sErr;
        _warnings = sWarn;
        _scope = sScope;
        _reportedModules = sReported;
        _arityPragmas = sPragmas;
        _silent = sSilent;
        return result;
    }

    public CheckResult CheckModule(string name)
    {
        if (_modules.TryGetValue(name, out var cached)) return cached;
        var res = new CheckResult();
        if (_modulesInProgress.Contains(name))
        {
            res.ModuleType = PrimType.Unknown; // circular require
            return res;
        }
        ModuleSource? src = null;
        try { src = _finder?.Invoke(name); }
        catch (CameoCompileException e)
        {
            // Engine module parsing wraps syntax diagnostics. These are user source
            // errors, not checker bugs or warnings that can certify a valid program.
            res.Errors.AddRange(e.Diagnostics);
            _modules[name] = res;
            return res;
        }
        catch (CameoSyntaxException e)
        {
            res.Errors.Add(e.Diagnostic);
            _modules[name] = res;
            return res;
        }
        if (src == null)
        {
            string? builtin = name switch
            {
                "units" or "SharpUnits" or "sharpunits" => "SharpUnits",
                "safenum" => "safenum",
                _ => null,
            };
            if (builtin != null && _globals.Lookup(builtin)?.Type is { } bt)
            {
                res.ModuleType = bt;
                _modules[name] = res;
                return res;
            }
            res.Errors.Add(new Diagnostic(_file, 1, 1, $"module not found: '{name}'"));
            res.ModuleType = null;
            _modules[name] = res;
            return res;
        }
        if (src.IsUntyped)
        {
            res.ModuleType = PrimType.Unknown;
            _modules[name] = res;
            return res;
        }
        _modulesInProgress.Add(name);
        try { res = Check(src.Ast); }
        finally { _modulesInProgress.Remove(name); }
        _modules[name] = res;
        return res;
    }

    // ------------------------------------------------------------------ diagnostics

    private void Err(Node at, string msg)
    {
        if (_silent > 0) { _silentErrors++; return; }
        // the same construct may be visited twice (e.g. lazily resolved declarations)
        foreach (Diagnostic d in _errors)
            if (d.Line == at.Line && d.Col == at.Col && d.Message == msg && d.File == _file) return;
        _errors.Add(new Diagnostic(_file, at.Line, at.Col, msg));
    }

    // ---- usage tracking: unused and shadowing warnings ---------------------------------

    private sealed class Tracked
    {
        public VarInfo Var = null!;
        public Node At = null!;
        public string Kind = "variable";
        public string File = "";
    }
    private Dictionary<Node, Tracked> _tracked = new(ReferenceEqualityComparer.Instance);
    private List<Tracked> _trackedOrder = new();
    private HashSet<Node> _usedDecls = new(ReferenceEqualityComparer.Instance);
    private HashSet<Node> _assignedDecls = new(ReferenceEqualityComparer.Instance);
    private List<(LabelStat Label, Scope Scope)> _labelDecls = new();

    // Finds the tracked declaration a name refers to (looking through flow-narrowed copies).
    private static VarInfo? DeclOf(Scope scope, string name)
    {
        for (Scope? s = scope; s != null; s = s.Parent)
            if (s.Vars.TryGetValue(name, out var v))
            {
                if (v.DeclNode != null) return v;
                if (v.IsGlobal) return null;
            }
        return null;
    }

    private void MarkUsed(Scope scope, string name)
    {
        if (DeclOf(scope, name) is { DeclNode: { } d }) _usedDecls.Add(d);
    }

    private void MarkAssigned(Scope scope, string name)
    {
        if (DeclOf(scope, name) is { DeclNode: { } d }) _assignedDecls.Add(d);
    }

    /// <summary>Registers a local declaration (call before adding it to the scope).</summary>
    private void Track(Scope scope, VarInfo v, Node nameAt, string kind, Node? posAt = null, bool shadowCheck = true)
    {
        Node pos = posAt ?? nameAt;
        v.DeclNode = nameAt;
        v.Line = pos.Line;
        v.Col = pos.Col;
        if (_silent > 0) return;
        if (_tracked.TryGetValue(nameAt, out Tracked? again))
        {
            again.Var = v; // the same declaration checked a second time
            return;
        }
        if (shadowCheck && !v.Name.StartsWith('_') && v.Name != "self")
        {
            VarInfo? prev = DeclOf(scope, v.Name);
            if (prev != null && !ReferenceEquals(prev.DeclNode, nameAt))
                Warn(pos, $"{(kind == "function" ? "function" : "variable")} shadows previous declaration of '{v.Name}' (originally declared at {prev.Line}:{prev.Col})");
        }
        var t = new Tracked { Var = v, At = pos, Kind = kind, File = _file };
        _tracked[nameAt] = t;
        _trackedOrder.Add(t);
    }

    private void ReportUnused()
    {
        foreach (Tracked t in _trackedOrder)
        {
            VarInfo v = t.Var;
            if (v.Name.StartsWith('_') || v.DeclNode == null || _usedDecls.Contains(v.DeclNode)) continue;
            string msg;
            if (t.Kind == "type")
            {
                msg = $"unused type {v.Name}: type {(v.TypeDef != null ? Disp(v.TypeDef) : v.Name)}";
            }
            else
            {
                TType? ty = v.Declared ?? v.Type;
                string tys = ty == null ? "" : Disp(ty);
                string kind = t.Kind == "variable" && ty is FuncType or PolyType ? "function" : t.Kind;
                if (_assignedDecls.Contains(v.DeclNode) && kind == "variable") msg = $"variable {v.Name} (of type {tys}) is never read";
                else msg = ty == null || IsUnknown(ty) ? $"unused {kind} {v.Name}" : $"unused {kind} {v.Name}: {tys}";
            }
            _warnings.Add(new Diagnostic(t.File, t.At.Line, t.At.Col, msg, true));
        }
        foreach (var (l, s) in _labelDecls)
            if (s.UsedLabels == null || !s.UsedLabels.Contains(l.Name))
                _warnings.Add(new Diagnostic(_file, l.Line, l.Col, $"unused label ::{l.Name}::", true));
    }

    private void Warn(Node at, string msg)
    {
        if (_silent > 0) return;
        foreach (Diagnostic d in _warnings)
            if (d.Line == at.Line && d.Col == at.Col && d.Message == msg && d.File == _file) return;
        _warnings.Add(new Diagnostic(_file, at.Line, at.Col, msg, true));
    }

    private int _silentErrors;

    /// <summary>Runs an action with error reporting suppressed; returns the number of errors it would have reported.</summary>
    private int Silently(Action a)
    {
        int saved = _silentErrors;
        _silentErrors = 0;
        _silent++;
        try { a(); }
        finally { _silent--; }
        int n = _silentErrors;
        _silentErrors = saved;
        return n;
    }

    private static string Disp(TType t) => t.Show();

    private static bool IsNominal(TType t) => t is RecordType { IsAnonymous: false } || t is InstType || t is EnumType;

    /// <summary>The "got X, expected Y" part of a type mismatch message, worded as tl does.</summary>
    private static string Mismatch(TType from, TType to)
    {
        if (to is PolyType && from is not PolyType) return "cannot match against all alternatives of the polymorphic type";
        if (from is PolyType && to is not PolyType) return "cannot match against any alternatives of the polymorphic type";
        if (from is FuncType ff && to is FuncType tf && !ff.IsBare && !tf.IsBare
            && ff.Returns.Count < tf.Returns.Count && !ff.RetVarArg && !tf.RetVarArg)
            return $"incompatible number of returns: got {ff.Returns.Count} ({string.Join(", ", ff.Returns.Select(Disp))}), expected {tf.Returns.Count} ({string.Join(", ", tf.Returns.Select(Disp))})";
        if (to is EnumType && from is StringLitType) return $"{Disp(from)} is not a member of {Disp(to)}";
        if ((IsNominal(from) && IsNominal(to)) || (to is EnumType && IsPrim(from, Prim.String)))
            return $"{Disp(from)} is not a {Disp(to)}";
        return $"got {Disp(from)}, expected {Disp(to)}";
    }

    /// <summary>Scope lookup that resolves lazily declared type aliases.</summary>
    private VarInfo? Look(Scope scope, string name)
    {
        VarInfo? v = scope.Lookup(name);
        if (v != null && v.AliasNode != null && v.TypeDef == null) Force(v);
        return v;
    }

    private void Force(VarInfo v)
    {
        if (v.AliasNode == null || v.TypeDef != null || v.Resolving) return;
        v.Resolving = true;
        string sFile = _file;
        _file = v.AliasFile ?? _file;
        try
        {
            TypeNode node = v.AliasNode;
            Scope scope = v.AliasScope ?? _scope;
            if (v.AliasParamNodes != null)
            {
                scope = new Scope(scope);
                v.AliasParams = DeclareTypeParams(v.AliasParamNodes, scope);
            }
            TType t = node is RequireTypeNode rq ? ResolveRequireType(rq)
                : node is NameTypeNode nd ? ResolveNameType(nd, scope, true)
                : ResolveType(node, scope);
            v.TypeDef = t;
            if (t is RecordType { IsInterface: false } || t is InstType { Def.IsInterface: false }) v.Type = t;
        }
        finally
        {
            v.Resolving = false;
            _file = sFile;
        }
    }

    // ---- `--#pragma arity on|off` (read from the source file, since comments are not in the AST)
    private List<(int line, bool off)>? _arityPragmas;

    private void LoadPragmas(Chunk chunk)
    {
        _arityPragmas = null;
        string? src = chunk.Source;
        string prefix = chunk.IsCameo ? "//#pragma" : "--#pragma";
        if (src == null || !src.Contains(prefix, StringComparison.Ordinal)) return;
        int ln = 0;
        foreach (string line in src.Split('\n'))
        {
            ln++;
            string t = line.TrimStart();
            if (!t.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int col = line.Length - t.Length + 1;
            string[] parts = t.Substring(prefix.Length).Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { _errors.Add(new Diagnostic(_file, ln, col, "invalid pragma")); continue; }
            if (parts[0] != "arity")
            {
                _errors.Add(new Diagnostic(_file, ln, col, "invalid pragma: " + parts[0]));
                continue;
            }
            if (parts.Length >= 2 && (parts[1] == "on" || parts[1] == "off"))
                (_arityPragmas ??= new()).Add((ln, parts[1] == "off"));
            else
                _errors.Add(new Diagnostic(_file, ln, col, "invalid value for pragma 'arity': " + (parts.Length >= 2 ? parts[1] : "")));
        }
    }

    private bool ArityOff(int line)
    {
        if (_arityPragmas == null) return false;
        bool off = false;
        foreach (var (l, o) in _arityPragmas)
        {
            if (l > line) break;
            off = o;
        }
        return off;
    }

    // ------------------------------------------------------------------ type helpers

    private static bool IsUnknown(TType t) => t is PrimType { Kind: Prim.Unknown };
    private static bool IsAny(TType t) => t is PrimType { Kind: Prim.Any };
    private static bool IsNil(TType t) => t is PrimType { Kind: Prim.Nil };
    private static bool IsPrim(TType t, Prim k) => t is PrimType p && p.Kind == k;
    private static bool IsNumeric(TType t) => t is PrimType { Kind: Prim.Integer or Prim.Number };
    private static bool IsStringLike(TType t) => t is StringLitType || t is EnumType || IsPrim(t, Prim.String);

    /// <summary>Drops literal-ness: the type a variable gets when initialised with this value.</summary>
    private static TType Widen(TType t) => t is StringLitType ? PrimType.String : t;

    private static TType MakeUnion(IEnumerable<TType> types)
    {
        var list = new List<TType>();
        foreach (TType t0 in types)
        {
            foreach (TType t in t0 is UnionType u ? u.Members : new List<TType> { t0 })
            {
                TType w = Widen(t);
                if (IsAny(w)) return PrimType.Any;
                if (IsUnknown(w)) return PrimType.Unknown;
                if (IsNil(w)) continue;
                if (!list.Any(x => Same(x, w))) list.Add(w);
            }
        }
        // integer is absorbed by number
        if (list.Any(x => IsPrim(x, Prim.Number))) list.RemoveAll(x => IsPrim(x, Prim.Integer));
        if (list.Count == 0) return PrimType.Nil;
        return list.Count == 1 ? list[0] : new UnionType(list);
    }

    private static bool Same(TType a, TType b)
    {
        if (ReferenceEquals(a, b)) return true;
        switch (a)
        {
            case PrimType pa: return b is PrimType pb && pa.Kind == pb.Kind;
            case StringLitType sa: return b is StringLitType sb && sa.Value == sb.Value;
            case ArrayType aa: return b is ArrayType ab && Same(aa.Elem, ab.Elem);
            case MapType ma: return b is MapType mb && Same(ma.Key, mb.Key) && Same(ma.Value, mb.Value);
            case TupleType ta:
                return b is TupleType tb && ta.Elems.Count == tb.Elems.Count && ta.Elems.Zip(tb.Elems).All(p => Same(p.First, p.Second));
            case InstType ia:
                return b is InstType ib && ia.Def == ib.Def && ia.Args.Count == ib.Args.Count && ia.Args.Zip(ib.Args).All(p => Same(p.First, p.Second));
            case UnionType ua:
                return b is UnionType ub && ua.Members.Count == ub.Members.Count && ua.Members.All(m => ub.Members.Any(n => Same(m, n)));
            case FuncType fa:
                if (b is not FuncType fb) return false;
                if (fa.IsBare || fb.IsBare) return fa.IsBare == fb.IsBare;
                return fa.Params.Count == fb.Params.Count && fa.Returns.Count == fb.Returns.Count
                    && fa.Params.Zip(fb.Params).All(p => Same(p.First, p.Second))
                    && fa.Returns.Zip(fb.Returns).All(p => Same(p.First, p.Second));
            default: return false;
        }
    }

    private static bool IsTableLike(TType t) => t switch
    {
        ArrayType or TupleType or MapType or RecordType or InstType or EmptyTableType => true,
        UnionType u => u.Members.Any(IsTableLike),
        TypeVar => true,
        _ => false,
    };

    // ------------------------------------------------------------------ substitution

    private static TType Subst(TType t, Dictionary<TypeVar, TType>? map)
    {
        if (map == null || map.Count == 0) return t;
        switch (t)
        {
            case TypeVar tv:
                return map.TryGetValue(tv, out var r) ? r : t;
            case ArrayType a:
                {
                    TType e = Subst(a.Elem, map);
                    return ReferenceEquals(e, a.Elem) ? t : new ArrayType(e);
                }
            case MapType m:
                {
                    TType k = Subst(m.Key, map), v = Subst(m.Value, map);
                    return ReferenceEquals(k, m.Key) && ReferenceEquals(v, m.Value) ? t : new MapType(k, v);
                }
            case TupleType tu:
                {
                    var l = SubstList(tu.Elems, map);
                    return ReferenceEquals(l, tu.Elems) ? t : new TupleType(l);
                }
            case UnionType u:
                {
                    var l = SubstList(u.Members, map);
                    return ReferenceEquals(l, u.Members) ? t : MakeUnion(l);
                }
            case InstType i:
                {
                    var l = SubstList(i.Args, map);
                    return ReferenceEquals(l, i.Args) ? t : new InstType(i.Def, l);
                }
            case FuncType f:
                {
                    if (f.IsBare) return t;
                    var ps = SubstList(f.Params, map);
                    var rs = SubstList(f.Returns, map);
                    TType? va = f.VarArg == null ? null : Subst(f.VarArg, map);
                    if (ReferenceEquals(ps, f.Params) && ReferenceEquals(rs, f.Returns) && ReferenceEquals(va, f.VarArg)) return t;
                    return new FuncType
                    {
                        TypeParams = f.TypeParams, Params = ps, Optional = f.Optional, VarArg = va, Returns = rs,
                        RetVarArg = f.RetVarArg, IsMethod = f.IsMethod, SelfNamed = f.SelfNamed, IsMacroExp = f.IsMacroExp, Macro = f.Macro, MacroFile = f.MacroFile, LaxArity = f.LaxArity,
                    };
                }
            case PolyType p:
                {
                    var np = new PolyType();
                    bool changed = false;
                    foreach (FuncType o in p.Overloads)
                    {
                        var s = (FuncType)Subst(o, map);
                        if (!ReferenceEquals(s, o)) changed = true;
                        np.Overloads.Add(s);
                    }
                    return changed ? np : t;
                }
            default:
                return t;
        }
    }

    private static List<TType> SubstList(List<TType> list, Dictionary<TypeVar, TType> map)
    {
        List<TType>? res = null;
        for (int i = 0; i < list.Count; i++)
        {
            TType s = Subst(list[i], map);
            if (res == null && !ReferenceEquals(s, list[i]))
            {
                res = new List<TType>(list.Count);
                for (int j = 0; j < i; j++) res.Add(list[j]);
            }
            res?.Add(s);
        }
        return res ?? list;
    }

    /// <summary>Record definition + type-argument substitution for a record-like type.</summary>
    private bool AsRecord(TType t, out RecordType rec, out Dictionary<TypeVar, TType>? map)
    {
        map = null;
        if (t is RecordType r)
        {
            rec = r;
            EnsureFilled(r);
            return true;
        }
        if (t is InstType i)
        {
            rec = i.Def;
            EnsureFilled(rec);
            if (rec.TypeParams != null)
            {
                map = new Dictionary<TypeVar, TType>();
                for (int k = 0; k < rec.TypeParams.Count; k++)
                    map[rec.TypeParams[k]] = k < i.Args.Count ? i.Args[k] : PrimType.Unknown;
            }
            return true;
        }
        rec = null!;
        return false;
    }

    private TType? LookupField(TType t, string name, bool meta = false, int depth = 0)
    {
        if (depth > 20 || !AsRecord(t, out var rec, out var map)) return null;
        var dict = meta ? rec.MetaFields : rec.Fields;
        if (dict.TryGetValue(name, out var ft)) return Subst(ft, map);
        foreach (TType it in rec.Interfaces)
        {
            TType? r = LookupField(Subst(it, map), name, meta, depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    private TType? ArrayElemOf(TType t, int depth = 0)
    {
        if (depth > 20 || !AsRecord(t, out var rec, out var map)) return null;
        if (rec.ArrayElem != null) return Subst(rec.ArrayElem, map);
        foreach (TType it in rec.Interfaces)
        {
            TType? r = ArrayElemOf(Subst(it, map), depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>Tuple part of a record (own or inherited; the longest one wins).</summary>
    private List<TType>? TupleElemsOf(TType t, int depth = 0)
    {
        if (depth > 20 || !AsRecord(t, out var rec, out var map)) return null;
        List<TType>? best = rec.TupleElems?.Select(x => Subst(x, map)).ToList();
        foreach (TType it in rec.Interfaces)
        {
            List<TType>? r = TupleElemsOf(Subst(it, map), depth + 1);
            if (r != null && (best == null || r.Count > best.Count)) best = r;
        }
        return best;
    }

    private string? WhereSourceOf(TType t, out RecordType? owner, int depth = 0)
    {
        owner = null;
        if (depth > 20 || !AsRecord(t, out var rec, out _)) return null;
        if (rec.WhereSource != null) { owner = rec; return rec.WhereSource; }
        foreach (TType it in rec.Interfaces)
        {
            string? r = WhereSourceOf(it, out owner, depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    private bool IsUserdataType(TType t, int depth = 0)
    {
        if (depth > 20 || !AsRecord(t, out var rec, out _)) return false;
        if (rec.IsUserdata) return true;
        foreach (TType it in rec.Interfaces) if (IsUserdataType(it, depth + 1)) return true;
        return false;
    }

    private IEnumerable<string> AllFieldNames(TType t, int depth = 0)
    {
        if (depth > 20 || !AsRecord(t, out var rec, out _)) yield break;
        foreach (string f in rec.FieldOrder) yield return f;
        foreach (TType it in rec.Interfaces)
            foreach (string f in AllFieldNames(it, depth + 1)) yield return f;
    }

    // ------------------------------------------------------------------ assignability

    private bool Assignable(TType from, TType to, Dictionary<TypeVar, TType?>? infer = null, int depth = 0)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        if (ReferenceEquals(from, to)) return true;
        if (depth > 40) return true;
        if (to is TypeVar tv && infer != null && infer.ContainsKey(tv))
        {
            TType? cur = infer[tv];
            if (cur == null)
            {
                if (IsUnknown(from) || from is EmptyTableType) return true;
                if (from is TypeVar ftv && infer.ContainsKey(ftv) && !ReferenceEquals(ftv, tv)) return true;
                if (tv.Constraint != null && !Assignable(from, tv.Constraint, infer, depth + 1)) return false;
                infer[tv] = Widen(from);
                return true;
            }
            if (Assignable(from, cur, null, depth + 1)) return true;
            if (Assignable(cur, from, null, depth + 1)) { infer[tv] = Widen(from); return true; }
            // e.g. heterogeneous arguments to T...: widen to a union when possible
            if (IsNil(cur)) { infer[tv] = Widen(from); return true; }
            return false;
        }
        if (from is TypeVar fv && infer != null && infer.ContainsKey(fv))
        {
            TType? cur = infer[fv];
            return cur == null || Assignable(cur, to, infer, depth + 1);
        }
        if (IsUnknown(from) || IsUnknown(to)) return true;
        if (IsAny(to)) return true;
        if (IsNil(from)) return true;
        if (from is EmptyTableType) return IsTableLike(to);
        if (from is UnionType fu)
        {
            if (to is UnionType tu0 && fu.Members.All(m => tu0.Members.Any(n => Assignable(m, n, infer, depth + 1)))) return true;
            return fu.Members.All(m => Assignable(m, to, infer, depth + 1));
        }
        if (to is UnionType tu)
        {
            // prefer an exact match so inference binds the right member
            foreach (TType m in tu.Members) if (Same(Widen(from), m)) return true;
            foreach (TType m in tu.Members) if (Assignable(from, m, infer, depth + 1)) return true;
            return false;
        }
        if (IsAny(from)) return false;
        if (from is TypeVar ftv2 && to is not TypeVar)
        {
            return ftv2.Constraint != null && Assignable(ftv2.Constraint, to, infer, depth + 1);
        }

        switch (to)
        {
            case PrimType p:
                switch (p.Kind)
                {
                    case Prim.Number: return IsNumeric(from);
                    case Prim.Integer: return IsPrim(from, Prim.Integer);
                    case Prim.String: return IsStringLike(from);
                    case Prim.Boolean: return IsPrim(from, Prim.Boolean);
                    case Prim.Thread: return IsPrim(from, Prim.Thread);
                    default: return false;
                }
            case StringLitType:
                return IsStringLike(from);
            case EnumType e:
                if (from is StringLitType sl) return e.Values.Contains(sl.Value);
                if (from is EnumType fe) return fe.Values.All(e.Values.Contains);
                return false;
            case ArrayType a:
                if (from is ArrayType fa) return Assignable(fa.Elem, a.Elem, infer, depth + 1);
                if (from is TupleType ft)
                {
                    if (a.Elem is TypeVar atv && infer != null && infer.ContainsKey(atv) && infer[atv] == null)
                        return Assignable(MakeUnion(ft.Elems), a.Elem, infer, depth + 1);
                    return ft.Elems.All(x => Assignable(x, a.Elem, infer, depth + 1));
                }
                {
                    TType? el = ArrayElemOf(from);
                    if (el != null) return Assignable(el, a.Elem, infer, depth + 1);
                }
                return false;
            case TupleType t:
                if (from is TupleType ftu)
                {
                    if (ftu.Elems.Count > t.Elems.Count) return false;
                    for (int i = 0; i < ftu.Elems.Count; i++)
                        if (!Assignable(ftu.Elems[i], t.Elems[i], infer, depth + 1)) return false;
                    return true;
                }
                if (from is ArrayType fa2) return t.Elems.All(x => Assignable(fa2.Elem, x, infer, depth + 1));
                if (TupleElemsOf(from) is { } rte)
                {
                    if (rte.Count > t.Elems.Count) return false;
                    for (int i = 0; i < rte.Count; i++)
                        if (!Assignable(rte[i], t.Elems[i], infer, depth + 1)) return false;
                    return true;
                }
                return false;
            case MapType m:
                if (from is MapType fm) return Assignable(fm.Key, m.Key, infer, depth + 1) && Assignable(fm.Value, m.Value, infer, depth + 1);
                if (from is ArrayType fa3) return Assignable(PrimType.Integer, m.Key, infer, depth + 1) && Assignable(fa3.Elem, m.Value, infer, depth + 1);
                if (from is TupleType ft3) return Assignable(PrimType.Integer, m.Key, infer, depth + 1) && ft3.Elems.All(x => Assignable(x, m.Value, infer, depth + 1));
                if (AsRecord(from, out var frec, out _))
                {
                    if (!Assignable(PrimType.String, m.Key, infer, depth + 1)) return false;
                    if (m.Value is TypeVar vtv && infer != null && infer.ContainsKey(vtv) && infer[vtv] == null)
                    {
                        // pairs() over a record: values are of mixed types
                        var ftypes = AllFieldNames(from).Select(n => LookupField(from, n)!).ToList();
                        TType common = ftypes.Count > 0 && ftypes.All(x => Same(Widen(x), Widen(ftypes[0]))) ? Widen(ftypes[0]) : PrimType.Any;
                        infer[vtv] = common;
                        return true;
                    }
                    foreach (string n in AllFieldNames(from))
                        if (!Assignable(LookupField(from, n)!, m.Value, infer, depth + 1)) return false;
                    return true;
                }
                return false;
            case FuncType f:
                if (from is FuncType ff) return FuncAssignable(ff, f, infer, depth + 1);
                if (from is PolyType fp) return f.IsBare || fp.Overloads.Any(o => FuncAssignable(o, f, infer, depth + 1));
                if (AsRecord(from, out _, out _) && LookupField(from, "__call", true) != null) return true;
                return false;
            case PolyType pt:
                if (from is FuncType ff2) return pt.Overloads.Any(o => FuncAssignable(ff2, o, infer, depth + 1));
                if (from is PolyType) return true;
                return false;
            case RecordType or InstType:
                return RecordAssignable(from, to, infer, depth + 1);
            case TypeVar ttv:
                if (from is TypeVar f2) return ReferenceEquals(f2, ttv);
                // a value satisfying the constraint is accepted for a constrained type variable
                return ttv.Constraint != null && Assignable(from, ttv.Constraint, infer, depth + 1);
            case EmptyTableType:
                return IsTableLike(from);
        }
        return false;
    }

    private bool RecordAssignable(TType from, TType to, Dictionary<TypeVar, TType?>? infer, int depth)
    {
        if (!AsRecord(to, out var trec, out _)) return false;
        if (trec.IsForward()) return true;
        if (from is RecordType { IsAnonymous: true } anon)
        {
            foreach (string n in anon.FieldOrder)
            {
                TType? tf = LookupField(to, n);
                if (tf == null) return false;
                if (!Assignable(anon.Fields[n], tf, infer, depth + 1)) return false;
            }
            if (anon.ArrayElem != null)
            {
                TType? te = ArrayElemOf(to);
                if (te == null || !Assignable(anon.ArrayElem, te, infer, depth + 1)) return false;
            }
            return true;
        }
        if (!AsRecord(from, out var frec, out var fmap)) return false;
        if (frec.IsForward()) return true;
        if (ReferenceEquals(frec, trec))
        {
            if (from is InstType fi && to is InstType ti)
            {
                for (int i = 0; i < Math.Min(fi.Args.Count, ti.Args.Count); i++)
                    if (!Assignable(fi.Args[i], ti.Args[i], infer, depth + 1)) return false;
            }
            return true;
        }
        foreach (TType it in frec.Interfaces)
            if (RecordAssignable(Subst(it, fmap), to, infer, depth + 1)) return true;
        if (_cameoMode && trec.IsInterface && StructurallySatisfies(from, to, frec, trec, infer, depth))
            return true;
        return false;
    }

    private bool StructurallySatisfies(TType from, TType to, RecordType frec, RecordType trec, Dictionary<TypeVar, TType?>? infer, int depth)
    {
        if (depth > 20) return false;
        var key = (frec, trec);
        if (!_structuralPairs.Add(key)) return true;
        try
        {
            TType? reqArr = ArrayElemOf(to);
            if (reqArr != null)
            {
                TType? gotArr = ArrayElemOf(from);
                if (gotArr == null || !Assignable(gotArr, reqArr, infer, depth + 1)) return false;
            }
            foreach (string member in AllFieldNames(to).Distinct())
            {
                TType? req = LookupField(to, member);
                if (req == null) continue;
                TType? got = LookupField(from, member);
                if (got == null) return false;
                if (req is FuncType reqFn && got is FuncType gotFn)
                {
                    if (reqFn.SelfNamed != gotFn.SelfNamed) return false;
                    if (reqFn.SelfNamed)
                    {
                        if (!MethodSigAssignable(gotFn, reqFn, infer, depth + 1)) return false;
                        continue;
                    }
                }
                if (!Assignable(got, req, infer, depth + 1)) return false;
            }
            return true;
        }
        finally
        {
            _structuralPairs.Remove(key);
        }
    }

    private bool MethodSigAssignable(FuncType from, FuncType to, Dictionary<TypeVar, TType?>? infer, int depth)
    {
        // Both functions are receiver methods (param 0 is `self`); compare explicit parameters (1..) and returns.
        FuncType fromTail = DropSelfParam(from);
        FuncType toTail = DropSelfParam(to);
        return FuncAssignable(fromTail, toTail, infer, depth);
    }

    internal static FuncType DropSelfParam(FuncType fn) => new()
    {
        TypeParams = fn.TypeParams,
        Params = fn.Params.Count > 1 ? fn.Params.GetRange(1, fn.Params.Count - 1) : new List<TType>(),
        Optional = fn.Optional.Count > 1 ? fn.Optional.GetRange(1, fn.Optional.Count - 1) : new List<bool>(),
        VarArg = fn.VarArg,
        Returns = fn.Returns,
        RetVarArg = fn.RetVarArg,
        IsBare = fn.IsBare,
        LaxArity = fn.LaxArity,
        IsMethod = false,
        SelfNamed = false,
    };

    private bool FuncAssignable(FuncType from, FuncType to, Dictionary<TypeVar, TType?>? infer, int depth)
    {
        if (from.IsBare || to.IsBare) return true;
        if (from.TypeParams != null)
        {
            // a generic function used where a concrete one is expected: its type variables are inferrable
            var ninfer = infer == null ? new Dictionary<TypeVar, TType?>() : new Dictionary<TypeVar, TType?>(infer);
            foreach (TypeVar tv in from.TypeParams) if (!ninfer.ContainsKey(tv)) ninfer[tv] = null;
            var plain = new FuncType
            {
                Params = from.Params, Optional = from.Optional, VarArg = from.VarArg, Returns = from.Returns,
                RetVarArg = from.RetVarArg, IsMethod = from.IsMethod, SelfNamed = from.SelfNamed,
            };
            bool ok = FuncAssignable(plain, to, ninfer, depth + 1);
            if (ok && infer != null)
            {
                // bindings must not mention the generic function's own type variables
                var own = new Dictionary<TypeVar, TType>();
                foreach (TypeVar tv in from.TypeParams) own[tv] = ninfer[tv] ?? PrimType.Unknown;
                foreach (var kv in ninfer)
                    if (infer.ContainsKey(kv.Key)) infer[kv.Key] = kv.Value == null ? null : Subst(kv.Value, own);
            }
            return ok;
        }
        if (to.TypeParams != null)
        {
            // the expected type is generic: its type variables match anything
            infer = infer == null ? new Dictionary<TypeVar, TType?>() : new Dictionary<TypeVar, TType?>(infer);
            foreach (TypeVar tv in to.TypeParams) if (!infer.ContainsKey(tv)) infer[tv] = null;
        }
        int np = Math.Min(from.Params.Count, to.Params.Count);
        for (int i = 0; i < np; i++)
        {
            TType fp = from.Params[i], tp = to.Params[i];
            if (!Assignable(fp, tp, infer, depth + 1) && !Assignable(tp, fp, infer, depth + 1)) return false;
        }
        if (!to.LaxArity && !from.LaxArity && from.MinArgs > to.Params.Count && to.VarArg == null) return false;
        if (from.Returns.Count < to.Returns.Count && !from.RetVarArg && !to.RetVarArg) return false;
        int nr = Math.Min(from.Returns.Count, to.Returns.Count);
        for (int i = 0; i < nr; i++)
            if (!Assignable(from.Returns[i], to.Returns[i], infer, depth + 1)) return false;
        return true;
    }

    // ------------------------------------------------------------------ type resolution

    private static PrimType? BuiltinType(string name) => name switch
    {
        "any" => PrimType.Any,
        "nil" => PrimType.Nil,
        "boolean" => PrimType.Boolean,
        "integer" => PrimType.Integer,
        "number" => PrimType.Number,
        "string" => PrimType.String,
        "thread" => PrimType.Thread,
        _ => null,
    };

    private TType ResolveType(TypeNode node, Scope scope)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        switch (node)
        {
            case NameTypeNode n:
                return ResolveNameType(n, scope);
            case ArrayTypeNode a:
                return new ArrayType(ResolveType(a.Element, scope));
            case TupleTypeNode t:
                return new TupleType(t.Elements.Select(e => ResolveType(e, scope)).ToList());
            case MapTypeNode m:
                return new MapType(ResolveType(m.Key, scope), ResolveType(m.Value, scope));
            case UnionTypeNode u:
                {
                    var ms = u.Members.Select(e => ResolveType(e, scope)).ToList();
                    CheckUnionValid(u, ms);
                    if (ms.Any(IsNil) || ms.Any(IsAny) || ms.Any(IsUnknown)) return MakeUnion(ms);
                    // keep declared members as written (no literal widening needed)
                    var flat = new List<TType>();
                    foreach (TType m in ms)
                        foreach (TType x in m is UnionType mu ? mu.Members : new List<TType> { m })
                            if (!flat.Any(y => Same(y, x))) flat.Add(x);
                    return flat.Count == 1 ? flat[0] : new UnionType(flat);
                }
            case FunctionTypeNode f:
                return ResolveFunctionType(f, scope);
            case RecordTypeNode r:
                {
                    // anonymous inline record type (only reachable through unusual syntax)
                    RecordType rec = MakeRecordShell("record", r, scope);
                    EnsureFilled(rec);
                    return rec;
                }
            case EnumTypeNode e:
                return MakeEnum("enum", e);
            case RequireTypeNode rq:
                return ResolveRequireType(rq);
        }
        return PrimType.Unknown;
    }

    // tl can only discriminate union members by their runtime type() (or a `where` clause)
    private void CheckUnionValid(UnionTypeNode node, List<TType> members)
    {
        int nTable = 0, nTableIs = 0, nFunc = 0, nUd = 0, nUdIs = 0, nStr = 0;
        bool hasStr = false;
        string? err = null;
        var flat = new List<TType>();
        foreach (TType m in members) flat.AddRange(m is UnionType mu ? mu.Members : new List<TType> { m });
        foreach (TType m in flat)
        {
            if (m is RecordType || m is InstType)
            {
                RecordType rec = m is RecordType r ? r : ((InstType)m).Def;
                if (rec.Filling || rec.IsForward()) return;
                bool hasIs = WhereSourceOf(m, out _) != null || LookupField(m, "__is", true) != null;
                if (IsUserdataType(m))
                {
                    if (hasIs) { nUdIs++; if (nUd > 0) err ??= "cannot mix userdata types with and without __is metamethod"; }
                    else
                    {
                        nUd++;
                        if (nUd > 1) err ??= "cannot discriminate a union between multiple userdata types";
                        if (nUdIs > 0) err ??= "cannot mix userdata types with and without __is metamethod";
                    }
                }
                else if (hasIs) { nTableIs++; if (nTable > 0) err ??= "cannot mix table types with and without __is metamethod"; }
                else
                {
                    nTable++;
                    if (nTable > 1) err ??= "cannot discriminate a union between multiple table types";
                    if (nTableIs > 0) err ??= "cannot mix table types with and without __is metamethod";
                }
            }
            else if (m is ArrayType || m is MapType || m is TupleType)
            {
                nTable++;
                if (nTable > 1) err ??= "cannot discriminate a union between multiple table types";
                if (nTableIs > 0) err ??= "cannot mix table types with and without __is metamethod";
            }
            else if (m is FuncType || m is PolyType)
            {
                nFunc++;
                if (nFunc > 1) err ??= "cannot discriminate a union between multiple function types";
            }
            else if (m is EnumType || (IsPrim(m, Prim.String) && !hasStr))
            {
                nStr++;
                if (nStr > 1) err ??= "cannot discriminate a union between multiple string/enum types";
                if (IsPrim(m, Prim.String)) hasStr = true;
            }
        }
        if (err != null) Err(node, err + ": " + string.Join(" | ", flat.Select(Disp)));
    }

    private EnumType MakeEnum(string name, EnumTypeNode e)
    {
        var en = new EnumType { Name = name };
        foreach (string v in e.Values) if (en.Values.Add(v)) en.Order.Add(v);
        return en;
    }

    private TType ResolveRequireType(RequireTypeNode rq)
    {
        TType? t = RequireModule(rq.Module, rq);
        if (t == null) return PrimType.Unknown;
        foreach (string name in rq.Path)
        {
            TType? nt = NestedType(t, name);
            if (nt == null)
            {
                if (!IsUnknown(t)) Err(rq, $"unknown type {name} in module '{rq.Module}'");
                return PrimType.Unknown;
            }
            t = nt;
        }
        return t;
    }

    private TType? NestedType(TType t, string name)
    {
        if (!AsRecord(t, out var rec, out _)) return null;
        if (rec.Nested.TryGetValue(name, out var nt)) return nt;
        // a table holding a record type in a field (`local m = { Point = Point }`)
        if (rec.IsAnonymous && rec.Fields.TryGetValue(name, out var ft) && ft is RecordType { IsAnonymous: false }) return ft;
        return null;
    }

    private TType ResolveNameType(NameTypeNode n, Scope scope, bool keepGeneric = false)
    {
        string first = n.Names[0];
        TType? t = null;
        if (n.Names.Count == 1)
        {
            t = BuiltinType(first);
            if (t != null)
            {
                if (n.TypeArgs != null) Err(n, $"unexpected type argument");
                return t;
            }
        }
        VarInfo? v = Look(scope, first);
        MarkUsed(scope, first);
        if (v == null)
        {
            Err(n, $"unknown type {n.FullName}");
            return PrimType.Unknown;
        }
        if (v.Resolving) return PrimType.Unknown; // recursive alias
        if (first == "table" && n.Names.Count == 1 && v.IsGlobal && n.TypeArgs == null)
            return new MapType(PrimType.Any, PrimType.Any);
        if (v.AliasParams != null && v.TypeDef != null && n.Names.Count == 1)
        {
            // generic alias: substitute its type arguments
            var amap = new Dictionary<TypeVar, TType>();
            for (int i = 0; i < v.AliasParams.Count; i++)
                amap[v.AliasParams[i]] = n.TypeArgs != null && i < n.TypeArgs.Count ? ResolveType(n.TypeArgs[i], scope) : PrimType.Unknown;
            if (n.TypeArgs != null && n.TypeArgs.Count != v.AliasParams.Count) Err(n, "mismatch in number of type arguments");
            return Subst(v.TypeDef, amap);
        }
        if (v.TypeDef != null) t = v.TypeDef;
        else if (v.Type != null && (v.Type is RecordType || v.Type is InstType || IsUnknown(v.Type))) t = v.Type;
        else
        {
            Err(n, $"{first} is not a type");
            return PrimType.Unknown;
        }
        for (int i = 1; i < n.Names.Count; i++)
        {
            if (IsUnknown(t)) return PrimType.Unknown;
            TType? nt = NestedType(t, n.Names[i]);
            if (nt == null)
            {
                if (t is RecordType fr && fr.IsForward()) return PrimType.Unknown;
                Err(n, $"unknown type {n.FullName}");
                return PrimType.Unknown;
            }
            t = nt;
        }
        if (t is RecordType rec && rec.TypeParams != null)
        {
            if (keepGeneric && n.TypeArgs == null) return rec;
            var args = new List<TType>();
            if (n.TypeArgs != null)
            {
                if (n.TypeArgs.Count != rec.TypeParams.Count)
                    Err(n, $"mismatch in number of type arguments");
                foreach (TypeNode a in n.TypeArgs) args.Add(ResolveType(a, scope));
            }
            while (args.Count < rec.TypeParams.Count) args.Add(PrimType.Unknown);
            return new InstType(rec, args);
        }
        if (n.TypeArgs != null)
        {
            var args = n.TypeArgs.Select(a => ResolveType(a, scope)).ToList();
            if (t is FuncType ft && ft.TypeParams != null)
            {
                var map = new Dictionary<TypeVar, TType>();
                for (int i = 0; i < ft.TypeParams.Count; i++)
                    map[ft.TypeParams[i]] = i < args.Count ? args[i] : PrimType.Unknown;
                var r = (FuncType)Subst(ft, map);
                if (ReferenceEquals(r, ft)) return ft;
                r.TypeParams = null;
                return r;
            }
            if (!IsUnknown(t) && !(t is RecordType { } fr2 && fr2.IsForward()))
                Err(n, $"unexpected type argument");
        }
        return t;
    }

    private List<TypeVar>? DeclareTypeParams(List<TypeParam>? tps, Scope scope)
    {
        if (tps == null) return null;
        var list = new List<TypeVar>();
        foreach (TypeParam tp in tps)
        {
            var tv = new TypeVar(tp.Name);
            if (BuiltinType(tp.Name) != null) Err(tp, $"cannot use base type name '{tp.Name}' as a type variable");
            scope.Declare(new VarInfo { Name = tp.Name, TypeDef = tv });
            list.Add(tv);
        }
        for (int i = 0; i < tps.Count; i++)
            if (tps[i].Constraint != null) list[i].Constraint = ResolveType(tps[i].Constraint!, scope);
        return list;
    }

    private FuncType ResolveFunctionType(FunctionTypeNode f, Scope scope)
    {
        var ft = new FuncType();
        if (f.IsBare) { ft.IsBare = true; return ft; }
        Scope s = scope;
        if (f.TypeParams != null)
        {
            s = new Scope(scope);
            ft.TypeParams = DeclareTypeParams(f.TypeParams, s);
        }
        foreach (ParamTypeNode p in f.Params)
        {
            ft.Params.Add(ResolveType(p.Type, s));
            ft.Optional.Add(p.Optional);
        }
        if (f.VarArg != null) ft.VarArg = ResolveType(f.VarArg, s);
        foreach (TypeNode r in f.Returns) ft.Returns.Add(ResolveType(r, s));
        ft.RetVarArg = f.ReturnsVarArg;
        ft.SelfNamed = f.Params.Count > 0 && f.Params[0].Name == "self"; // confirmed (or cleared) by the enclosing record
        return ft;
    }

    // ------------------------------------------------------------------ record declarations

    // ------------------------------------------------------------------ classes: private and protected members

    private readonly List<RecordType> _classes = new();

    // The class whose declaration contains this place in the source (the innermost, if nested).
    private RecordType? ClassAt(Node at)
    {
        RecordType? best = null;
        foreach (RecordType c in _classes)
        {
            if (c.File != _file || c.ClassRange is not { } r) continue;
            bool inside = (at.Line > r.StartLine || (at.Line == r.StartLine && at.Col >= r.StartCol))
                       && (at.Line < r.EndLine || (at.Line == r.EndLine && at.Col <= r.EndCol));
            if (inside && (best == null || r.StartLine > best.ClassRange!.Value.StartLine
                           || (r.StartLine == best.ClassRange!.Value.StartLine && r.StartCol >= best.ClassRange!.Value.StartCol))) best = c;
        }
        return best;
    }

    // The record, among t and what it is declared to be, that declares the member.
    private RecordType? DeclaringRecord(TType t, string name, int depth = 0)
    {
        if (depth > 20 || !AsRecord(t, out var rec, out _)) return null;
        if (rec.Fields.ContainsKey(name)) return rec;
        foreach (TType it in rec.Interfaces)
            if (DeclaringRecord(it, name, depth + 1) is { } found) return found;
        return null;
    }

    private bool DerivesFrom(RecordType rec, RecordType ancestor, int depth = 0)
    {
        if (ReferenceEquals(rec, ancestor)) return true;
        if (depth > 20) return false;
        foreach (TType it in rec.Interfaces)
            if (AsRecord(it, out var parent, out _) && DerivesFrom(parent, ancestor, depth + 1)) return true;
        return false;
    }

    /// <summary>Reports the use of a private or protected member from outside where it may be used.</summary>
    private void CheckVisible(Node at, TType on, string name)
    {
        RecordType? owner = DeclaringRecord(on, name);
        if (owner?.Visibility == null || !owner.Visibility.TryGetValue(name, out int visibility)) return;
        RecordType? here = ClassAt(at);
        if (here != null && (ReferenceEquals(here, owner) || (visibility == 1 && DerivesFrom(here, owner)))) return;
        Err(at, visibility == 2
            ? $"'{name}' is private to class {owner.Name}"
            : $"'{name}' is protected: only class {owner.Name} and its subclasses can use it");
    }

    // The abstract functions a class is left with: its own, and those of its parent that
    // neither it nor a mixin defines. A class with any cannot be instantiated.
    private void ComputeUnimplemented(RecordTypeNode node, RecordType rec)
    {
        if (node.ClassDefines == null) return;
        var missing = new HashSet<string>(node.ClassAbstract ?? []);
        foreach (TType super in rec.Interfaces)
        {
            if (!AsRecord(super, out var s, out _) || s.Unimplemented == null) continue;
            foreach (string member in s.Unimplemented)
            {
                if (node.ClassDefines.Contains(member)) continue;
                bool provided = false;
                foreach (TType other in rec.Interfaces)
                    if (!ReferenceEquals(other, super) && AsRecord(other, out var o, out _) && !o.IsInterface
                        && o.Unimplemented?.Contains(member) != true && LookupField(other, member) != null) { provided = true; break; }
                if (!provided) missing.Add(member);
            }
        }
        rec.Unimplemented = missing.Count > 0 ? missing : null;
    }

    // A class must define the functions of the interfaces it implements (or get them from its
    // parent class or a mixin).
    private void CheckImplements(RecordTypeNode node, RecordType rec)
    {
        if (node.ClassDefines == null) return;
        foreach (TType it in rec.Interfaces)
        {
            if (!AsRecord(it, out var iface, out _) || !iface.IsInterface) continue;
            foreach (string member in AllFieldNames(it).Distinct())
            {
                if (LookupField(it, member) is not FuncType || node.ClassDefines.Contains(member) || node.ClassAbstract?.Contains(member) == true) continue;
                bool inherited = false;
                foreach (TType other in rec.Interfaces)
                    if (AsRecord(other, out var o, out _) && !o.IsInterface && LookupField(other, member) != null) { inherited = true; break; }
                if (!inherited) Err(node, $"class {rec.Name} does not define '{member}', which interface {iface.Name} requires");
            }
        }
    }

    private void CheckCameoImplements(RecordTypeNode node, RecordType rec)
    {
        if (!node.CheckCameoImplements) return;
        foreach (TType it in rec.Interfaces)
        {
            if (!AsRecord(it, out var iface, out _) || !iface.IsInterface) continue;
            foreach (string member in AllFieldNames(it).Distinct())
            {
                TType? req = LookupField(it, member);
                if (req == null || IsUnknown(req)) continue;
                TType? own = null;
                if (rec.Fields.TryGetValue(member, out var f)) own = f;
                else
                {
                    foreach (TType other in rec.Interfaces)
                        if (AsRecord(other, out var o, out _) && !o.IsInterface && LookupField(other, member) is { } inh)
                        { own = inh; break; }
                }
                if (own == null)
                {
                    Err(node, $"record {rec.Name} does not implement '{member}', which interface {Disp(it)} requires");
                    continue;
                }
                if (req is FuncType reqFn && own is FuncType ownFn && reqFn.SelfNamed != ownFn.SelfNamed)
                {
                    Err(node, $"record {rec.Name} member '{member}' {(ownFn.SelfNamed ? "is an instance method" : "is not an instance method")}, which does not match interface {Disp(it)}");
                }
            }
        }
    }

    private RecordType MakeRecordShell(string name, RecordTypeNode node, Scope parent)
    {
        var rec = new RecordType
        {
            Name = name, IsInterface = node.IsInterface, IsUserdata = node.IsUserdata, Decl = node,
            Filled = false, File = _file, WhereSource = node.WhereSource,
            CameoMetatableVar = node.CameoMetatableVar, CameoRecordId = node.CameoRecordId,
            IsCameoRecord = node.IsCameoRecord,
        };
        foreach (RecordField f in node.Fields)
        {
            if (f.Visibility != 0) (rec.Visibility ??= new())[f.Name] = f.Visibility;
            if (f.IsOptional) (rec.OptionalFields ??= new()).Add(f.Name);
        }
        if (node.CameoMethodStats is { Count: > 0 } mstats)
        {
            rec.CameoMethodNames = new HashSet<string>();
            foreach (FunctionStat ms in mstats)
                if (ms.Path.Count > 0) rec.CameoMethodNames.Add(ms.Path[^1]);
        }
        if (node.ClassRange != null)
        {
            rec.ClassRange = node.ClassRange;
            rec.ReadOnly = node.ClassReadOnly is { Count: > 0 } ? node.ClassReadOnly : null;
            rec.ClassDefines = node.ClassDefines;
            _classes.Add(rec);
        }
        var body = new Scope(parent);
        rec.BodyScope = body;
        rec.TypeParams = null;
        if (node.TypeParams != null)
        {
            rec.TypeParams = new List<TypeVar>();
            foreach (TypeParam tp in node.TypeParams)
            {
                var tv = new TypeVar(tp.Name);
                body.Declare(new VarInfo { Name = tp.Name, TypeDef = tv });
                rec.TypeParams.Add(tv);
            }
        }
        TType selfT = rec.TypeParams == null ? rec : new InstType(rec, rec.TypeParams.Cast<TType>().ToList());
        body.Declare(new VarInfo { Name = "self", TypeDef = selfT });
        foreach (NestedTypeDecl nd in node.NestedTypes)
        {
            TType? nt = null;
            if (nd.Def is RecordTypeNode nr)
            {
                nt = MakeRecordShell(nd.Name, nr, body);
                ((RecordType)nt).Name = name + "." + nd.Name; // nested types are shown qualified
            }
            else if (nd.Def is EnumTypeNode ne) nt = MakeEnum(nd.Name, ne);
            if (nt != null)
            {
                rec.Nested[nd.Name] = nt;
                body.Declare(new VarInfo { Name = nd.Name, TypeDef = nt, Type = nt is RecordType { IsInterface: false } ? nt : null });
            }
        }
        return rec;
    }

    private void EnsureFilled(RecordType rec)
    {
        if (rec.Filled || rec.Filling || rec.Decl == null) return;
        rec.Filling = true;
        string sFile = _file;
        _file = rec.File;
        try
        {
            RecordTypeNode node = rec.Decl;
            var body = (Scope)rec.BodyScope!;
            if (node.TypeParams != null)
                for (int i = 0; i < node.TypeParams.Count; i++)
                    if (node.TypeParams[i].Constraint != null)
                        rec.TypeParams![i].Constraint = ResolveType(node.TypeParams[i].Constraint!, body);
            foreach (NestedTypeDecl nd in node.NestedTypes)
            {
                if (nd.Def is RecordTypeNode || nd.Def is EnumTypeNode) continue;
                TType nt = ResolveType(nd.Def, body);
                rec.Nested[nd.Name] = nt;
                body.Declare(new VarInfo { Name = nd.Name, TypeDef = nt });
            }
            foreach (TypeNode it in node.Interfaces)
            {
                if (it is NameTypeNode { Names.Count: 1 } nn && nn.Names[0] == "userdata")
                {
                    rec.IsUserdata = true;
                    if (rec.Name == "userdata") continue;
                }
                TType t = ResolveType(it, body);
                if (ReferenceEquals(t, rec)) continue;
                if (AsRecordNoFill(t)) rec.Interfaces.Add(t);
                else if (!IsUnknown(t)) Err(it, $"{it switch { NameTypeNode x => x.FullName, _ => "type" }} is not an interface");
            }
            if (node.ArrayElement is TupleTypeNode tupleNode)
            {
                // tuple record (`is {A, B}`): positions are typed as the union of the element types
                var elems = tupleNode.Elements.Select(x => ResolveType(x, body)).ToList();
                rec.TupleElems = elems;
                rec.ArrayElem = Common(elems) ?? MakeUnion(elems);
            }
            else if (node.ArrayElement != null) rec.ArrayElem = ResolveType(node.ArrayElement, body);
            CheckInheritedArrayParts(node, rec);
            CheckImplements(node, rec);
            ComputeUnimplemented(node, rec);
            foreach (RecordField f in node.Fields)
            {
                TType ft = ResolveType(f.Type, body);
                if (f.Macro != null && ft is FuncType mf)
                {
                    mf.IsMacroExp = true; mf.Macro = f.Macro; mf.MacroFile = _file;
                    CheckMacroArgUses(f.Macro);
                }
                // a field is a method only if its `self` parameter is of the record's own type
                if (ft is FuncType sf && sf.SelfNamed)
                {
                    TType p0 = sf.Params[0];
                    bool own = ReferenceEquals(p0, rec)
                        || (p0 is InstType pi && ReferenceEquals(pi.Def, rec) && rec.TypeParams != null
                            && pi.Args.Count == rec.TypeParams.Count && pi.Args.Zip(rec.TypeParams).All(z => ReferenceEquals(z.First, z.Second)));
                    if (!own) sf.SelfNamed = false;
                }
                var dict = f.IsMetamethod ? rec.MetaFields : rec.Fields;
                if (dict.TryGetValue(f.Name, out var existing))
                {
                    if (ft is FuncType nf && existing is FuncType ef)
                    {
                        var p = new PolyType();
                        p.Overloads.Add(ef);
                        p.Overloads.Add(nf);
                        dict[f.Name] = p;
                    }
                    else if (ft is FuncType nf2 && existing is PolyType ep) ep.Overloads.Add(nf2);
                    else Err(f, $"attempt to redeclare field '{f.Name}' (only functions can be overloaded)");
                }
                else
                {
                    dict[f.Name] = ft;
                    if (!f.IsMetamethod) rec.FieldOrder.Add(f.Name);
                    // a field that an interface also declares has to be compatible with it — except
                    // constructors (`new`, and the `__init` behind it in a class): they belong to
                    // the type, not to its values, so a subtype may take different arguments
                    foreach (TType it in rec.Interfaces)
                    {
                        if (f.Name is "new" or "__init" && !f.IsMetamethod) break;
                        TType? inherited = LookupField(it, f.Name, f.IsMetamethod);
                        if (inherited == null || IsUnknown(inherited) || IsUnknown(ft)) continue;
                        if (ft is RecordType { Filled: false } || inherited is RecordType { Filled: false }) continue;
                        if (!Assignable(ft, inherited))
                        {
                            Err(f, $"{(f.IsMetamethod ? "metamethod" : "field")} '{f.Name}' does not match definition in interface {Disp(it)}");
                            break;
                        }
                    }
                }
            }
            CheckCameoImplements(node, rec);
            rec.Filled = true;
            foreach (TType nt in rec.Nested.Values) if (nt is RecordType nr) EnsureFilled(nr);
        }
        finally
        {
            rec.Filling = false;
            rec.Filled = true;
            _file = sFile;
        }
    }

    // A record may inherit array/tuple parts from several interfaces: they have to agree.
    private void CheckInheritedArrayParts(RecordTypeNode node, RecordType rec)
    {
        var tuples = new List<List<TType>>();
        var arrays = new List<TType>();
        if (rec.TupleElems != null) tuples.Add(rec.TupleElems);
        else if (rec.ArrayElem != null) arrays.Add(rec.ArrayElem);
        foreach (TType it in rec.Interfaces)
        {
            if (TupleElemsOf(it) is { } te) tuples.Add(te);
            else if (ArrayElemOf(it) is { } ae) arrays.Add(ae);
        }
        if (tuples.Count + arrays.Count < 2) return;
        for (int i = 0; i < tuples.Count; i++)
            for (int j = i + 1; j < tuples.Count; j++)
            {
                int n = Math.Min(tuples[i].Count, tuples[j].Count);
                for (int k = 0; k < n; k++)
                    if (!Same(tuples[i][k], tuples[j][k]))
                    {
                        Err(node, "incompatible tuple interfaces");
                        return;
                    }
            }
        foreach (TType a in arrays)
            foreach (List<TType> t in tuples)
            {
                string at = "{" + Disp(a) + "}", tt = "{" + string.Join(", ", t.Select(Disp)) + "}";
                if (!t.All(x => Assignable(x, a)))
                {
                    Err(node, $"inherits incompatible array {at} and tuple {tt}");
                    return;
                }
                Warn(node, $"inherits overlapping array {at} and tuple {tt}");
            }
    }

    private static bool AsRecordNoFill(TType t) => t is RecordType || t is InstType;

    // ------------------------------------------------------------------ modules

    private TType? RequireModule(string name, Node at)
    {
        CheckResult res = CheckModule(name);
        if (res.Errors.Count > 0 && _reportedModules.Add(name))
        {
            foreach (Diagnostic d in res.Errors)
            {
                if (_silent > 0) { _silentErrors++; continue; }
                // "module not found" is reported at the require site
                if (d.Message.StartsWith("module not found")) _errors.Add(new Diagnostic(_file, at.Line, at.Col, d.Message));
                else _errors.Add(d);
            }
        }
        else if (res.Errors.Count > 0 && res.Errors[0].Message.StartsWith("module not found") && !_errors.Any(e => e.Line == at.Line && e.Col == at.Col))
        {
            Err(at, res.Errors[0].Message);
        }
        if (res.Errors.Count > 0 && res.ModuleType == null) return PrimType.Unknown;
        return res.ModuleType ?? PrimType.Unknown;
    }
}

internal static class RecordTypeExt
{
    /// <summary>A forward-declared type (<c>global type X</c>) whose definition has not been seen.</summary>
    public static bool IsForward(this RecordType r) => r.Decl == null && r.Name.StartsWith("?");
}
