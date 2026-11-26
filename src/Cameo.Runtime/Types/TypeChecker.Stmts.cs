using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Types;

public sealed partial class TypeChecker
{
    // ------------------------------------------------------------------ blocks

    private void CheckBlock(Block b) => CheckBlockIn(b, new Scope(_scope));

    private void CheckBlockIn(Block b, Scope scope)
    {
        Scope saved = _scope;
        _scope = scope;
        try
        {
            List<RecordType> shells = Hoist(b, scope);
            CollectLabels(b, scope);
            CheckGotoScopes(b, false);
            foreach (Stat s in b.Stats) CheckStat(s);
            foreach (RecordType r in shells) EnsureFilled(r);
        }
        finally { _scope = saved; }
    }

    private void CollectLabels(Block b, Scope scope)
    {
        foreach (Stat st in b.Stats)
        {
            if (st is not LabelStat l) continue;
            scope.Labels ??= new HashSet<string>();
            if (!scope.Labels.Add(l.Name)) Err(l, $"label '{l.Name}' already defined");
            else if (_silent == 0) _labelDecls.Add((l, scope));
        }
    }

    // A goto may not jump forward past a local declaration into that local's scope
    // (unless the label is at the very end of the block).
    private void CheckGotoScopes(Block b, bool conditionSeesLocals)
    {
        for (int j = 0; j < b.Stats.Count; j++)
        {
            if (b.Stats[j] is not LabelStat label) continue;
            bool atEnd = !conditionSeesLocals;
            for (int k = j + 1; k < b.Stats.Count && atEnd; k++) if (b.Stats[k] is not LabelStat) atEnd = false;
            if (atEnd) continue;
            bool localSeen = false;
            for (int i = j - 1; i >= 0; i--)
            {
                Stat st = b.Stats[i];
                if (localSeen)
                    foreach (GotoStat g in AstWalk.Gotos(st, label.Name)) Err(g, "goto jumps into scope of a local variable");
                if (st is LocalStat or LocalFunctionStat) localSeen = true;
            }
        }
    }

    // macroexp arguments are substituted textually by tl, so each may be used only once
    private void CheckMacroArgUses(MacroExp m)
    {
        if (m.Body.Body.Stats.Count != 1 || m.Body.Body.Stats[0] is not ReturnStat r) return;
        var names = new HashSet<string>(m.Body.Params.Select(p => p.Name));
        var seen = new HashSet<string>();
        foreach (Expr v in r.Values)
            foreach (Expr x in AstWalk.All(v))
            {
                string? name = x is NameExpr n && names.Contains(n.Name) ? n.Name : x is VarargExpr ? "..." : null;
                if (name != null && !seen.Add(name)) Err(x, $"cannot use argument '{name}' multiple times in macroexp");
            }
    }

    /// <summary>Scope for a loop body: variables that the body assigns lose their flow-narrowing,
    /// since a later iteration may see the assigned value.</summary>
    private Scope LoopScope(Block body, Scope parent, IEnumerable<string>? loopVars = null)
    {
        var scope = new Scope(parent);
        var assigned = new HashSet<string>();
        AstWalk.AssignedNames(body, assigned);
        var narrowed = assigned.Where(n => parent.Lookup(n) is { Declared: not null, Type: not null } v && !ReferenceEquals(v.Declared, v.Type)).ToList();
        if (narrowed.Count == 0) return scope;
        // Trial run: does the body leave any of them with a type that no longer fits the type
        // it had on entry? Only then is the narrowing dropped for the loop.
        var widen = new List<string>();
        Scope saved = _scope;
        var trial = new Scope(parent);
        if (loopVars != null) foreach (string lv in loopVars) trial.Declare(new VarInfo { Name = lv, Type = PrimType.Unknown });
        try
        {
            Silently(() => CheckBlockIn(body, trial));
        }
        finally { _scope = saved; }
        foreach (string name in narrowed)
        {
            TType entry = parent.Lookup(name)!.Type!;
            TType? atEnd = trial.Lookup(name)?.Type;
            if (atEnd != null && !Assignable(atEnd, entry)) widen.Add(name);
        }
        foreach (string name in widen)
        {
            VarInfo v = parent.Lookup(name)!;
            scope.Declare(new VarInfo { Name = name, Type = v.Declared, Declared = v.Declared, Attrib = v.Attrib, IsGlobal = v.IsGlobal });
        }
        return scope;
    }

    // After an `if`, a narrowed variable assigned in some branch has the widest of the types it
    // ends up with in the branches (or its declared type when those do not fit together).
    private void MergeBranches(IfStat s, List<Scope> branchScopes, List<Block> branchBlocks, Scope fallthrough, Scope into)
    {
        var assigned = new HashSet<string>();
        foreach (Block b in branchBlocks) AstWalk.AssignedNames(b, assigned);
        foreach (string name in assigned)
        {
            VarInfo? cur = into.Lookup(name);
            if (cur?.Type == null || cur.IsGlobal && cur.Declared == null) continue;
            TType declared = cur.Declared ?? cur.Type;
            if (declared is not UnionType && !IsAny(declared)) continue;
            var ends = new List<TType>();
            for (int i = 0; i < branchScopes.Count; i++)
            {
                if (BlockReturns(branchBlocks[i])) continue;
                TType? t = branchScopes[i].Lookup(name)?.Type;
                if (t != null) ends.Add(t);
            }
            if (s.Else == null)
            {
                TType? t = fallthrough.Lookup(name)?.Type;
                if (t != null) ends.Add(t);
            }
            if (ends.Count == 0) continue;
            TType? join = ends.FirstOrDefault(c => ends.All(e => Assignable(e, c)));
            join ??= declared;
            if (!Same(join, cur.Type))
                into.Declare(new VarInfo { Name = name, Type = join, Declared = declared, Attrib = cur.Attrib, IsGlobal = cur.IsGlobal });
        }
    }

    private void CheckGoto(GotoStat g)
    {
        for (Scope? s = _scope; s != null; s = s.Parent)
        {
            if (s.Labels != null && s.Labels.Contains(g.Label))
            {
                (s.UsedLabels ??= new HashSet<string>()).Add(g.Label);
                return;
            }
            if (s.FuncBoundary) break;
        }
        Err(g, $"no visible label '{g.Label}' for goto");
    }

    /// <summary>Pre-declares the record/interface/enum types of a block so they can refer to each other.</summary>
    private List<RecordType> Hoist(Block b, Scope scope)
    {
        var shells = new List<RecordType>();
        foreach (Stat st in b.Stats)
        {
            if (st is not TypeDeclStat s) continue;
            string name = s.Name.Name;
            Scope target = s.IsGlobal ? _globals : scope;
            if (s.Def is RecordTypeNode rn)
            {
                if (s.IsGlobal && rn.IsInterface && BuiltinType(name) != null) continue; // `global interface any` in the prelude
                if (s.TypeParams != null && rn.TypeParams == null) rn.TypeParams = s.TypeParams;
                RecordType rec = MakeRecordShell(name, rn, scope);
                shells.Add(rec);
                var rv = new VarInfo { Name = name, TypeDef = rec, Type = rec.IsInterface ? null : rec, IsGlobal = s.IsGlobal };
                if (!s.IsGlobal) Track(scope, rv, s.Name, "type", s, false);
                target.Declare(rv);
            }
            else if (s.Def is EnumTypeNode en)
            {
                var ev = new VarInfo { Name = name, TypeDef = MakeEnum(name, en), IsGlobal = s.IsGlobal };
                if (!s.IsGlobal) Track(scope, ev, s.Name, "type", s, false);
                target.Declare(ev);
            }
            else if (s.Def != null)
            {
                var av0 = new VarInfo { Name = name, AliasNode = s.Def, AliasScope = scope, AliasFile = _file, IsGlobal = s.IsGlobal, AliasParamNodes = s.TypeParams };
                if (!s.IsGlobal) Track(scope, av0, s.Name, "type", s, false);
                target.Declare(av0);
            }
        }
        return shells;
    }

    // ------------------------------------------------------------------ statements

    private void CheckStat(Stat st)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        switch (st)
        {
            case LocalStat s: CheckLocal(s.Names, s.Types, s.Values, false, s); break;
            case GlobalStat s: CheckLocal(s.Names, s.Types, s.Values, true, s); break;
            case AssignStat s: CheckAssign(s); break;
            case CallStat s: CheckMulti(s.Call, null); break;
            case DoStat s: CheckBlock(s.Body); break;
            case WhileStat s:
                {
                    Scope saved = _scope;
                    _scope = LoopScope(s.Body, _scope);
                    try
                    {
                        CheckExpr(s.Cond);
                        Facts? f = FactsOf(s.Cond);
                        CheckBlockIn(s.Body, ApplyFacts(f?.True));
                    }
                    finally { _scope = saved; }
                    break;
                }
            case RepeatStat s:
                {
                    // the condition can see the body's locals
                    Scope saved = _scope;
                    var scope = LoopScope(s.Body, _scope);
                    _scope = scope;
                    try
                    {
                        var shells = Hoist(s.Body, scope);
                        CollectLabels(s.Body, scope);
                        CheckGotoScopes(s.Body, true);
                        foreach (Stat x in s.Body.Stats) CheckStat(x);
                        foreach (var r in shells) EnsureFilled(r);
                        CheckExpr(s.Cond);
                    }
                    finally { _scope = saved; }
                    break;
                }
            case IfStat s: CheckIf(s); break;
            case ForNumStat s: CheckForNum(s); break;
            case ForInStat s: CheckForIn(s); break;
            case FunctionStat s: CheckFunctionStat(s); break;
            case LocalFunctionStat s:
                {
                    Scope scope = _scope;
                    CheckFunction(s.Body, null, null, sig =>
                    {
                        var fv = new VarInfo { Name = s.Name.Name, Type = sig };
                        Track(scope, fv, s.Name, "function", s);
                        scope.Declare(fv);
                    }, null, true);
                    break;
                }
            case GlobalFunctionStat s:
                if (Look(_scope, s.Name) is { IsGlobal: false })
                    Err(s, "cannot define a global when a local with the same name is in scope");
                CheckFunction(s.Body, null, null, sig => _globals.Declare(new VarInfo { Name = s.Name, Type = sig, IsGlobal = true }), null, true);
                break;
            case ReturnStat s: CheckReturn(s); break;
            case TypeDeclStat s: CheckTypeDecl(s); break;
            case MatchStat s: CheckCameoMatch(s.Match, null); break;
            case CameoImportStat s: CheckCameoImport(s); break;
            case MacroExpStat s:
                {
                    Scope scope = _scope;
                    FuncType sig = CheckFunction(s.Macro.Body, null, null, null);
                    sig.IsMacroExp = true;
                    sig.Macro = s.Macro;
                    sig.MacroFile = _file;
                    CheckMacroArgUses(s.Macro);
                    scope.Declare(new VarInfo { Name = s.Name, Type = sig, IsMacro = true });
                    break;
                }
            case GotoStat g:
                CheckGoto(g);
                break;
            case BreakStat or ContinueStat or LabelStat:
                break;
        }
    }

    private void CheckTypeDecl(TypeDeclStat s)
    {
        string name = s.Name.Name;
        Scope target = s.IsGlobal ? _globals : _scope;
        if (s.Def is RecordTypeNode || s.Def is EnumTypeNode) return; // hoisted
        if (s.Def == null)
        {
            // forward declaration
            if (target.Lookup(name) == null)
                target.Declare(new VarInfo { Name = name, TypeDef = new RecordType { Name = "?" + name }, IsForward = true, IsGlobal = s.IsGlobal });
            return;
        }
        if (!target.Vars.TryGetValue(name, out var av) || !ReferenceEquals(av.AliasNode, s.Def))
        {
            av = new VarInfo { Name = name, AliasNode = s.Def, AliasScope = _scope, AliasFile = _file, IsGlobal = s.IsGlobal, AliasParamNodes = s.TypeParams };
            target.Declare(av);
        }
        Force(av);
        s.RuntimeValue = av.Type != null ? TypeDeclValue.AliasValue : TypeDeclValue.None;
    }

    private void CheckLocal(List<AttribName> names, List<TypeNode>? typeNodes, List<Expr> values, bool isGlobal, Node at)
    {
        if (at is not LocalStat { CameoTryOperand: true } && !isGlobal && typeNodes == null && names.Count == 1 && values.Count == 1 && values[0] is NameExpr tn
            && Look(_scope, tn.Name) is { TypeDef: not null, Type: null, IsForward: false } tv)
        {
            // `local X = SomeType` works as a type alias
            _scope.Declare(new VarInfo { Name = names[0].Name, TypeDef = tv.TypeDef });
            return;
        }
        List<TType>? types = typeNodes?.Select(t => ResolveType(t, _scope)).ToList();
        if (at is LocalStat { CameoTryPayloadOf: { } source })
            types = [Look(_scope, source)?.Type is InstType { Args.Count: 2 } result && result.Def == _resultDefinition
                ? result.Args[0] : PrimType.Unknown];
        for (int i = 0; i < values.Count && i < names.Count; i++)
            if (values[i] is TableExpr lit) _litCtx[lit] = $"in {(isGlobal ? "global" : "local")} declaration: {names[i].Name}";
        Multi vals = CheckExprList(values, i => types != null && i < types.Count ? types[i] : null);
        if (at is LocalStat { CameoTryOperand: true } capture)
        {
            if (vals.First is not InstType { Args.Count: 2 } tagged || tagged.Def != _resultDefinition)
                Err(values[0], "try operand must be a tagged Result<T, E>");
            else if (capture.CameoCatchCases is { } cases)
            {
                TType? kind = LookupField(tagged.Args[1], "kind");
                var covered = new HashSet<string>(StringComparer.Ordinal);
                foreach (StringExpr label in cases)
                {
                    if (kind is not EnumType || !Assignable(new StringLitType(label.Value), kind))
                        Err(label, $"unknown catch case '{label.Value}' in the result error kind");
                    else
                        covered.Add(label.Value);
                }
                if (kind is EnumType et && et.Values.Count > 0 && covered.Count == et.Values.Count && et.Values.All(covered.Contains))
                    capture.CameoTryExhaustive = true;
            }
        }
        var infos = new List<VarInfo>();
        if (types != null && types.Count > names.Count) Err(at, "number of types exceeds number of variables");
        for (int i = 0; i < names.Count; i++)
        {
            AttribName n = names[i];
            TType? declared = types != null && i < types.Count ? types[i] : null;
            TType? vt = vals.At(i);
            if (vt == null && declared == null && values.Count > 0 && !vals.VarTail)
                Err(n, $"assignment in declaration did not produce an initial value for variable '{n.Name}'");
            if (isGlobal)
            {
                if (Look(_scope, n.Name) is { IsGlobal: false })
                    Err(n, "cannot define a global when a local with the same name is in scope");
                else if (_globals.Vars.TryGetValue(n.Name, out var prev) && prev.Type != null)
                {
                    if (prev.Attrib != null && values.Count > 0) Err(n, $"cannot reassign to <{prev.Attrib}> global: {n.Name}");
                    else if (prev.Attrib != null && n.Attrib == null) Err(n, $"global was previously declared as <{prev.Attrib}>: {n.Name}");
                    else if (prev.Attrib == null && n.Attrib != null) Err(n, $"global was previously declared as not <{n.Attrib}>: {n.Name}");
                    else if (declared != null && !IsUnknown(prev.Type) && !(Assignable(declared, prev.Type) && Assignable(prev.Type, declared)))
                        Err(n, $"cannot redeclare global with a different type: previous type of {n.Name} is {Disp(prev.Type)}");
                }
            }
            Expr? ve = i < values.Count ? values[i] : null;
            TType type;
            if (declared != null)
            {
                if (vt != null) AssignCheck(ve ?? at, vt, declared, $"in {(isGlobal ? "global" : "local")} declaration: {n.Name}");
                type = declared;
            }
            else if (vt == null)
            {
                type = PrimType.Unknown;
                if (isGlobal && values.Count == 0)
                {
                    // `global x` re-declaration without type: keep an existing declaration
                    VarInfo? ex = _globals.Lookup(n.Name);
                    if (ex?.Type != null) type = ex.Type;
                }
            }
            else type = InferVarType(vt);
            if (n.Attrib == "total") CheckTotal(n, ve, type);
            if (n.Attrib == "close")
            {
                TType ct = Widen(declared ?? vt ?? PrimType.Unknown);
                bool closable = IsNil(ct) || IsUnknown(ct) || IsAny(ct) || ct is TypeVar
                    || (AsRecord(ct, out _, out _) && (LookupField(ct, "__close", true) != null || IsUserdataType(ct)));
                if (!closable) Err(n, $"to-be-closed variable {n.Name} has a non-closable type {Disp(ct)}");
            }
            var info = new VarInfo { Name = n.Name, Type = type, Attrib = n.Attrib, IsGlobal = isGlobal };
            if (!isGlobal) Track(_scope, info, n, "variable");
            if (declared != null && vt != null && !isGlobal && CanNarrowTo(declared, vt) && !ReferenceEquals(vt, declared))
            {
                // a union/any variable starts out narrowed to the type of its initial value
                info.Type = Widen(vt);
                info.Declared = declared;
            }
            infos.Add(info);
        }
        foreach (VarInfo v in infos)
        {
            if (isGlobal) _globals.Declare(v); else _scope.Declare(v);
        }
    }

    private static bool CanNarrowTo(TType declared, TType vt)
    {
        if (declared is not UnionType && !IsAny(declared)) return false;
        TType w = Widen(vt);
        return !IsNil(w) && !IsUnknown(w) && !IsAny(w) && w is not EmptyTableType && w is not UnionType && w is not TypeVar;
    }

    private static TType InferVarType(TType vt)
    {
        TType w = Widen(vt);
        if (IsNil(w)) return PrimType.Unknown;
        if (w is EmptyTableType) return new EmptyTableType();
        return w;
    }

    private void CheckTotal(AttribName n, Expr? value, TType type)
    {
        if (type is not MapType && !AsRecord(type, out _, out _) && !IsUnknown(type))
        {
            Err(n, "attribute <total> only applies to maps and records");
            return;
        }
        if (value == null)
        {
            Err(n, "variable declared <total> does not declare an initialization value");
            return;
        }
        if (value is not TableExpr te)
        {
            Err(n, "attribute <total> only applies to literal tables");
            return;
        }
        var present = new HashSet<string>();
        foreach (TableItem it in te.Items)
        {
            if (it.Key is StringExpr s) present.Add(s.Value);
            else if (it.Key is BoolExpr b) present.Add(b.Value ? "true" : "false");
        }
        List<string>? required = null;
        if (type is MapType m)
        {
            if (m.Key is EnumType e) required = e.Order;
            else if (IsPrim(m.Key, Prim.Boolean)) required = new List<string> { "true", "false" };
        }
        else if (AsRecord(type, out _, out _)) required = AllFieldNames(type).Where(f => LookupField(type, f) is not (FuncType or PolyType)).ToList();
        if (required == null)
        {
            Err(n, type is MapType
                ? "map variable declared <total> does not declare values for all possible keys"
                : "attribute <total> only applies to maps with enum or boolean keys, and records");
            return;
        }
        var missing = required.Where(r => !present.Contains(r)).ToList();
        if (missing.Count > 0)
            Err(n, type is MapType
                ? $"map variable declared <total> does not declare values for all possible keys (missing: {string.Join(", ", missing)})"
                : $"record variable declared <total> does not declare values for all fields (missing: {string.Join(", ", missing)})");
    }

    private void AssignCheck(Node at, TType from, TType to, string context)
    {
        if (!Assignable(from, to))
            Err(at, $"{context}: {Mismatch(from, to)}");
    }

    private void CheckAssign(AssignStat s)
    {
        var targetTypes = new List<TType?>();
        var targetVars = new List<VarInfo?>();
        var emptyTargets = new List<(VarInfo v, TType keyType)?>();
        foreach (Expr t in s.Targets)
        {
            VarInfo? v = null;
            TType? tt;
            (VarInfo, TType)? empty = null;
            if (t is NameExpr n)
            {
                v = Look(_scope, n.Name);
                if (v == null)
                {
                    Err(n, $"unknown variable: {n.Name}");
                    tt = null;
                }
                else if (v.Type == null || (v.TypeDef != null && v.DeclNode != null))
                {
                    Err(n, "cannot reassign a type");
                    tt = null;
                }
                else
                {
                    MarkAssigned(_scope, n.Name);
                    if (v.Attrib == "const" || v.Attrib == "total" || v.Attrib == "close")
                        Err(n, $"cannot assign to <{v.Attrib}> variable {n.Name}");
                    tt = v.Declared ?? v.Type;
                    n.TypeInfo = tt;
                }
            }
            else if (t is IndexExpr ix)
            {
                TType ot = CheckExpr(ix.Object);
                if (ot is EmptyTableType && ix.Object is NameExpr on && Look(_scope, on.Name) is { } ov && ov.Type is EmptyTableType)
                {
                    TType kt = CheckExpr(ix.Key);
                    empty = (ov, kt);
                    tt = null;
                }
                else tt = IndexType(ix, ot, ix.Key, ix.Object, true);
            }
            else
            {
                CheckExpr(t);
                tt = null;
            }
            targetTypes.Add(tt);
            targetVars.Add(v);
            emptyTargets.Add(empty);
        }
        foreach (Expr sv in s.Values) if (sv is TableExpr lit) _litCtx[lit] = "in assignment";
        Multi vals = CheckExprList(s.Values, i => i < targetTypes.Count ? targetTypes[i] : null);
        for (int i = 0; i < s.Targets.Count; i++)
        {
            TType? vt = vals.At(i);
            if (vt == null)
            {
                if (!vals.VarTail)
                {
                    if (s.Values.Count > 0 && s.Values.Count <= i && IsMultiExpr(s.Values[^1]) && s.Values[^1] is not VarargExpr)
                    {
                        Warn(s.Targets[i], $"only {vals.Types.Count - (s.Values.Count - 1)} values are returned by the function");
                        continue;
                    }
                    Err(s.Targets[i], "variable is not being assigned a value");
                    break;
                }
                continue;
            }
            Expr? ve = i < s.Values.Count ? s.Values[i] : null;
            if (emptyTargets[i] is { } et)
            {
                // first write to an empty table decides its type
                TType k = Widen(et.keyType), val = Widen(vt);
                if (IsNil(val) || val is EmptyTableType || IsUnknown(k)) et.v.Type = PrimType.Unknown;
                else if (IsPrim(k, Prim.Integer)) et.v.Type = new ArrayType(val);
                else if (s.Targets[i] is IndexExpr { IsDot: true }) et.v.Type = PrimType.Unknown;
                else et.v.Type = new MapType(k, val);
                continue;
            }
            TType? tt = targetTypes[i];
            if (tt == null) continue;
            VarInfo? v = targetVars[i];
            if (v != null && v.Type is EmptyTableType && IsTableLike(vt) && vt is not EmptyTableType)
            {
                v.Type = Widen(vt);
                continue;
            }
            if (v != null && IsUnknown(tt)) continue;
            if (tt is EmptyTableType && !IsTableLike(Widen(vt)) && !IsNil(vt) && !IsUnknown(vt) && !IsAny(vt))
            {
                Err(ve ?? s.Targets[i], $"in assignment: assigning {Disp(Widen(vt))} to a variable declared with {{}}");
                continue;
            }
            AssignCheck(ve ?? s.Targets[i], vt, tt, "in assignment");
            if (v != null)
            {
                // assignment re-narrows a union-typed variable to the assigned type (until the scope ends)
                TType declared = v.Declared ?? v.Type!;
                TType w = Widen(vt);
                bool narrow = CanNarrowTo(declared, vt) && !ReferenceEquals(vt, declared);
                if (narrow || v.Declared != null)
                    _scope.Declare(new VarInfo { Name = v.Name, Type = narrow ? w : declared, Declared = declared, Attrib = v.Attrib, IsGlobal = v.IsGlobal });
            }
        }
    }

    private void CheckIf(IfStat s)
    {
        // facts accumulated from failed earlier conditions
        Scope saved = _scope;
        Scope chain = _scope;
        try
        {
            var branchScopes = new List<Scope>();
            var branchBlocks = new List<Block>();
            foreach (var (cond, body) in s.Clauses)
            {
                _scope = chain;
                CheckExpr(cond);
                Facts? f = FactsOf(cond);
                Scope bs = ApplyFacts(f?.True, chain);
                CheckBlockIn(body, bs);
                branchScopes.Add(bs);
                branchBlocks.Add(body);
                chain = ApplyFacts(f?.False, chain);
            }
            if (s.Else != null)
            {
                var es = new Scope(chain);
                CheckBlockIn(s.Else, es);
                branchScopes.Add(es);
                branchBlocks.Add(s.Else);
            }
            _scope = saved;
            MergeBranches(s, branchScopes, branchBlocks, chain, saved);
        }
        finally { _scope = saved; }
        // when every branch returns, the code after the `if` runs only if all conditions were false
        if (s.Else == null && s.Clauses.All(c => BlockReturns(c.Body)))
        {
            var seen = new HashSet<string>();
            for (Scope? sc = chain; sc != null && sc != saved; sc = sc.Parent)
                foreach (var kv in sc.Vars)
                    if (seen.Add(kv.Key)) saved.Declare(kv.Value);
        }
    }

    private static bool BlockReturns(Block b)
    {
        if (b.Stats.Count == 0) return false;
        Stat last = b.Stats[^1];
        if (last is ReturnStat) return true;
        if (last is IfStat i) return i.Clauses.All(c => BlockReturns(c.Body)) && (i.Else == null || BlockReturns(i.Else));
        if (last is DoStat d) return BlockReturns(d.Body);
        return false;
    }

    /// <summary>A scope in which all flow-narrowed variables are back to their declared types
    /// (used for loop and function bodies, where narrowing from before may not hold).</summary>
    private Scope WidenedScope(Scope parent)
    {
        var scope = new Scope(parent);
        var seen = new HashSet<string>();
        for (Scope? sc = parent; sc != null && sc != _globals; sc = sc.Parent)
            foreach (var kv in sc.Vars)
                if (seen.Add(kv.Key) && kv.Value.Declared != null)
                    scope.Declare(new VarInfo { Name = kv.Key, Type = kv.Value.Declared, Declared = kv.Value.Declared, Attrib = kv.Value.Attrib, IsGlobal = kv.Value.IsGlobal });
        return scope;
    }

    private void CheckForNum(ForNumStat s)
    {
        TType a = CheckExpr(s.Start), b = CheckExpr(s.Limit);
        TType? c = s.Step != null ? CheckExpr(s.Step) : null;
        foreach (var (t, e) in new[] { (a, s.Start), (b, s.Limit) })
            if (!IsNumeric(Widen(t)) && !IsUnknown(t) && !(t is TypeVar))
                Err(e, $"expected a number, got {Disp(t)}");
        if (c != null && !IsNumeric(Widen(c)) && !IsUnknown(c)) Err(s.Step!, $"expected a number, got {Disp(c)}");
        bool isInt = IsPrim(a, Prim.Integer) && (c == null || IsPrim(c, Prim.Integer));
        TType vt = IsUnknown(a) ? PrimType.Unknown : isInt ? PrimType.Integer : PrimType.Number;
        var scope = LoopScope(s.Body, _scope, new[] { s.Var.Name });
        var loopVar = new VarInfo { Name = s.Var.Name, Type = vt };
        Track(scope, loopVar, s.Var, "variable");
        scope.Declare(loopVar);
        CheckBlockIn(s.Body, scope);
    }

    private void CheckCameoImport(CameoImportStat s)
    {
        CheckResult mod = CheckModule(s.Module);
        if (mod.Errors.Count > 0 && _reportedModules.Add(s.Module))
            Err(s, $"error loading module '{s.Module}': {mod.Errors[0].Message}");
        TType modType = mod.ModuleType ?? PrimType.Unknown;
        if (s.DefaultName != null)
        {
            var vi = new VarInfo
            {
                Name = s.DefaultName.Name,
                Type = modType,
                TypeDef = modType is RecordType mr ? mr : null,
                Attrib = "const",
            };
            Track(_scope, vi, s.DefaultName, "variable");
            _scope.Declare(vi);
            return;
        }
        if (s.Named == null) return;
        foreach (CameoImportItem item in s.Named)
        {
            TType? valType = null;
            TType? typeDef = null;
            if (AsRecord(modType, out var rec, out _))
            {
                EnsureFilled(rec);
                valType = LookupField(modType, item.ExportName);
                rec.Nested.TryGetValue(item.ExportName, out typeDef);
                if (valType == null && typeDef == null)
                    Err(item, $"module '{s.Module}' has no export '{item.ExportName}'");
            }
            else if (IsUnknown(modType) || IsAny(modType))
            {
                valType = PrimType.Unknown;
            }
            else
            {
                Err(item, $"module '{s.Module}' does not export named bindings (got {Disp(modType)})");
                valType = PrimType.Unknown;
            }
            item.HasRuntimeValue = valType != null || IsUnknown(modType) || IsAny(modType);
            var vi = new VarInfo
            {
                Name = item.LocalName.Name,
                Type = valType,
                TypeDef = typeDef,
                Attrib = "const",
            };
            Track(_scope, vi, item.LocalName, typeDef != null && valType == null ? "type" : "variable");
            _scope.Declare(vi);
        }
    }

    private readonly HashSet<(RecordType, string)> _implemented = new();

    private bool TryGetCameoIterableTypes(TType iterable, int varCount, out List<TType> varTypes)
    {
        TType w = Widen(iterable);
        if (w is InstType li && ReferenceEquals(li.Def, _listDefinition) && li.Args.Count == 1)
        {
            varTypes = varCount <= 1 ? [li.Args[0]] : [PrimType.Integer, li.Args[0]];
            return true;
        }
        if (w is InstType di && ReferenceEquals(di.Def, _dictDefinition) && di.Args.Count == 2)
        {
            varTypes = varCount <= 1 ? [di.Args[0]] : [di.Args[0], di.Args[1]];
            return true;
        }
        if (w is InstType qi && ReferenceEquals(qi.Def, _queryDefinition) && qi.Args.Count == 1)
        {
            varTypes = varCount <= 1 ? [qi.Args[0]] : [PrimType.Integer, qi.Args[0]];
            return true;
        }
        if (w is RecordType fa && ReferenceEquals(fa, _floatArrayDefinition))
        {
            varTypes = varCount <= 1 ? [PrimType.Number] : [PrimType.Integer, PrimType.Number];
            return true;
        }
        if (w is ArrayType arr)
        {
            varTypes = varCount <= 1 ? [arr.Elem] : [PrimType.Integer, arr.Elem];
            return true;
        }
        if (w is MapType mp)
        {
            varTypes = varCount <= 1 ? [mp.Key] : [mp.Key, mp.Value];
            return true;
        }
        if (w is TupleType tp)
        {
            TType elem = tp.Elems.Count == 0 ? PrimType.Any
                : tp.Elems.All(x => Same(Widen(x), Widen(tp.Elems[0]))) ? Widen(tp.Elems[0]) : PrimType.Any;
            varTypes = varCount <= 1 ? [elem] : [PrimType.Integer, elem];
            return true;
        }
        if (w is EmptyTableType)
        {
            varTypes = varCount <= 1 ? [PrimType.Unknown] : [PrimType.Unknown, PrimType.Unknown];
            return true;
        }
        varTypes = null!;
        return false;
    }

    private void CheckForIn(ForInStat s)
    {
        Multi m = CheckExprList(s.Exprs, _ => null);
        if (s.Exprs.Count == 1 && s.Exprs[0] is CallExpr { Callee: NameExpr { Name: "pairs" }, Args.Count: 1 } pc
            && Look(_scope, "pairs") is { IsGlobal: true } && pc.Args[0].TypeInfo is TType pat && Widen(pat) is ArrayType)
            Warn(s, "applying pairs on an array: did you intend to apply ipairs?");
        TType first = m.Types.Count > 0 ? m.Types[0] : PrimType.Unknown;
        Multi? rets = null;
        switch (first)
        {
            case FuncType f:
                s.CameoIterCount = -1;
                if (!f.IsBare) rets = new Multi { Types = f.Returns, VarTail = f.RetVarArg };
                break;
            case PolyType p:
                s.CameoIterCount = -1;
                rets = new Multi { Types = p.Overloads[0].Returns, VarTail = p.Overloads[0].RetVarArg };
                break;
            default:
                if (AsRecord(first, out _, out _) && LookupField(first, "__call", true) is FuncType cf)
                {
                    s.CameoIterCount = -1;
                    rets = new Multi { Types = cf.Returns, VarTail = cf.RetVarArg };
                }
                else if (_cameoMode && s.Exprs.Count == 1 && TryGetCameoIterableTypes(first, s.Names.Count, out var elemTypes))
                {
                    s.CameoIterCount = s.Names.Count == 1 ? 1 : 2;
                    rets = new Multi { Types = elemTypes };
                }
                else if (IsUnknown(first) || IsAny(first) || first is TypeVar or UnionType)
                {
                    s.CameoIterCount = _cameoMode && s.Exprs.Count == 1 ? 0 : -1;
                }
                else
                {
                    Err(s.Exprs[0], _cameoMode
                        ? $"cannot iterate: expected a collection or iterator function, got {Disp(first)}"
                        : $"cannot iterate: expected a function, got {Disp(first)}");
                }
                break;
        }
        if (m.Types.Count == 0 && !m.VarTail && s.Exprs.Count == 1 && IsMultiExpr(s.Exprs[0]))
            Err(s.Exprs[0], "expression in 'for' statement does not return any values");
        else if (rets != null && !rets.VarTail && rets.Types.Count > 0 && s.Names.Count > rets.Types.Count && !rets.Types.Any(IsUnknown))
            Err(s.Names[rets.Types.Count], $"too many variables for this iterator; it produces {rets.Types.Count} value{(rets.Types.Count == 1 ? "" : "s")}");
        var scope = LoopScope(s.Body, _scope, s.Names.Select(n => n.Name));
        for (int i = 0; i < s.Names.Count; i++)
        {
            TType t = rets?.At(i) ?? PrimType.Unknown;
            if (rets != null && rets.At(i) == null) t = PrimType.Unknown;
            var iv = new VarInfo { Name = s.Names[i].Name, Type = InferVarType(t) };
            Track(scope, iv, s.Names[i], "variable");
            scope.Declare(iv);
        }
        CheckBlockIn(s.Body, scope);
    }

    private void CheckFunctionStat(FunctionStat s)
    {
        MarkUsed(_scope, s.Root.Name);
        VarInfo? root = Look(_scope, s.Root.Name);
        if (s.Path.Count == 0)
        {
            if (root == null)
            {
                // tl requires the annotation; still declare the function so later uses do not cascade
                Err(s, "functions need an explicit 'local' or 'global' annotation");
                CheckFunction(s.Body, null, null, sig => _globals.Declare(new VarInfo { Name = s.Root.Name, Type = sig, IsGlobal = true }), null, true);
                return;
            }
            FuncType ft = CheckFunction(s.Body, root.Type as FuncType, null, null);
            if (root.Type != null && !IsUnknown(root.Type)) AssignCheck(s, ft, root.Type, $"in assignment: {s.Root.Name}");
            return;
        }
        if (root == null)
        {
            Err(s.Root, $"unknown variable: {s.Root.Name}");
            CheckFunction(s.Body, null, s.IsMethod ? PrimType.Unknown : null, null);
            return;
        }
        TType cur = root.Type ?? root.TypeDef ?? PrimType.Unknown;
        s.Root.TypeInfo = cur;
        for (int i = 0; i < s.Path.Count - 1; i++)
        {
            string name = s.Path[i];
            if (IsUnknown(cur) || IsAny(cur)) break;
            TType? nt = LookupField(cur, name) ?? NestedType(cur, name);
            if (nt == null)
            {
                if (cur is MapType mm) nt = mm.Value;
                else
                {
                    Err(s, $"invalid key '{name}' in type {Disp(cur)}");
                    nt = PrimType.Unknown;
                }
            }
            cur = nt;
        }
        string fname = s.Path[^1];
        Scope? extra = null;
        TType selfType = cur;
        RecordType? rec = null;
        if (AsRecord(cur, out var r0, out _))
        {
            rec = r0;
            if (rec.TypeParams != null)
            {
                // the record's type variables are visible in its functions
                extra = new Scope(_scope);
                foreach (TypeVar tv in rec.TypeParams)
                    if (Look(_scope, tv.Name)?.TypeDef == null) extra.Declare(new VarInfo { Name = tv.Name, TypeDef = tv });
                if (cur is RecordType) selfType = new InstType(rec, rec.TypeParams.Cast<TType>().ToList());
            }
            if (rec.IsInterface) Err(s, $"interfaces are abstract; consider using a concrete record");
        }
        if (rec != null && _silent == 0 && !_implemented.Add((rec, fname)))
            Warn(s, $"function shadows previous declaration of '{fname}'");
        TType? existing = rec != null ? LookupField(selfType, fname) : (cur is MapType m2 ? m2.Value : null);
        FuncType? expected = existing as FuncType;
        if (expected != null && s.IsMethod && expected.Params.Count > 0)
        {
            // declared as function(self, a, b): the body lists only a, b plus the implicit self
        }
        FuncType sig = CheckFunction(s.Body, expected, s.IsMethod ? selfType : null, f =>
        {
            if (rec != null && existing == null && !rec.IsForward())
            {
                rec.Fields[fname] = f;
                rec.FieldOrder.Add(fname);
            }
        }, extra);
        if (existing == null && rec == null && !IsUnknown(cur) && !IsAny(cur) && cur is not MapType && cur is not EmptyTableType)
            Err(s, $"cannot add function '{fname}' to a value of type {Disp(cur)}");
        else if (existing is PolyType ep)
        {
            if (!ep.Overloads.Any(o => Assignable(sig, o) && Assignable(o, sig)))
                Err(s, "type signature does not match declaration: field has multiple function definitions (such polymorphic declarations are intended for Lua module interoperability)");
        }
        else if (existing is FuncType ef && !ef.IsBare)
        {
            string where = $"type signature of '{fname}' does not match its declaration in {(rec != null ? rec.Name : Disp(cur))}";
            int selfAdj = s.IsMethod ? 1 : 0; // tl does not count the implicit self
            if ((sig.Params.Count > ef.Params.Count && ef.VarArg == null) || (sig.Params.Count < ef.MinArgs && sig.VarArg == null))
                Err(s, $"{where}: different number of input arguments: got {sig.Params.Count - selfAdj}, expected {ef.Params.Count - selfAdj}");
            else if (sig.Returns.Count > ef.Returns.Count && !ef.RetVarArg)
                Err(s, $"{where}: different number of return values: got {sig.Returns.Count}, expected {ef.Returns.Count}");
            else if (!Assignable(sig, ef))
            {
                // name the first argument that does not fit
                string? detail = null;
                var inf = ef.TypeParams?.ToDictionary(t => t, _ => (TType?)null);
                for (int pi = 0; pi < Math.Min(sig.Params.Count, ef.Params.Count) && detail == null; pi++)
                    if (!Assignable(sig.Params[pi], ef.Params[pi], inf) && !Assignable(ef.Params[pi], sig.Params[pi], inf))
                        detail = $"argument {pi + 1 - selfAdj}: {Mismatch(sig.Params[pi], ef.Params[pi])}";
                Err(s, detail != null ? $"{where}: {detail}" : $"{where}: got {Disp(sig)}, expected {Disp(ef)}");
            }
        }
    }

    private void CheckReturn(ReturnStat s)
    {
        FuncCtx ctx = _funcs.Peek();
        if (s.CameoErrorPropagation)
        {
            TType incoming = CheckExpr(s.Values[0]);
            if (s.CameoTrySource?.CameoTryExhaustive == true) return;
            if (incoming is InstType { Args.Count: 2 } source && source.Def == _resultDefinition)
            {
                if (ctx.IsModule || ctx.Sig.RetVarArg || ctx.Sig.Returns.Count != 1
                    || ctx.Sig.Returns[0] is not InstType { Args.Count: 2 } target || target.Def != _resultDefinition)
                    Err(s, "propagated error requires a function returning one Result<T, E>");
                else AssignCheck(s, source.Args[1], target.Args[1], "propagated error");
            }
            return;
        }
        if (ctx.IsModule)
        {
            Multi m = CheckExprList(s.Values, _ => null);
            if (ctx.ModuleType == null && m.Types.Count > 0) ctx.ModuleType = Widen(m.Types[0]);
            return;
        }
        if (ctx.Infer)
        {
            // Only Cameo expression functions opt into this branch. Inspect source arity
            // before the explicit ParenExpr lowering erases companion return values.
            Expr expression = s.Values[0];
            while (expression is ParenExpr paren) expression = paren.Inner;
            if (expression is CastExpr)
                Err(expression, "CAMEO004: outer result casts are not supported in expression functions; use a full function with an explicit result type and return expression as Type end");
            Multi values = CheckMulti(expression, ctx.ExpressionExpected);
            if (values.Types.Count != 1 || values.VarTail || IsUnknown(values.First) || IsAny(values.First))
                Err(expression, "CAMEO002: expression body must have one known, fixed single result; use a full function ... end with explicit result types to select or forward multiple values, or annotate the called function to establish its result types");
            // Keep literal precision so a context returning an enum can accept its case.
            // Generic inference may widen later; erasing it here loses valid callbacks.
            ctx.Inferred = new List<TType> { values.First };
            s.Values[0].TypeInfo = values.First;
            return;
        }
        FuncType sig = ctx.Sig;
        TType? ExpectedAt(int i)
        {
            if (i < sig.Returns.Count) return sig.Returns[i];
            if (sig.RetVarArg && sig.Returns.Count > 0) return sig.Returns[^1];
            return null;
        }
        Multi vals = CheckExprList(s.Values, ExpectedAt);
        {
            // the last value may be a call producing several (or zero) values
            bool lastMulti = s.Values.Count > 0 && IsMultiExpr(s.Values[^1]);
            int given = lastMulti ? (vals.VarTail || s.Values[^1] is VarargExpr ? 0 : vals.Types.Count) : s.Values.Count;
            if (given > sig.Returns.Count && !sig.RetVarArg)
                Err(s, $"in return value: excess return values, expected {sig.Returns.Count} ({string.Join(", ", sig.Returns.Select(Disp))}), got {given} ({string.Join(", ", vals.Types.Take(given).Select(Disp))})");
        }
        if (sig.Returns.Count > 1 && s.Values.Count == 1 && s.Values[0] is BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } andOr
            && (IsMultiExpr(andOr.Right) || IsMultiExpr(andOr.Left)))
            Warn(andOr, $"additional return values are being discarded due to '{(andOr.Op == BinaryOp.And ? "and" : "or")}' expression; suggest parentheses if intentional");
        for (int i = 0; i < vals.Types.Count; i++)
        {
            TType? et = ExpectedAt(i);
            if (et == null) break;
            Expr at = s.Values[Math.Min(i, s.Values.Count - 1)];
            if (!Assignable(vals.Types[i], et))
                Err(at, $"in return value{(sig.Returns.Count > 1 ? $" {i + 1}" : "")}: {Mismatch(vals.Types[i], et)}");
        }
    }

    // ------------------------------------------------------------------ functions

    private FuncType CheckFunction(FunctionBody fb, FuncType? expected, TType? selfType, Action<FuncType>? beforeBody, Scope? parent = null, bool declared = false)
    {
        if (expected != null && expected.IsBare) expected = null;
        var fs = WidenedScope(parent ?? _scope);
        fs.FuncBoundary = true;
        var sig = new FuncType();
        sig.TypeParams = DeclareTypeParams(fb.TypeParams, fs);
        int ei = 0; // index into the expected parameter list
        if (expected != null && selfType != null && fb.Params.Count > 0 && fb.Params[0].IsImplicitSelf) ei = 0;
        for (int i = 0; i < fb.Params.Count; i++)
        {
            Param p = fb.Params[i];
            TType pt;
            if (p.IsImplicitSelf)
            {
                pt = selfType ?? PrimType.Unknown;
                sig.IsMethod = true;
                sig.SelfNamed = true;
            }
            else if (p.Type != null) pt = ResolveType(p.Type, fs);
            else if (expected != null && ei < expected.Params.Count) pt = expected.Params[ei];
            else if (expected?.VarArg != null) pt = expected.VarArg;
            else if (i == 0 && p.Name == "self" && selfType != null) pt = selfType;
            else pt = PrimType.Unknown;
            if (i == 0 && p.Name == "self") sig.SelfNamed = true;
            ei++;
            if (fb.IsExpressionFunction && (IsUnknown(pt) || IsAny(pt)))
                Err(p, $"CAMEO001: expression parameter needs a concrete annotation or a known callback context; for '{p.Name}', replace {p.Name} with {p.Name}: <concrete type> (placeholder: supply the actual type), or pass the expression directly to a callback with known parameter types; use function ... end for unchecked parameters");
            sig.Params.Add(pt);
            sig.Optional.Add(p.Optional);
            p.NumericHint = Widen(pt) == PrimType.Integer ? Vm.ValueKind.Integer
                : Widen(pt) == PrimType.Number ? Vm.ValueKind.Float : null;
        }
        for (int i = 0; i < fb.Params.Count; i++)
        {
            var pv = new VarInfo { Name = fb.Params[i].Name, Type = sig.Params[i], LooseUnion = fb.Params[i].Type == null && sig.Params[i] is UnionType };
            if (!fb.Params[i].IsImplicitSelf) Track(fs, pv, fb.Params[i], "argument");
            fs.Declare(pv);
        }
        if (ArityOff(fb.Line)) sig.LaxArity = true;
        TType? varArg = null;
        if (fb.IsVarArg)
        {
            varArg = fb.VarArgType != null ? ResolveType(fb.VarArgType, fs) : (expected?.VarArg ?? PrimType.Any);
            sig.VarArg = varArg;
        }
        var ctx = new FuncCtx { Sig = sig, HasVarArg = fb.IsVarArg, VarArg = varArg, Infer = fb.IsExpressionFunction };
        if (fb.IsExpressionFunction && expected is { Returns.Count: 1, RetVarArg: false })
            ctx.ExpressionExpected = expected.Returns[0];
        if (fb.HasReturnAnnotation)
        {
            foreach (TypeNode r in fb.Returns) sig.Returns.Add(ResolveType(r, fs));
            sig.RetVarArg = fb.ReturnsVarArg;
        }
        // Ordinary Teal functions keep annotated returns. Only the explicit Cameo
        // expression-function marker enables inference; it cannot arise from Teal parsing.
        _ = declared;
        beforeBody?.Invoke(sig);
        _funcs.Push(ctx);
        try { CheckBlockIn(fb.Body, fs); }
        finally { _funcs.Pop(); }
        if (ctx.Infer)
        {
            sig.Returns = ctx.Inferred ?? new List<TType>();
            sig.RetVarArg = false;
        }
        return sig;
    }

    private static bool ContainsUnknown(TType t) => t switch
    {
        PrimType p => p.Kind == Prim.Unknown,
        ArrayType a => ContainsUnknown(a.Elem),
        MapType m => ContainsUnknown(m.Key) || ContainsUnknown(m.Value),
        TupleType tu => tu.Elems.Any(ContainsUnknown),
        UnionType u => u.Members.Any(ContainsUnknown),
        InstType i => i.Args.Any(ContainsUnknown),
        FuncType f => f.Params.Any(ContainsUnknown) || f.Returns.Any(ContainsUnknown),
        _ => false,
    };

    // ------------------------------------------------------------------ flow facts

    private sealed class Facts
    {
        public Dictionary<string, TType>? True, False;
    }

    private Facts? FactsOf(Expr e)
    {
        switch (e)
        {
            case ParenExpr p:
                return FactsOf(p.Inner);
            case IsExpr ie when ie.Inner is NameExpr n && _isTypes.TryGetValue(ie, out var t):
                {
                    VarInfo? v = Look(_scope, n.Name);
                    if (v?.Type == null) return null;
                    var f = new Facts { True = new() { [n.Name] = t } };
                    TType? rest = Subtract(v.Type, t);
                    if (rest != null) f.False = new() { [n.Name] = rest };
                    return f;
                }
            case BinaryExpr { Op: BinaryOp.Eq } eq:
                {
                    // `x == y` narrows a union-typed variable to the type of the other operand
                    Facts? f = null;
                    foreach (var (a, b) in new[] { (eq.Left, eq.Right), (eq.Right, eq.Left) })
                    {
                        if (a is not NameExpr n || b.TypeInfo is not TType bt) continue;
                        VarInfo? v = Look(_scope, n.Name);
                        TType w = Widen(bt);
                        if (v?.Type == null || w is UnionType || IsNil(w) || IsAny(w) || IsUnknown(w) || w is EmptyTableType || w is TypeVar) continue;
                        if (IsPrim(v.Type, Prim.String) && bt is StringLitType) w = bt; // may satisfy an enum
                        else if (v.Type is not (UnionType or TypeVar) && !IsAny(v.Type)) continue;
                        else if (v.Type is UnionType && !Assignable(w, v.Type)) continue;
                        f ??= new Facts { True = new() };
                        f.True![n.Name] = w;
                    }
                    return f;
                }
            case UnaryExpr { Op: UnaryOp.Not } u:
                {
                    Facts? f = FactsOf(u.Operand);
                    return f == null ? null : new Facts { True = f.False, False = f.True };
                }
            case BinaryExpr { Op: BinaryOp.And } b:
                {
                    Facts? l = FactsOf(b.Left), r = FactsOf(b.Right);
                    if (l == null && r == null) return null;
                    var af = new Facts { True = Merge(l?.True, r?.True) };
                    // not (A and B): when only one side carries facts, its negation is used (as tl does)
                    if (l == null) af.False = r!.False;
                    else if (r == null) af.False = l.False;
                    else if (l.False != null && r.False != null)
                    {
                        foreach (var kv in l.False)
                            if (r.False.TryGetValue(kv.Key, out var rt2))
                                (af.False ??= new())[kv.Key] = UnionKeepLits(kv.Value, rt2);
                    }
                    return af;
                }
            case BinaryExpr { Op: BinaryOp.Or } b:
                {
                    Facts? l = FactsOf(b.Left), r = FactsOf(b.Right);
                    if (l == null && r == null) return null;
                    var f = new Facts { False = Merge(l?.False, r?.False) };
                    if (l?.True != null && r?.True != null)
                    {
                        foreach (var kv in l.True)
                            if (r.True.TryGetValue(kv.Key, out var rt))
                                (f.True ??= new())[kv.Key] = UnionKeepLits(kv.Value, rt);
                    }
                    return f;
                }
        }
        return null;
    }

    private static TType UnionKeepLits(TType a, TType b)
    {
        var list = new List<TType>();
        foreach (TType t in new[] { a, b })
            foreach (TType m in t is UnionType u ? u.Members : new List<TType> { t })
                if (!list.Any(x => Same(x, m))) list.Add(m);
        if (list.All(x => x is StringLitType)) return list.Count == 1 ? list[0] : new UnionType(list);
        return MakeUnion(list);
    }

    private static Dictionary<string, TType>? Merge(Dictionary<string, TType>? a, Dictionary<string, TType>? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        var r = new Dictionary<string, TType>(a);
        foreach (var kv in b) r[kv.Key] = kv.Value;
        return r;
    }

    private TType? Subtract(TType from, TType what)
    {
        if (from is not UnionType u) return null;
        var rest = u.Members.Where(m => !Assignable(m, what)).ToList();
        if (rest.Count == 0 || rest.Count == u.Members.Count) return null;
        return rest.Count == 1 ? rest[0] : new UnionType(rest);
    }

    private Scope ApplyFacts(Dictionary<string, TType>? facts, Scope? parent = null)
    {
        var scope = new Scope(parent ?? _scope);
        if (facts == null) return scope;
        foreach (var kv in facts)
        {
            VarInfo? v = Look(parent ?? _scope, kv.Key);
            if (v?.Type == null) continue;
            scope.Declare(new VarInfo
            {
                Name = v.Name, Type = kv.Value, Declared = v.Declared ?? v.Type, Attrib = v.Attrib, IsGlobal = v.IsGlobal,
            });
        }
        return scope;
    }
}
