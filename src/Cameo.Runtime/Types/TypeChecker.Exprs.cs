using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Types;

public sealed partial class TypeChecker
{
    // ------------------------------------------------------------------ expression lists

    private static bool IsMultiExpr(Expr e) => e is CallExpr || e is MethodCallExpr || e is VarargExpr || e is CastExpr { IsTuple: true };

    private Multi CheckExprList(List<Expr> values, Func<int, TType?> expectedAt)
    {
        var m = new Multi();
        for (int i = 0; i < values.Count; i++)
        {
            if (i == values.Count - 1 && IsMultiExpr(values[i]))
            {
                Multi r = CheckMulti(values[i], expectedAt(i));
                m.Types.AddRange(r.Types);
                m.VarTail = r.VarTail;
            }
            else m.Types.Add(CheckExpr(values[i], expectedAt(i)));
        }
        return m;
    }

    private Multi CheckMulti(Expr e, TType? expected)
    {
        switch (e)
        {
            case CallExpr c:
                {
                    Multi m = CheckCall(c, c.Callee, c.Args, expected);
                    e.TypeInfo = m.First;
                    return m;
                }
            case MethodCallExpr mc:
                {
                    Multi m = CheckMethodCall(mc, expected);
                    e.TypeInfo = m.First;
                    return m;
                }
            case VarargExpr:
                {
                    FuncCtx ctx = _funcs.Peek();
                    if (!ctx.HasVarArg)
                    {
                        Err(e, "cannot use '...' outside a vararg function");
                        return new Multi(PrimType.Unknown) { VarTail = true };
                    }
                    e.TypeInfo = ctx.VarArg;
                    return new Multi(ctx.VarArg ?? PrimType.Any) { VarTail = true };
                }
            case CastExpr { IsTuple: true } ce:
                {
                    CheckMulti(ce.Inner, null);
                    var m = new Multi();
                    foreach (TypeNode t in ce.Types) m.Types.Add(ResolveType(t, _scope));
                    e.TypeInfo = m.First;
                    return m;
                }
            default:
                return new Multi(CheckExpr(e, expected));
        }
    }

    // ------------------------------------------------------------------ expressions

    private TType CheckExpr(Expr e, TType? expected = null)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); // deep nesting becomes an error, not a crash
        TType t = CheckExprInner(e, expected);
        e.TypeInfo = t;
        return t;
    }

    private TType CheckExprInner(Expr e, TType? expected)
    {
        switch (e)
        {
            case NilExpr: return PrimType.Nil;
            case BoolExpr: return PrimType.Boolean;
            case NumberExpr n: return n.IsInteger ? PrimType.Integer : PrimType.Number;
            case StringExpr s: return new StringLitType(s.Value);
            case VarargExpr: return CheckMulti(e, expected).First;
            case NameExpr n:
                {
                    VarInfo? v = Look(_scope, n.Name);
                    MarkUsed(_scope, n.Name);
                    if (v == null)
                    {
                        Err(n, $"unknown variable: {n.Name}");
                        return PrimType.Unknown;
                    }
                    if (v.Type == null)
                    {
                        if (v.IsForward) return PrimType.Unknown;
                        Err(n, v.TypeDef is RecordType { IsInterface: true } or InstType { Def.IsInterface: true }
                            ? "interfaces are abstract; consider using a concrete record"
                            : $"cannot use a type definition as a concrete value: {n.Name}");
                        return PrimType.Unknown;
                    }
                    return v.Type;
                }
            case IndexExpr ix:
                {
                    TType ot = CheckExpr(ix.Object);
                    if (ix.IsOptional)
                    {
                        if (IsNil(ot)) return PrimType.Nil;
                        TType nonNil = StripNil(ot);
                        return IndexType(ix, nonNil, ix.Key, ix.Object, false);
                    }
                    return IndexType(ix, ot, ix.Key, ix.Object, false);
                }
            case CallExpr or MethodCallExpr:
                {
                    Multi m = CheckMulti(e, expected);
                    return m.Types.Count > 0 ? m.Types[0] : PrimType.Nil;
                }
            case FunctionExpr f:
                return CheckFunction(f.Body, ExpectedFunc(expected), null, null);
            case TableExpr t:
                return CheckTable(t, expected);
            case CameoListExpr list:
                return CheckCameoList(list, expected);
            case CameoListCompExpr comp:
                return CheckCameoListComp(comp, expected);
            case CameoMatchExpr match:
                return CheckCameoMatch(match, expected);
            case ParenExpr p:
                return CheckExpr(p.Inner, expected);
            case CastExpr c:
                {
                    if (c.IsTuple) return CheckMulti(e, expected).First;
                    TType target = ResolveType(c.Types[0], _scope);
                    if (IsMultiExpr(c.Inner)) CheckMulti(c.Inner, null);
                    else CheckExpr(c.Inner, _cameoMode && c.Inner is TableExpr && AsRecord(target, out var cr, out _) && cr.CameoRecordId != null ? target : null);
                    return target;
                }
            case IsExpr ie:
                {
                    CheckExpr(ie.Inner);
                    TType target = ResolveType(ie.Type, _scope);
                    _isTypes[ie] = target;
                    ie.Test = BuildTest(target, ie, 0);
                    return PrimType.Boolean;
                }
            case UnaryExpr u: return CheckUnary(u);
            case BinaryExpr b: return CheckBinary(b, expected);
        }
        return PrimType.Unknown;
    }

    private TType? ExpectedListElement(TType? expected)
    {
        if (expected == null || _listDefinition == null) return null;
        if (expected is InstType ei && ReferenceEquals(ei.Def, _listDefinition) && ei.Args.Count == 1)
            return ei.Args[0];
        if (expected is UnionType u)
        {
            TType? found = null;
            foreach (TType m in u.Members)
            {
                if (m is InstType mi && ReferenceEquals(mi.Def, _listDefinition) && mi.Args.Count == 1)
                {
                    if (found != null) return null;
                    found = mi.Args[0];
                }
            }
            return found;
        }
        return null;
    }

    private TType CheckCameoList(CameoListExpr list, TType? expected)
    {
        TType? elemExpected = ExpectedListElement(expected);
        if (list.Items.Count == 0)
            return _listDefinition != null
                ? new InstType(_listDefinition, [elemExpected ?? PrimType.Unknown])
                : PrimType.Unknown;
        TType? inferred = null;
        foreach (Expr item in list.Items)
        {
            TType t = Widen(CheckExpr(item, elemExpected));
            if (elemExpected != null)
            {
                AssignCheck(item, t, elemExpected, "in list literal");
                continue;
            }
            if (inferred == null || IsNil(inferred) || IsUnknown(inferred))
                inferred = t;
            else if (IsNil(t) || IsUnknown(t))
                continue;
            else if (Same(inferred, t))
                continue;
            else if (IsNumeric(inferred) && IsNumeric(t))
                inferred = PrimType.Number;
            else if (Assignable(t, inferred))
                continue;
            else if (Assignable(inferred, t))
                inferred = t;
            else
                inferred = PrimType.Any;
        }
        TType elem = elemExpected ?? inferred ?? PrimType.Unknown;
        return _listDefinition != null ? new InstType(_listDefinition, [elem]) : PrimType.Unknown;
    }

    private TType CheckCameoListComp(CameoListCompExpr comp, TType? expected)
    {
        TType iterType = CheckExpr(comp.Iterable);
        List<TType>? varTypes = null;
        if (TryGetCameoIterableTypes(iterType, comp.Vars.Count, out var elemTypes))
        {
            comp.IterCount = comp.Vars.Count == 1 ? 1 : 2;
            varTypes = elemTypes;
        }
        else if (iterType is FuncType fn)
        {
            comp.IterCount = comp.Vars.Count == 1 ? 1 : 2;
            varTypes = fn.IsBare ? [] : fn.Returns;
        }
        else if (iterType is PolyType poly)
        {
            comp.IterCount = comp.Vars.Count == 1 ? 1 : 2;
            varTypes = poly.Overloads[0].Returns;
        }
        else if (IsUnknown(iterType) || IsAny(iterType) || iterType is TypeVar or UnionType)
        {
            comp.IterCount = comp.Vars.Count == 1 ? 1 : 2;
            varTypes = [];
        }
        else
        {
            comp.IterCount = comp.Vars.Count == 1 ? 1 : 2;
            Err(comp.Iterable, $"cannot iterate in list comprehension: expected a collection or iterator function, got {Disp(iterType)}");
            varTypes = [];
        }

        var scope = new Scope(_scope);
        for (int i = 0; i < comp.Vars.Count; i++)
        {
            TType vt = i < varTypes.Count ? varTypes[i] : PrimType.Unknown;
            var iv = new VarInfo { Name = comp.Vars[i].Name, Type = InferVarType(vt) };
            Track(scope, iv, comp.Vars[i], "variable");
            scope.Declare(iv);
        }

        Scope saved = _scope;
        _scope = scope;
        TType bodyType;
        TType? elemExpected = ExpectedListElement(expected);
        try
        {
            if (comp.Filter != null) CheckExpr(comp.Filter);
            bodyType = Widen(CheckExpr(comp.Body, elemExpected));
            if (elemExpected != null)
                AssignCheck(comp.Body, bodyType, elemExpected, "in list comprehension");
        }
        finally
        {
            _scope = saved;
        }
        TType elem = elemExpected ?? bodyType;
        return _listDefinition != null ? new InstType(_listDefinition, [elem]) : PrimType.Unknown;
    }

    private TType CheckCameoMatch(CameoMatchExpr match, TType? expected)
    {
        TType scrutType = CheckExpr(match.Scrutinee);
        TType wideScrut = Widen(scrutType);
        EnumType? enumScrut = wideScrut as EnumType;
        bool hasWildcard = false;
        var coveredStrings = new HashSet<string>(StringComparer.Ordinal);
        TType? resultType = null;

        foreach (MatchArm arm in match.Arms)
        {
            var armScope = new Scope(_scope);
            TType? singleNarrow = null;
            foreach (MatchPattern p in arm.Patterns)
            {
                if (p.IsWildcard)
                {
                    hasWildcard = true;
                }
                else if (p.TypeTest != null)
                {
                    TType target = ResolveType(p.TypeTest, _scope);
                    p.CompiledTest = BuildTest(target, p, 0);
                    if (arm.Patterns.Count == 1) singleNarrow = target;
                    if (p.Binding != null)
                    {
                        var bv = new VarInfo { Name = p.Binding.Name, Type = target, Attrib = "const" };
                        Track(armScope, bv, p.Binding, "variable");
                        armScope.Declare(bv);
                    }
                }
                else if (p.Literal != null)
                {
                    CheckExpr(p.Literal);
                    if (p.Literal is StringExpr se)
                    {
                        coveredStrings.Add(se.Value);
                        if (enumScrut != null && !enumScrut.Values.Contains(se.Value))
                            Err(p, $"'{se.Value}' is not a member of enum '{enumScrut.Name}'");
                    }
                }
            }
            if (singleNarrow != null && match.Scrutinee is NameExpr sn)
            {
                VarInfo? orig = Look(_scope, sn.Name);
                if (orig != null)
                    armScope.Declare(new VarInfo { Name = sn.Name, Type = singleNarrow, Attrib = orig.Attrib, Declared = orig.Declared ?? orig.Type });
            }

            Scope saved = _scope;
            _scope = armScope;
            try
            {
                if (arm.BlockBody != null) CheckBlockIn(arm.BlockBody, armScope);
                if (arm.ExprBody != null)
                {
                    TType armT = Widen(CheckExpr(arm.ExprBody, match.IsStatement ? null : expected));
                    if (!match.IsStatement)
                    {
                        if (expected != null)
                        {
                            AssignCheck(arm.ExprBody, armT, expected, "in match arm");
                        }
                        else if (resultType == null || IsNil(resultType) || IsUnknown(resultType))
                            resultType = armT;
                        else if (IsNil(armT) || IsUnknown(armT)) { }
                        else if (Same(resultType, armT)) { }
                        else if (IsNumeric(resultType) && IsNumeric(armT))
                            resultType = PrimType.Number;
                        else if (Assignable(armT, resultType)) { }
                        else if (Assignable(resultType, armT))
                            resultType = armT;
                        else
                            resultType = new UnionType([resultType, armT]);
                    }
                }
            }
            finally
            {
                _scope = saved;
            }
        }

        if (!hasWildcard && enumScrut != null)
        {
            var missing = enumScrut.Values.Where(v => !coveredStrings.Contains(v)).ToList();
            if (missing.Count > 0)
                Err(match, $"non-exhaustive match on enum '{enumScrut.Name}': missing {string.Join(", ", missing.Select(v => $"'{v}'"))}");
        }
        if (match.IsStatement) return PrimType.Nil;
        return expected ?? resultType ?? PrimType.Nil;
    }

    private static FuncType? ExpectedFunc(TType? expected)
    {
        if (expected is FuncType f) return f;
        if (expected is UnionType u)
        {
            var fs = u.Members.OfType<FuncType>().ToList();
            if (fs.Count == 1) return fs[0];
        }
        return null;
    }

    // ------------------------------------------------------------------ `is` tests

    private TypeTest? BuildTest(TType t, Node at, int depth)
    {
        if (depth > 10) return null;
        switch (t)
        {
            case PrimType p:
                return p.Kind switch
                {
                    Prim.Nil => new KindTest(TypeKindTest.Nil),
                    Prim.Boolean => new KindTest(TypeKindTest.Boolean),
                    Prim.Integer => new KindTest(TypeKindTest.Integer),
                    Prim.Number => new KindTest(TypeKindTest.Number),
                    Prim.String => new KindTest(TypeKindTest.String),
                    Prim.Thread => new KindTest(TypeKindTest.Thread),
                    _ => null,
                };
            case StringLitType or EnumType:
                return new KindTest(TypeKindTest.String);
            case FuncType or PolyType:
                return new KindTest(TypeKindTest.Function);
            case ArrayType or TupleType or MapType or EmptyTableType:
                return new KindTest(TypeKindTest.Table);
            case UnionType u:
                {
                    var any = new AnyOfTest();
                    foreach (TType m in u.Members)
                    {
                        TypeTest? mt = BuildTest(m, at, depth + 1);
                        if (mt == null) return null;
                        any.Options.Add(mt);
                    }
                    return any;
                }
            case RecordType or InstType:
                {
                    if (LookupField(t, "__is", true) is FuncType { Macro: not null } isMacro)
                    {
                        // explicit `metamethod __is ... = macroexp(self: T): boolean return ... end`
                        try
                        {
                            FunctionBody fb = Parser.ParseMacroBody(isMacro.Macro!.Source, isMacro.MacroFile ?? _file);
                            if (fb.Params.Count >= 1 && fb.Body.Stats.Count == 1 && fb.Body.Stats[0] is ReturnStat { Values.Count: 1 } ret)
                            {
                                var scope = new Scope(_scope);
                                scope.Declare(new VarInfo { Name = fb.Params[0].Name, Type = t });
                                Scope saved = _scope;
                                _scope = scope;
                                try { Silently(() => CheckExpr(ret.Values[0])); }
                                finally { _scope = saved; }
                                return new WhereTest { Condition = ret.Values[0], SelfName = fb.Params[0].Name };
                            }
                        }
                        catch (CameoSyntaxException)
                        {
                        }
                        return null;
                    }
                    string? where = WhereSourceOf(t, out RecordType? owner);
                    if (where != null && owner != null)
                    {
                        try
                        {
                            bool whereCameo = _isCameo
                                && !owner.File.EndsWith(".d.tl", StringComparison.Ordinal)
                                && !owner.File.EndsWith(".tl", StringComparison.Ordinal);
                            Expr cond = Parser.ParseExpression(where, owner.File, cameo: whereCameo);
                            // type-check the fresh copy with `self` bound to the tested type (annotates nested `is`)
                            var scope = new Scope((Scope?)owner.BodyScope ?? _scope);
                            scope.Declare(new VarInfo { Name = "self", Type = t });
                            Scope saved = _scope;
                            string sFile = _file;
                            _scope = scope;
                            _file = owner.File;
                            try { Silently(() => CheckExpr(cond)); }
                            finally { _scope = saved; _file = sFile; }
                            return new WhereTest { Condition = cond };
                        }
                        catch (CameoSyntaxException)
                        {
                            return null;
                        }
                    }
                    return new KindTest(IsUserdataType(t) ? TypeKindTest.Userdata : TypeKindTest.Table);
                }
        }
        return null;
    }

    // ------------------------------------------------------------------ indexing

    // tl's wording for an index that cannot work on the object's type
    private string IndexKeyMsg(string lit, Expr? objExpr, TType ot) =>
        objExpr is NameExpr v
            ? $"cannot index key '{lit}' in variable '{v.Name}' of type {Disp(ot)}"
            : $"cannot index key '{lit}' in type {Disp(ot)}";

    private bool IsLooseUnionVar(Expr? e) => e is NameExpr n && Look(_scope, n.Name) is { LooseUnion: true };

    private string InvalidKeyMsg(string lit, Expr? objExpr, TType ot)
    {
        bool iface = AsRecord(ot, out var r, out _) && r.IsInterface;
        if (objExpr is NameExpr on)
            return iface ? $"invalid key '{lit}' in '{on.Name}' of interface type {Disp(ot)}" : $"invalid key '{lit}' in record '{on.Name}' of type {Disp(ot)}";
        return iface ? $"invalid key '{lit}' in interface type {Disp(ot)}" : $"invalid key '{lit}' in record of type {Disp(ot)}";
    }

    private TType IndexType(Node at, TType ot, Expr key, Expr? objExpr, bool write)
    {
        string? lit = key is StringExpr se ? se.Value : null;
        if (lit != null) key.TypeInfo = new StringLitType(lit);
        switch (ot)
        {
            case PrimType p:
                switch (p.Kind)
                {
                    case Prim.Unknown:
                        if (lit == null) CheckExpr(key);
                        return PrimType.Unknown;
                    case Prim.Any:
                        if (lit == null) CheckExpr(key);
                        return PrimType.Any;
                    case Prim.String:
                        return IndexString(at, key, lit);
                    default:
                        if (lit == null) CheckExpr(key);
                        Err(at, lit != null ? IndexKeyMsg(lit, objExpr, ot) : $"cannot index a value of type {Disp(ot)}");
                        return PrimType.Unknown;
                }
            case StringLitType or EnumType:
                return IndexString(at, key, lit);
            case RecordType or InstType:
                {
                    AsRecord(ot, out var rec, out _);
                    if (lit != null)
                    {
                        TType? f = LookupField(ot, lit);
                        if (f != null)
                        {
                            if (rec.Visibility != null || rec.Interfaces.Count > 0) CheckVisible(at, ot, lit);
                            if (lit == "new" && rec.Unimplemented is { Count: > 0 } missing && objExpr is NameExpr or IndexExpr)
                                Err(at, $"class {rec.Name} is abstract and cannot be created: it does not define {string.Join(", ", missing.Order().Select(m => "'" + m + "'"))}");
                            if (write && DeclaringRecord(ot, lit) is { ReadOnly: { } ro } && ro.Contains(lit))
                                Err(at, $"property '{lit}' is read-only (it has a getter and no setter)");
                            return Generalize(ot, f);
                        }
                        TType? nt = NestedType(ot, lit);
                        if (nt != null)
                        {
                            if (nt is RecordType { IsInterface: false } || nt is InstType) return nt;
                            Err(at, $"cannot use a type definition as a concrete value: {lit}");
                            return PrimType.Unknown;
                        }
                        if (rec.IsForward() || rec.Filling) return PrimType.Unknown;
                        if (MetaIndex(ot) is { } mi) return mi;
                        Err(key, InvalidKeyMsg(lit, objExpr, ot));
                        return PrimType.Unknown;
                    }
                    if (key is NumberExpr { IsInteger: true } tki && TupleElemsOf(ot) is { } tes)
                    {
                        if (tki.IntValue >= 1 && tki.IntValue <= tes.Count) return tes[(int)tki.IntValue - 1];
                        Err(key, $"index {tki.IntValue} out of range for tuple record {Disp(ot)}");
                        return PrimType.Unknown;
                    }
                    TType kt = CheckExpr(key);
                    TType? el = ArrayElemOf(ot);
                    if (el != null && (IsNumeric(Widen(kt)) || IsUnknown(kt))) return el;
                    if (IsStringLike(kt))
                    {
                        if (kt is StringLitType ksl && LookupField(ot, ksl.Value) is { } litField) return Generalize(ot, litField);
                        if (MetaIndex(ot) is { } mis) return mis;
                        if (kt is EnumType en)
                        {
                            // every enum value has to name a field, and all those fields need the same type
                            var sel = en.Order.Select(v => LookupField(ot, v)).ToList();
                            int missing = sel.FindIndex(x => x == null);
                            if (missing >= 0)
                            {
                                Err(key, $"enum value '{en.Order[missing]}' is not a field in {Disp(ot)}");
                                return PrimType.Unknown;
                            }
                            if (sel.Count > 0 && sel.All(x => Same(x!, sel[0]!))) return sel[0]!;
                            if (sel.Count > 0)
                            {
                                int diff = sel.FindIndex(x => !Same(x!, sel[0]!));
                                Err(key, $"cannot index, not all enum values map to record fields of the same type (types of fields '{en.Order[0]}' and '{en.Order[diff]}' do not match)");
                            }
                            return PrimType.Unknown;
                        }
                        if (rec.IsForward() || rec.Filling || rec.IsAnonymous) return PrimType.Unknown;
                        Err(key, $"cannot index object of type {Disp(ot)} with a string, consider using an enum");
                        return PrimType.Unknown;
                    }
                    if (IsUnknown(kt) || IsAny(kt) || kt is TypeVar) return PrimType.Unknown;
                    if (MetaIndex(ot) is { } mi2) return mi2;
                    Err(key, $"cannot index object of type {Disp(ot)} with {Disp(kt)}");
                    return PrimType.Unknown;
                }
            case ArrayType a:
                {
                    if (lit != null)
                    {
                        Err(key, IndexKeyMsg(lit, objExpr, ot));
                        return PrimType.Unknown;
                    }
                    TType kt = CheckExpr(key, PrimType.Integer);
                    if (!IsNumeric(Widen(kt)) && !IsUnknown(kt) && !(kt is TypeVar))
                        Err(key, $"wrong index type: got {Disp(kt)}, expected integer");
                    return a.Elem;
                }
            case TupleType t:
                {
                    if (key is NumberExpr { IsInteger: true } ne)
                    {
                        if (ne.IntValue >= 1 && ne.IntValue <= t.Elems.Count) return t.Elems[(int)ne.IntValue - 1];
                        if (!write) Err(key, $"unexpected index {ne.IntValue} in tuple {Disp(ot)}");
                        return PrimType.Unknown;
                    }
                    TType kt = CheckExpr(key);
                    if (!IsNumeric(Widen(kt)) && !IsUnknown(kt))
                        Err(key, $"wrong index type: got {Disp(kt)}, expected integer");
                    return MakeUnion(t.Elems);
                }
            case MapType m:
                {
                    TType kt = lit != null ? new StringLitType(lit) : CheckExpr(key, m.Key);
                    if (!Assignable(kt, m.Key))
                        Err(key, $"wrong index type: got {Disp(kt)}, expected {Disp(m.Key)}");
                    return m.Value;
                }
            case EmptyTableType:
                if (lit == null) CheckExpr(key);
                return PrimType.Unknown;
            case UnionType u:
                {
                    if (lit == null) CheckExpr(key);
                    TType? common = null;
                    bool ok = true;
                    foreach (TType mbr in u.Members)
                    {
                        TType? r = null;
                        int errs = Silently(() => r = IndexType(at, mbr, key, objExpr, write));
                        if (errs > 0 || r == null) { ok = false; break; }
                        if (common == null) common = r;
                        else if (!Same(common, r)) { ok = false; break; }
                    }
                    if (ok && common != null) return common;
                    // a union cannot be indexed until it has been narrowed with `is`
                    if (lit != null && !IsLooseUnionVar(objExpr)) Err(at, IndexKeyMsg(lit, objExpr, ot));
                    return PrimType.Unknown;
                }
            case TypeVar tv:
                if (tv.Constraint != null) return IndexType(at, tv.Constraint, key, objExpr, write);
                if (lit == null) CheckExpr(key);
                return PrimType.Unknown;
            default:
                if (lit == null) CheckExpr(key);
                Err(at, lit != null ? IndexKeyMsg(lit, objExpr, ot) : $"cannot index a value of type {Disp(ot)}");
                return PrimType.Unknown;
        }
    }

    /// <summary>Result type of a record's <c>__index</c> metamethod, if it declares one.</summary>
    private TType? MetaIndex(TType ot)
    {
        TType? mm = LookupField(ot, "__index", true);
        if (mm == null) return null;
        if (mm is FuncType f && f.TypeParams == null && f.Returns.Count > 0) return f.Returns[0];
        return PrimType.Unknown;
    }

    private TType IndexString(Node at, Expr key, string? lit)
    {
        if (lit != null && _stringLib != null)
        {
            TType? f = LookupField(_stringLib, lit);
            if (f != null) return f;
            Err(key, $"invalid key '{lit}' in type string");
            return PrimType.Unknown;
        }
        if (lit == null) CheckExpr(key);
        return PrimType.Unknown;
    }

    // ------------------------------------------------------------------ table constructors

    // Table literals checked against a declared type report their errors in the context of
    // the declaration/assignment they belong to ("in local declaration: x: ...").
    private readonly Dictionary<TableExpr, string> _litCtx = new(ReferenceEqualityComparer.Instance);

    private void CheckCameoRequiredFields(TableExpr e, RecordType rec, TType exp, HashSet<string>? provided)
    {
        EnsureFilled(rec);
        if (rec.IsInterface)
        {
            foreach (string reqMember in AllFieldNames(exp).Distinct())
                if (provided == null || !provided.Contains(reqMember))
                    Err(e, $"table literal is missing '{reqMember}', which interface {Disp(exp)} requires");
            return;
        }
        if (!rec.IsCameoRecord) return;
        foreach (string fieldName in rec.FieldOrder)
        {
            if (provided != null && provided.Contains(fieldName)) continue;
            if (rec.OptionalFields != null && rec.OptionalFields.Contains(fieldName)) continue;
            if (rec.CameoMethodNames != null && rec.CameoMethodNames.Contains(fieldName)) continue;
            TType? ft = LookupField(exp, fieldName);
            if (ft == null || IsNil(ft) || IsAny(ft) || IsUnknown(ft)) continue;
            if (ft is UnionType u && u.Members.Any(IsNil)) continue;
            Err(e, $"missing required field '{fieldName}' in record '{rec.Name}'");
        }
    }

    private TType CheckTable(TableExpr e, TType? expected)
    {
        string ctx = _litCtx.TryGetValue(e, out string? lc) ? lc + ": " : "";
        TType? exp = expected;
        if (exp is UnionType u)
        {
            var tl = u.Members.Where(m => m is ArrayType || m is MapType || m is TupleType || m is RecordType || m is InstType).ToList();
            exp = tl.Count == 1 ? tl[0] : null;
        }
        if (exp is RecordType { } fr && fr.IsForward()) exp = null;
        if (exp is not (ArrayType or MapType or TupleType or RecordType or InstType)) exp = null;
        if (e.Items.Count == 0)
        {
            if (exp == null) return new EmptyTableType();
            if (_cameoMode && AsRecord(exp, out var emptyRec, out _))
            {
                if (_silent == 0 && emptyRec.CameoRecordId != null)
                {
                    e.CameoMetatableVar = emptyRec.CameoMetatableVar;
                    e.CameoRecordId = emptyRec.CameoRecordId;
                }
                CheckCameoRequiredFields(e, emptyRec, exp, null);
            }
            return expected!;
        }
        {
            // duplicate literal keys
            var seen = new Dictionary<string, TableItem>();
            int pos = 0;
            foreach (TableItem it in e.Items)
            {
                string? k = it.Kind == TableItemKind.Positional ? "n:" + (++pos)
                    : it.Key switch
                    {
                        StringExpr s => "s:" + s.Value,
                        NumberExpr { IsInteger: true } n => "n:" + n.IntValue,
                        NumberExpr { IsInteger: false } n => "n:" + (n.FloatValue == Math.Floor(n.FloatValue) && Math.Abs(n.FloatValue) < 1e15
                            ? ((long)n.FloatValue).ToString() : Cameo.Runtime.Vm.LuaNumber.FormatG(n.FloatValue, 14, false)),
                        BoolExpr b => "b:" + (b.Value ? "true" : "false"),
                        _ => null,
                    };
                if (k == null) continue;
                if (seen.TryGetValue(k, out TableItem? prev))
                    Err(it, $"redeclared key {k.Substring(2)} (previously declared at {_file}:{prev.Line}:{prev.Col})");
                else seen[k] = it;
            }
        }

        int lastIdx = e.Items.Count - 1;
        TType ValueOf(TableItem it, TType? want, bool isLast)
        {
            if (isLast && it.Kind == TableItemKind.Positional && IsMultiExpr(it.Value)) return CheckMulti(it.Value, want).First;
            return CheckExpr(it.Value, want);
        }

        switch (exp)
        {
            case RecordType or InstType:
                {
                    AsRecord(exp, out var rec, out _);
                    if (IsUserdataType(exp)) Err(e, $"{ctx}record is not a userdata");
                    if (_cameoMode && _silent == 0 && rec.CameoRecordId != null)
                    {
                        e.CameoMetatableVar = rec.CameoMetatableVar;
                        e.CameoRecordId = rec.CameoRecordId;
                    }
                    HashSet<string>? provided = _cameoMode && (rec.IsInterface || rec.IsCameoRecord) ? new HashSet<string>() : null;
                    TType? el = ArrayElemOf(exp);
                    List<TType>? tupleElems = TupleElemsOf(exp);
                    int apos = 0;
                    for (int i = 0; i < e.Items.Count; i++)
                    {
                        TableItem it = e.Items[i];
                        if (it.Key is StringExpr ks)
                        {
                            provided?.Add(ks.Value);
                            TType? ft = LookupField(exp, ks.Value);
                            if (it.DeclaredType != null) ft = ResolveType(it.DeclaredType, _scope);
                            if (ft == null)
                            {
                                CheckExpr(it.Value);
                                Err(it, ctx.Length > 0 ? $"{ctx}unknown field {ks.Value}" : $"in record field: unknown field {ks.Value}");
                                continue;
                            }
                            if (_cameoMode && rec.IsInterface && ft is FuncType { SelfNamed: true } mft)
                            {
                                FuncType withoutSelf = DropSelfParam(mft);
                                if (it.Value is FunctionExpr fe)
                                {
                                    bool takesSelf = fe.Body.Params.Count > 0 && fe.Body.Params[0].Name == "self";
                                    if (takesSelf)
                                    {
                                        TType vt = CheckExpr(it.Value, ft);
                                        AssignCheck(it.Value, vt, ft, $"in record field: {ks.Value}");
                                    }
                                    else
                                    {
                                        TType vt = CheckExpr(it.Value, withoutSelf);
                                        AssignCheck(it.Value, vt, withoutSelf, $"in record field: {ks.Value}");
                                        if (_silent == 0) fe.Body.PrependIgnoredSelf = true;
                                    }
                                }
                                else
                                {
                                    TType vt = CheckExpr(it.Value, withoutSelf);
                                    if (vt is FuncType { SelfNamed: false } && Assignable(vt, withoutSelf))
                                    {
                                        if (_silent == 0) it.WrapDropSelf = true;
                                    }
                                    else
                                    {
                                        AssignCheck(it.Value, vt, ft, $"in record field: {ks.Value}");
                                    }
                                }
                                continue;
                            }
                            TType vt2 = CheckExpr(it.Value, ft);
                            AssignCheck(it.Value, vt2, ft, $"in record field: {ks.Value}");
                        }
                        else if (it.Kind == TableItemKind.Positional)
                        {
                            apos++;
                            TType? want = tupleElems != null ? (apos <= tupleElems.Count ? tupleElems[apos - 1] : null) : el;
                            TType vt = ValueOf(it, want, i == lastIdx);
                            if (el == null) Err(it, $"unexpected array item in record of type {Disp(exp)}");
                            else if (tupleElems != null && want == null) Err(it, $"{ctx}unexpected index {apos} in tuple record {Disp(exp)}");
                            else AssignCheck(it.Value, vt, want!, tupleElems != null ? $"{ctx}in tuple: at index {apos}" : $"expected an array: at index {apos}");
                        }
                        else
                        {
                            TType kt = CheckExpr(it.Key!);
                            TType vt = CheckExpr(it.Value, IsNumeric(Widen(kt)) ? el : null);
                            if (el != null && IsNumeric(Widen(kt))) AssignCheck(it.Value, vt, el, "in array item");
                        }
                    }
                    if (provided != null)
                        CheckCameoRequiredFields(e, rec, exp, provided);
                    return expected!;
                }
            case ArrayType a:
                {
                    int arrPos = 0;
                    for (int i = 0; i < e.Items.Count; i++)
                    {
                        TableItem it = e.Items[i];
                        if (it.Kind == TableItemKind.Keyed) CheckExpr(it.Key!);
                        if (it.Kind == TableItemKind.Named)
                        {
                            CheckExpr(it.Value);
                            Err(it, $"unexpected field '{((StringExpr)it.Key!).Value}' in array of type {Disp(exp)}");
                            continue;
                        }
                        TType vt = ValueOf(it, a.Elem, i == lastIdx);
                        AssignCheck(it.Value, vt, a.Elem, $"expected an array: at index {(it.Kind == TableItemKind.Positional ? ++arrPos : i + 1)}");
                    }
                    return expected!;
                }
            case TupleType t:
                {
                    int pos = 0;
                    for (int i = 0; i < e.Items.Count; i++)
                    {
                        TableItem it = e.Items[i];
                        if (it.Kind != TableItemKind.Positional)
                        {
                            if (it.Kind == TableItemKind.Keyed)
                            {
                                CheckExpr(it.Key!);
                                if (it.Key is NumberExpr { IsInteger: true } tk)
                                {
                                    // explicit position
                                    TType? kw = tk.IntValue >= 1 && tk.IntValue <= t.Elems.Count ? t.Elems[(int)tk.IntValue - 1] : null;
                                    TType kv = CheckExpr(it.Value, kw);
                                    if (kw == null) Err(it, $"{ctx}unexpected index {tk.IntValue} in tuple {Disp(exp)}");
                                    else AssignCheck(it.Value, kv, kw, $"{ctx}in tuple: at index {tk.IntValue}");
                                    continue;
                                }
                                Err(it, $"{ctx}unknown index in tuple {Disp(exp)}");
                            }
                            CheckExpr(it.Value);
                            continue;
                        }
                        TType? want = pos < t.Elems.Count ? t.Elems[pos] : null;
                        TType vt = ValueOf(it, want, i == lastIdx);
                        if (want == null) Err(it, $"{ctx}unexpected index {pos + 1} in tuple {Disp(exp)}");
                        else AssignCheck(it.Value, vt, want, $"{ctx}in tuple: at index {pos + 1}");
                        pos++;
                    }
                    return expected!;
                }
            case MapType m:
                {
                    for (int i = 0; i < e.Items.Count; i++)
                    {
                        TableItem it = e.Items[i];
                        TType kt = it.Kind switch
                        {
                            TableItemKind.Named => new StringLitType(((StringExpr)it.Key!).Value),
                            TableItemKind.Keyed => CheckExpr(it.Key!, m.Key),
                            _ => PrimType.Integer,
                        };
                        if (!Assignable(kt, m.Key))
                            Err(it, $"in map key: got {Disp(kt)}, expected {Disp(m.Key)}");
                        TType vt = ValueOf(it, m.Value, i == lastIdx);
                        AssignCheck(it.Value, vt, m.Value, "in map value");
                    }
                    return expected!;
                }
        }

        // no usable expected type: infer
        var positional = new List<TType>();
        var named = new List<(string, TType)>();
        var keyTypes = new List<TType>();
        var keyedValues = new List<TType>();
        bool lastIsMulti = false;
        for (int i = 0; i < e.Items.Count; i++)
        {
            TableItem it = e.Items[i];
            if (it.Kind == TableItemKind.Positional)
            {
                if (i == lastIdx && IsMultiExpr(it.Value)) lastIsMulti = true;
                positional.Add(Widen(ValueOf(it, null, i == lastIdx)));
            }
            else if (it.Key is StringExpr ks)
            {
                TType? dt = it.DeclaredType != null ? ResolveType(it.DeclaredType, _scope) : null;
                TType vt = CheckExpr(it.Value, dt);
                if (dt != null) AssignCheck(it.Value, vt, dt, $"in record field: {ks.Value}");
                named.Add((ks.Value, dt ?? InferFieldType(vt)));
            }
            else
            {
                keyTypes.Add(Widen(CheckExpr(it.Key!)));
                keyedValues.Add(Widen(CheckExpr(it.Value)));
            }
        }
        if (keyTypes.Count > 0)
        {
            var ks = new List<TType>(keyTypes);
            var vs = new List<TType>(keyedValues);
            if (named.Count > 0) { ks.Add(PrimType.String); vs.AddRange(named.Select(n => n.Item2)); }
            if (positional.Count > 0) { ks.Add(PrimType.Integer); vs.AddRange(positional); }
            return new MapType(Common(ks) ?? MakeUnion(ks), Common(vs) ?? MakeUnion(vs));
        }
        if (named.Count > 0)
        {
            var rec = new RecordType { IsAnonymous = true, Name = "table" };
            foreach (var (n, t) in named)
            {
                if (!rec.Fields.ContainsKey(n)) rec.FieldOrder.Add(n);
                rec.Fields[n] = t;
            }
            if (positional.Count > 0) rec.ArrayElem = Common(positional) ?? MakeUnion(positional);
            return rec;
        }
        TType? c = Common(positional);
        if (c != null) return new ArrayType(c);
        if (lastIsMulti) return new ArrayType(MakeUnion(positional));
        return new TupleType(positional);
    }

    private static TType InferFieldType(TType vt)
    {
        TType w = Widen(vt);
        return IsNil(w) ? PrimType.Unknown : w;
    }

    /// <summary>A type from the list that all the others are assignable to, if any.</summary>
    private TType? Common(List<TType> types)
    {
        if (types.Count == 0) return null;
        foreach (TType cand in types)
        {
            if (IsNil(cand)) continue;
            if (types.All(t => Assignable(t, cand))) return cand is EmptyTableType ? PrimType.Unknown : cand;
        }
        if (types.All(IsNil)) return PrimType.Unknown;
        return null;
    }

    // ------------------------------------------------------------------ operators

    private string OpName(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+", BinaryOp.Sub => "-", BinaryOp.Mul => "*", BinaryOp.Div => "/",
        BinaryOp.IDiv => _isCameo ? "~/" : "//",
        BinaryOp.Mod => "%",
        BinaryOp.Pow => _isCameo ? "**" : "^",
        BinaryOp.Concat => "..", BinaryOp.Eq => "==",
        BinaryOp.Ne => _isCameo ? "!=" : "~=",
        BinaryOp.Lt => "<", BinaryOp.Le => "<=", BinaryOp.Gt => ">", BinaryOp.Ge => ">=",
        BinaryOp.And => _isCameo ? "&&" : "and",
        BinaryOp.Or => _isCameo ? "||" : "or",
        BinaryOp.BAnd => "&", BinaryOp.BOr => "|",
        BinaryOp.BXor => _isCameo ? "^" : "~",
        BinaryOp.Shl => "<<",
        BinaryOp.Coalesce => "??",
        _ => ">>",
    };

    private static string? MetaName(BinaryOp op) => op switch
    {
        BinaryOp.Add => "__add", BinaryOp.Sub => "__sub", BinaryOp.Mul => "__mul", BinaryOp.Div => "__div",
        BinaryOp.IDiv => "__idiv", BinaryOp.Mod => "__mod", BinaryOp.Pow => "__pow", BinaryOp.Concat => "__concat",
        BinaryOp.Eq or BinaryOp.Ne => "__eq", BinaryOp.Lt or BinaryOp.Gt => "__lt", BinaryOp.Le or BinaryOp.Ge => "__le",
        BinaryOp.BAnd => "__band", BinaryOp.BOr => "__bor", BinaryOp.BXor => "__bxor", BinaryOp.Shl => "__shl",
        BinaryOp.Shr => "__shr", _ => null,
    };

    private TType? MetaResult(TType l, TType r, string name, Node? at = null)
    {
        TType? mm = LookupField(l, name, true) ?? LookupField(r, name, true);
        if (mm == null) return null;
        if (at != null && mm is FuncType cf && cf.TypeParams == null && cf.Params.Count >= 2)
        {
            if (!Assignable(l, cf.Params[0])) Err(at, $"argument 1: {Mismatch(l, cf.Params[0])}");
            else if (!Assignable(r, cf.Params[1])) Err(at, $"argument 2: {Mismatch(r, cf.Params[1])}");
        }
        if (mm is PolyType p)
        {
            foreach (FuncType o in p.Overloads)
                if (o.Params.Count >= 2 && Assignable(l, o.Params[0]) && Assignable(r, o.Params[1]))
                    return o.Returns.Count > 0 ? o.Returns[0] : PrimType.Unknown;
            return PrimType.Unknown;
        }
        if (mm is FuncType f)
        {
            if (f.IsMacroExp && at is BinaryExpr be)
            {
                // operator implemented by a macroexp metamethod: expand it in place
                bool swap = be.Op is BinaryOp.Gt or BinaryOp.Ge;
                ExpandMacro(be, f, swap ? new List<Expr> { be.Right, be.Left } : new List<Expr> { be.Left, be.Right }, be.Op == BinaryOp.Ne);
            }
            else if (f.IsMacroExp && at is UnaryExpr ue) ExpandMacro(ue, f, new List<Expr> { ue.Operand }, false);
            if (f.TypeParams != null || f.Returns.Count == 0) return PrimType.Unknown;
            return f.Returns[0];
        }
        return PrimType.Unknown;
    }

    private static bool Loose(TType t) => IsUnknown(t) || t is TypeVar;

    private TType CheckBinary(BinaryExpr b, TType? expected)
    {
        BinaryOp op = b.Op;
        if (op == BinaryOp.And)
        {
            TType lt0 = CheckExpr(b.Left);
            Facts? f = FactsOf(b.Left);
            Scope saved = _scope;
            _scope = ApplyFacts(f?.True);
            TType rt0;
            try { rt0 = CheckExpr(b.Right, expected); }
            finally { _scope = saved; }
            _ = lt0;
            return expected != null ? rt0 : Widen(rt0);
        }
        if (op == BinaryOp.Coalesce)
        {
            TType lt0 = CheckExpr(b.Left, expected);
            TType nonNilLeft = StripNil(lt0);
            TType rt0 = CheckExpr(b.Right, expected ?? (IsNil(lt0) ? null : Widen(nonNilLeft)));
            if (IsNil(lt0)) return Widen(rt0);
            if (IsNil(rt0)) return Widen(lt0);
            TType cl = Widen(nonNilLeft), cr = Widen(rt0);
            if (Assignable(cr, cl)) return cl;
            if (Assignable(cl, cr)) return cr;
            return MakeUnion(new[] { cl, cr });
        }
        if (op == BinaryOp.Or)
        {
            TType lt0 = CheckExpr(b.Left, expected);
            Facts? f = FactsOf(b.Left);
            Scope saved = _scope;
            _scope = ApplyFacts(f?.False);
            TType rt0;
            try { rt0 = CheckExpr(b.Right, expected ?? (IsNil(lt0) || lt0 is EmptyTableType ? null : Widen(lt0))); }
            finally { _scope = saved; }
            if (IsNil(lt0) || lt0 is EmptyTableType) return Widen(rt0);
            if (IsNil(rt0)) return Widen(lt0);
            if (rt0 is EmptyTableType && b.Right is TableExpr)
            {
                TType wl0 = Widen(lt0);
                if (IsPrim(wl0, Prim.String) || IsNumeric(wl0) || IsPrim(wl0, Prim.Boolean) || wl0 is EnumType)
                {
                    Err(b, $"cannot use operator 'or' for types {Disp(lt0)} and {{}}");
                    return wl0;
                }
            }
            if (expected != null && !IsAny(expected) && Assignable(lt0, expected) && Assignable(rt0, expected)) return expected;
            if (Assignable(rt0, Widen(lt0))) return Widen(lt0);
            if (Assignable(lt0, Widen(rt0))) return Widen(rt0);
            return MakeUnion(new[] { lt0, rt0 });
        }

        TType lt = CheckExpr(b.Left);
        TType rt = CheckExpr(b.Right, op is BinaryOp.Eq or BinaryOp.Ne ? Widen(lt) is var wl && wl is EnumType ? wl : null : null);
        TType l = Widen(lt), r = Widen(rt);
        string? mn = MetaName(op);

        TType Bad()
        {
            Err(b, $"cannot use operator '{OpName(op)}' for types {Disp(lt)} and {Disp(rt)}");
            return PrimType.Unknown;
        }

        switch (op)
        {
            case BinaryOp.Eq:
            case BinaryOp.Ne:
                if (l is EnumType le && rt is StringLitType rs && !le.Values.Contains(rs.Value)) Err(b.Right, Mismatch(rt, l));
                else if (r is EnumType re && lt is StringLitType ls && !re.Values.Contains(ls.Value)) Err(b.Left, Mismatch(lt, r));
                else if (MetaResult(l, r, "__eq", b) != null) { }
                else if (!Assignable(lt, r) && !Assignable(rt, l) && !(l is TypeVar) && !(r is TypeVar))
                    Err(b, $"types are not comparable for equality: {Disp(lt)} and {Disp(rt)}");
                return PrimType.Boolean;
            case BinaryOp.Lt:
            case BinaryOp.Le:
            case BinaryOp.Gt:
            case BinaryOp.Ge:
                if (Loose(l) || Loose(r)) return PrimType.Boolean;
                if (IsNumeric(l) && IsNumeric(r)) return PrimType.Boolean;
                if (IsStringLike(l) && IsStringLike(r)) return PrimType.Boolean;
                if ((op is BinaryOp.Gt or BinaryOp.Ge ? MetaResult(r, l, mn!, b) : MetaResult(l, r, mn!, b)) != null) return PrimType.Boolean;
                Bad();
                return PrimType.Boolean;
            case BinaryOp.Concat:
                {
                    bool lo = IsStringLike(l) || IsNumeric(l) || Loose(l);
                    bool ro = IsStringLike(r) || IsNumeric(r) || Loose(r);
                    if (lo && ro) return PrimType.String;
                    TType? m = MetaResult(l, r, "__concat", b);
                    if (m != null) return m;
                    Bad();
                    return PrimType.String;
                }
            case BinaryOp.BAnd:
            case BinaryOp.BOr:
            case BinaryOp.BXor:
            case BinaryOp.Shl:
            case BinaryOp.Shr:
                {
                    if ((IsNumeric(l) || Loose(l)) && (IsNumeric(r) || Loose(r))) return PrimType.Integer;
                    TType? m = MetaResult(l, r, mn!, b);
                    if (m != null) return m;
                    Bad();
                    return PrimType.Integer;
                }
            default:
                {
                    // arithmetic
                    if (IsNumeric(l) && IsNumeric(r))
                    {
                        if (op == BinaryOp.Div || op == BinaryOp.Pow) return PrimType.Number;
                        return IsPrim(l, Prim.Integer) && IsPrim(r, Prim.Integer) ? PrimType.Integer : PrimType.Number;
                    }
                    if (l is TypeVar ltv && ReferenceEquals(l, r) && ltv.Constraint != null) return l;
                    if ((Loose(l) && (IsNumeric(r) || Loose(r))) || (Loose(r) && IsNumeric(l)))
                    {
                        if (op == BinaryOp.Div || op == BinaryOp.Pow) return PrimType.Number;
                        if (IsPrim(l, Prim.Number) || IsPrim(r, Prim.Number)) return PrimType.Number;
                        return PrimType.Unknown;
                    }
                    TType? m = MetaResult(l, r, mn!, b);
                    if (m != null) return m;
                    if (Loose(l) || Loose(r)) return PrimType.Unknown;
                    return Bad();
                }
        }
    }

    private TType CheckUnary(UnaryExpr u)
    {
        TType ot = CheckExpr(u.Operand);
        TType t = Widen(ot);
        switch (u.Op)
        {
            case UnaryOp.Not:
                return PrimType.Boolean;
            case UnaryOp.Neg:
                if (IsNumeric(t)) return t;
                if (Loose(t)) return t;
                {
                    TType? m = LookupField(t, "__unm", true);
                    if (m is FuncType f) return f.Returns.Count > 0 && f.TypeParams == null ? f.Returns[0] : PrimType.Unknown;
                }
                Err(u, $"cannot use operator '-' on type {Disp(ot)}");
                return PrimType.Unknown;
            case UnaryOp.BNot:
                if (IsNumeric(t) || Loose(t)) return PrimType.Integer;
                if (LookupField(t, "__bnot", true) is FuncType bf) return bf.Returns.Count > 0 && bf.TypeParams == null ? bf.Returns[0] : PrimType.Unknown;
                Err(u, $"cannot use operator '~' on type {Disp(ot)}");
                return PrimType.Integer;
            default: // Len
                if (t is MapType lm)
                {
                    TType lk = Widen(lm.Key);
                    if (IsNumeric(lk)) Warn(u, "using the '#' operator on a map with numeric key type may produce unexpected results");
                    else if (!IsUnknown(lk) && !IsAny(lk) && lk is not TypeVar && lk is not UnionType)
                        Err(u, "using the '#' operator on this map will always return 0");
                    return PrimType.Integer;
                }
                if (t is InstType pk && pk.Def.Name.EndsWith("PackTable", StringComparison.Ordinal) && LookupField(t, "__len", true) == null)
                    Warn(u, "using the '#' operator on a PackTable may produce unexpected results. Use '.n' instead");
                if (IsStringLike(t) || IsTableLike(t) || Loose(t) || IsAny(t))
                {
                    if (LookupField(t, "__len", true) is FuncType lf && lf.TypeParams == null && lf.Returns.Count > 0) return lf.Returns[0];
                    return PrimType.Integer;
                }
                Err(u, $"cannot use operator '#' on type {Disp(ot)}");
                return PrimType.Integer;
        }
    }

    // ------------------------------------------------------------------ calls

    private static Multi UnknownMulti() => new Multi(PrimType.Unknown) { VarTail = true };

    private void CheckArgsLoosely(List<Expr> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (i == args.Count - 1 && IsMultiExpr(args[i])) CheckMulti(args[i], null);
            else CheckExpr(args[i]);
        }
    }

    private Multi CheckCall(Node at, Expr callee, List<Expr> args, TType? expected)
    {
        if (_resultDefinition != null && callee is NameExpr constructor
            && Look(_scope, constructor.Name) is { } binding
            && (ReferenceEquals(binding, _okConstructor) || ReferenceEquals(binding, _errConstructor)))
        {
            CheckExpr(callee);
            bool success = ReferenceEquals(binding, _okConstructor);
            if (expected is not InstType { Args.Count: 2 } target || target.Def != _resultDefinition)
            {
                CheckArgsLoosely(args);
                Err(at, "result constructor requires an expected Result<T, E> type");
                return new Multi(PrimType.Unknown);
            }
            if (args.Count != 1)
            {
                CheckArgsLoosely(args);
                Err(at, "result constructor expects exactly one payload");
            }
            else
            {
                TType payload = target.Args[success ? 0 : 1];
                AssignCheck(args[0], CheckExpr(args[0], payload), payload, "result payload");
            }
            return new Multi(target);
        }
        if (callee is NameExpr n && Look(_scope, n.Name) is { IsGlobal: true })
        {
            switch (n.Name)
            {
                case "require" when args.Count == 1 && args[0] is StringExpr s:
                    {
                        CheckExpr(callee);
                        CheckExpr(args[0]);
                        return new Multi(RequireModule(s.Value, at) ?? PrimType.Unknown);
                    }
                case "pcall" when args.Count >= 1:
                case "xpcall" when args.Count >= 2:
                    {
                        CheckExpr(callee);
                        int skip = n.Name == "pcall" ? 1 : 2;
                        TType ft = CheckExpr(args[0]);
                        if (skip == 2) CheckExpr(args[1]);
                        var rest = args.Skip(skip).ToList();
                        var res = new Multi(PrimType.Boolean);
                        if (ft is FuncType f && !f.IsBare)
                        {
                            _argOffset = skip;
                            Multi r;
                            try { r = Apply(at, f, rest, null); }
                            finally { _argOffset = 0; }
                            res.Types.AddRange(r.Types);
                            res.VarTail = r.VarTail;
                            // on failure the second value is the error message
                            if (res.Types.Count == 1) { res.Types.Add(PrimType.Any); res.VarTail = false; }
                        }
                        else
                        {
                            CheckArgsLoosely(rest);
                            res.Types.Add(PrimType.Unknown);
                            res.VarTail = true;
                        }
                        return res;
                    }
            }
        }
        TType ct = CheckExpr(callee);
        if (at is CallExpr { IsOptional: true } optCall)
        {
            if (IsNil(ct)) { CheckArgsLoosely(args); return new Multi(PrimType.Nil); }
            ct = StripNil(ct);
        }
        if (at is CallExpr { CameoDotCall: true } cameoCall && callee is IndexExpr { IsDot: true } dot)
        {
            if (TryResolveCameoInstanceCall(dot, ct, out TType? selfInstance))
            {
                cameoCall.DotDispatch = CameoDotDispatch.Method;
                return ApplyCallee(at, ct, args, selfInstance, callee, expected);
            }
            if (IsKnownStaticCameoDotCall(dot, ct))
                cameoCall.DotDispatch = CameoDotDispatch.Function;
        }
        if (callee is IndexExpr { IsDot: true } mix && ct is FuncType { SelfNamed: true } mf && mf.Params.Count > 0 && !IsUnknown(mf.Params[0])
            && OwnsMethod(mix.Object, mf))
        {
            // a method called with '.': the receiver has to be passed explicitly
            TType? a0 = null;
            bool single = args.Count > 0 && !(args.Count == 1 && IsMultiExpr(args[0]));
            if (single) Silently(() => a0 = CheckExpr(args[0], mf.Params[0]));
            bool selfOk = args.Count > 0 && (!single || a0 == null || Assignable(a0, mf.Params[0], mf.TypeParams?.ToDictionary(t => t, _ => (TType?)null)));
            if (!selfOk)
            {
                Multi res = UnknownMulti();
                Silently(() => res = Apply(at, mf, args, null, expected));
                Err(at, "invoked method as a regular function: use ':' instead of '.'");
                return res;
            }
            // calling through the type itself (`Foo.add(x)`) is the explicit form and is fine
            bool viaType = mix.Object is NameExpr tn && Look(_scope, tn.Name) is { TypeDef: not null } tvi
                && (tvi.IsGlobal || (tvi.DeclNode != null && _tracked.TryGetValue(tvi.DeclNode, out Tracked? tk) && tk.Kind == "type"));
            if (!viaType) Warn(at, "invoked method as a regular function: consider using ':' instead of '.'");
        }
        return ApplyCallee(at, ct, args, null, callee, expected);
    }

    private static TType StripNil(TType t)
    {
        if (t is not UnionType u) return t;
        var rest = u.Members.Where(m => !IsNil(m)).ToList();
        if (rest.Count == 0) return PrimType.Nil;
        if (rest.Count == 1) return rest[0];
        return new UnionType(rest);
    }

    private bool IsViaTypeName(Expr obj) =>
        obj is NameExpr tn && Look(_scope, tn.Name) is { TypeDef: not null } tvi
        && (tvi.Type == null || tvi.IsGlobal || (tvi.DeclNode != null && _tracked.TryGetValue(tvi.DeclNode, out Tracked? tk) && tk.Kind == "type"));

    private bool MatchesBuiltInSelf(FuncType method, TType receiver)
    {
        if (method.Params.Count == 0) return false;
        TType first = Widen(method.Params[0]);
        if (IsUnknown(first) || IsAny(first)) return false;
        if (IsStringLike(receiver))
            return Assignable(PrimType.String, first);
        if (receiver is InstType instance && instance.Def.IsUserdata)
            return first is InstType selfInst && ReferenceEquals(selfInst.Def, instance.Def);
        if (receiver is RecordType rec && rec.IsUserdata)
            return ReferenceEquals(first, rec);
        return false;
    }

    private bool MatchesCameoRecordOrInterfaceSelf(FuncType method, TType receiver)
    {
        if (!method.SelfNamed || method.Params.Count == 0) return false;
        TType first = Widen(method.Params[0]);
        if (IsUnknown(first) || IsAny(first)) return false;
        var infer = method.TypeParams?.ToDictionary(t => t, _ => (TType?)null);
        return Assignable(receiver, first, infer) || Assignable(first, receiver, infer);
    }

    private bool TryResolveCameoInstanceCall(IndexExpr dot, TType ct, out TType? selfInstance)
    {
        selfInstance = null;
        if (IsViaTypeName(dot.Object) || dot.Object.TypeInfo is not TType ot) return false;
        TType baseOt = dot.IsOptional ? StripNil(ot) : ot;
        TType w = Widen(baseOt);
        bool isBuiltIn = IsStringLike(w) || (w is InstType inst && inst.Def.IsUserdata) || (w is RecordType rec && rec.IsUserdata);
        bool isCameoInterfaceOrMethodRec = _cameoMode && !isBuiltIn && AsRecord(w, out var urec, out _)
            && (urec.IsInterface || urec.CameoRecordId != null);
        if (!isBuiltIn && !isCameoInterfaceOrMethodRec)
            return false;
        bool matches = isBuiltIn
            ? ct switch
            {
                FuncType f => MatchesBuiltInSelf(f, w),
                PolyType ov => ov.Overloads.Count > 0 && ov.Overloads.All(f => MatchesBuiltInSelf(f, w)),
                _ => false,
            }
            : ct switch
            {
                FuncType f => MatchesCameoRecordOrInterfaceSelf(f, w),
                PolyType ov => ov.Overloads.Count > 0 && ov.Overloads.All(f => MatchesCameoRecordOrInterfaceSelf(f, w)),
                _ => false,
            };
        if (!matches) return false;
        selfInstance = baseOt;
        return true;
    }

    private bool IsKnownStaticCameoDotCall(IndexExpr dot, TType ct)
    {
        if (ct is not (FuncType or PolyType)) return false;
        if (IsViaTypeName(dot.Object)) return true;
        if (dot.Object.TypeInfo is not TType ot) return false;
        TType w = Widen(ot);
        if (Loose(w) || IsAny(w) || w is UnionType or TypeVar or EmptyTableType) return false;
        // Any concrete non-string, non-userdata value (modules, tables, records, classes)
        // cannot bind a built-in receiver at runtime.
        return !IsStringLike(w) && !IsUserdataType(w) && !(w is InstType inst && inst.Def.IsUserdata);
    }

    // Is the object a value (or the type) of the record the method belongs to?
    private bool OwnsMethod(Expr obj, FuncType method)
    {
        TType? ot = null;
        Silently(() => ot = CheckExpr(obj));
        if (ot == null) return false;
        TType w = Widen(ot);
        if (!AsRecord(w, out _, out _)) return false;
        var infer = method.TypeParams?.ToDictionary(t => t, _ => (TType?)null);
        return Assignable(w, method.Params[0], infer) || Assignable(method.Params[0], w, infer);
    }

    private Multi CheckMethodCall(MethodCallExpr mc, TType? expected)
    {
        TType ot = CheckExpr(mc.Object);
        TType w = Widen(ot);
        TType? mt;
        if (Loose(w) && (w is not TypeVar tv0 || tv0.Constraint == null) || IsAny(w) || w is UnionType || w is EmptyTableType)
        {
            if (w is UnionType un)
            {
                // all members must agree to give a precise type
                TType? common = null;
                bool ok = true;
                foreach (TType m in un.Members)
                {
                    TType? ft = IsStringLike(m) ? (_stringLib != null ? LookupField(_stringLib, mc.Name) : null) : LookupField(m, mc.Name);
                    if (ft == null || (common != null && !Same(common, ft))) { ok = false; break; }
                    common = ft;
                }
                if (ok && common != null) return ApplyCallee(mc, common, mc.Args, ot, null);
                if (!IsLooseUnionVar(mc.Object)) Err(mc, IndexKeyMsg(mc.Name, mc.Object, ot));
            }
            CheckArgsLoosely(mc.Args);
            return UnknownMulti();
        }
        if (w is TypeVar tv && tv.Constraint != null) w = tv.Constraint;
        if (IsStringLike(w)) mt = _stringLib != null ? LookupField(_stringLib, mc.Name) : PrimType.Unknown;
        else if (AsRecord(w, out var rec, out _))
        {
            mt = LookupField(w, mc.Name);
            if (mt != null) { CheckVisible(mc, w, mc.Name); mt = Generalize(w, mt); }
            else mt = MetaIndex(w);
            if (mt == null && (rec.IsForward() || rec.Filling)) mt = PrimType.Unknown;
        }
        else if (w is MapType mp && Assignable(PrimType.String, mp.Key)) mt = mp.Value;
        else mt = null;
        if (mt == null)
        {
            CheckArgsLoosely(mc.Args);
            if (IsStringLike(w) || AsRecord(w, out _, out _))
                Err(mc, IsStringLike(w) ? $"invalid key '{mc.Name}' in type string" : InvalidKeyMsg(mc.Name, mc.Object, ot));
            else Err(mc, IndexKeyMsg(mc.Name, mc.Object, ot));
            return UnknownMulti();
        }
        return ApplyCallee(mc, mt, mc.Args, ot, null);
    }

    // Marks `at` as a use of a macroexp: the compiler will expand a fresh copy of the macro's
    // return expression there, with the parameters bound to `args`.
    private void ExpandMacro(Node at, FuncType f, List<Expr> args, bool negate)
    {
        if (at is not Expr target || f.Macro == null)
        {
            Err(at, "macroexp cannot be expanded here");
            return;
        }
        try
        {
            FunctionBody fb = Parser.ParseMacroBody(f.Macro.Source, f.MacroFile ?? _file);
            if (fb.Body.Stats.Count != 1 || fb.Body.Stats[0] is not ReturnStat { Values.Count: 1 } ret)
            {
                Err(at, "macroexp body must be a single return statement with one expression");
                return;
            }
            var mx = new MacroExpansion { Body = ret.Values[0], Negate = negate };
            mx.Args.AddRange(args);
            var scope = new Scope(_scope);
            for (int i = 0; i < fb.Params.Count; i++)
            {
                mx.ParamNames.Add(fb.Params[i].Name);
                scope.Declare(new VarInfo { Name = fb.Params[i].Name, Type = i < f.Params.Count ? f.Params[i] : PrimType.Unknown });
            }
            // type-check the fresh copy so that nested `is` tests and macro uses get annotated
            Scope saved = _scope;
            string sFile = _file;
            _scope = scope;
            _file = f.MacroFile ?? _file;
            try { Silently(() => CheckExpr(mx.Body)); }
            finally { _scope = saved; _file = sFile; }
            target.Macro = mx;
        }
        catch (CameoSyntaxException)
        {
            Err(at, "invalid macroexp");
        }
    }

    private Multi ApplyCallee(Node at, TType ct, List<Expr> args, TType? self, Expr? calleeExpr, TType? expected = null)
    {
        switch (ct)
        {
            case FuncType f:
                if (f.IsBare)
                {
                    CheckArgsLoosely(args);
                    return UnknownMulti();
                }
                if (f.IsMacroExp)
                {
                    var margs = new List<Expr>();
                    if (at is MethodCallExpr mcall) margs.Add(mcall.Object);
                    margs.AddRange(args);
                    Multi mres = Apply(at, f, args, self, expected);
                    ExpandMacro(at, f, margs, false);
                    return mres;
                }
                return AdjustStringSpecial(f, args, self != null, Apply(at, f, args, self, expected));
            case PolyType p:
                {
                    // tuple arguments prefer overloads declared for a tuple of that size (table.unpack)
                    var arities = args.Select(a => a is NameExpr an && Look(_scope, an.Name)?.Type is TupleType tt ? tt.Elems.Count : 0).Where(x => x > 0).ToList();
                    IEnumerable<FuncType> order = p.Overloads;
                    if (arities.Count > 0)
                        order = p.Overloads.OrderByDescending(o => o.Params.Any(pp => pp is TupleType pt2 && arities.Contains(pt2.Elems.Count)) ? 1 : 0);
                    foreach (FuncType o in order)
                    {
                        Multi? r = null;
                        if (Silently(() => r = Apply(at, o, args, self, expected)) == 0) return r!;
                    }
                    // report against the overload with the fewest problems
                    FuncType best = p.Overloads[0];
                    int bestErrs = int.MaxValue;
                    foreach (FuncType o in p.Overloads)
                    {
                        int n = Silently(() => Apply(at, o, args, self, expected));
                        if (n < bestErrs) { bestErrs = n; best = o; }
                    }
                    return Apply(at, best, args, self, expected);
                }
            case PrimType { Kind: Prim.Unknown or Prim.Any }:
            case UnionType:
            case TypeVar:
                CheckArgsLoosely(args);
                return UnknownMulti();
            default:
                if (AsRecord(ct, out var rec, out _))
                {
                    TType? mm = LookupField(ct, "__call", true);
                    if (mm is FuncType mf) return Apply(at, (FuncType)Generalize(ct, mf), args, ct, expected);
                    if (mm != null || rec.IsForward())
                    {
                        CheckArgsLoosely(args);
                        return UnknownMulti();
                    }
                }
                CheckArgsLoosely(args);
                Err(at, $"not a function: {Disp(ct)}");
                return UnknownMulti();
        }
    }

    /// <summary>Fields reached through a generic record's type declaration (not an instance) have
    /// the record's type variables free: make them inferrable at the call.</summary>
    private static TType Generalize(TType container, TType field)
    {
        if (container is not RecordType { TypeParams: not null } rec) return field;
        if (field is FuncType f && !f.IsBare)
        {
            var tps = new List<TypeVar>(f.TypeParams ?? new List<TypeVar>());
            foreach (TypeVar tv in rec.TypeParams) if (!tps.Contains(tv)) tps.Add(tv);
            return new FuncType
            {
                TypeParams = tps, Params = f.Params, Optional = f.Optional, VarArg = f.VarArg, Returns = f.Returns,
                RetVarArg = f.RetVarArg, IsMethod = f.IsMethod, SelfNamed = f.SelfNamed, IsMacroExp = f.IsMacroExp, Macro = f.Macro, MacroFile = f.MacroFile, LaxArity = f.LaxArity,
            };
        }
        if (field is PolyType p)
        {
            var np = new PolyType();
            foreach (FuncType o in p.Overloads) np.Overloads.Add((FuncType)Generalize(container, o));
            return np;
        }
        return field;
    }

    // string.find/match/gmatch: result types depend on the captures of a literal pattern
    private Multi AdjustStringSpecial(FuncType f, List<Expr> args, bool isMethod, Multi result)
    {
        if (ReferenceEquals(f, _strUnpack)) return UnknownMulti(); // result types depend on the format string
        if (ReferenceEquals(f, _strFormat))
        {
            CheckFormat(args, isMethod);
            return result;
        }
        if (!ReferenceEquals(f, _strMatch) && !ReferenceEquals(f, _strFind) && !ReferenceEquals(f, _strGmatch)) return result;
        int pi = isMethod ? 0 : 1;
        if (pi >= args.Count || args[pi] is not StringExpr pat) return result;
        var caps = new List<TType>();
        string p = pat.Value;
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '%') { i++; continue; }
            if (c == '[')
            {
                i++;
                if (i < p.Length && p[i] == '^') i++;
                if (i < p.Length && p[i] == ']') i++;
                while (i < p.Length && p[i] != ']') { if (p[i] == '%') i++; i++; }
                continue;
            }
            if (c == '(') caps.Add(i + 1 < p.Length && p[i + 1] == ')' ? PrimType.Integer : PrimType.String);
        }
        var m = new Multi();
        if (ReferenceEquals(f, _strFind))
        {
            m.Types.Add(PrimType.Integer);
            m.Types.Add(PrimType.Integer);
            m.Types.AddRange(caps);
        }
        else
        {
            if (caps.Count == 0) caps.Add(PrimType.String);
            if (ReferenceEquals(f, _strGmatch)) m.Types.Add(new FuncType { Returns = caps });
            else m.Types.AddRange(caps);
        }
        return m;
    }

    // string.format with a literal format: numeric directives need numeric arguments
    private void CheckFormat(List<Expr> args, bool isMethod)
    {
        int fi = isMethod ? -1 : 0;
        string? fmt = fi < 0 ? null : (fi < args.Count && args[fi] is StringExpr fs ? fs.Value : null);
        if (fmt == null) return;
        int ai = fi + 1;
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%') continue;
            i++;
            if (i < fmt.Length && fmt[i] == '%') continue;
            while (i < fmt.Length && "-+ #0123456789.".IndexOf(fmt[i]) >= 0) i++;
            if (i >= fmt.Length) break;
            char c = fmt[i];
            if (ai < args.Count && "diucoxXaAeEfFgG".IndexOf(c) >= 0 && args[ai].TypeInfo is TType at
                && !(ai == args.Count - 1 && IsMultiExpr(args[ai])))
            {
                TType w = Widen(at);
                if (!IsNumeric(w) && !IsUnknown(w) && !IsNil(w) && w is not TypeVar && !(w is UnionType u && u.Members.All(IsNumeric)))
                    Err(args[ai], $"argument {ai + 1}: got {Disp(at)}, expected number");
            }
            ai++;
        }
    }

    private static bool ContainsUnbound(TType t, Dictionary<TypeVar, TType?> infer, int depth = 0)
    {
        if (depth > 20) return false;
        switch (t)
        {
            case TypeVar tv: return infer.TryGetValue(tv, out var b) && b == null;
            case ArrayType a: return ContainsUnbound(a.Elem, infer, depth + 1);
            case MapType m: return ContainsUnbound(m.Key, infer, depth + 1) || ContainsUnbound(m.Value, infer, depth + 1);
            case TupleType tu: return tu.Elems.Any(x => ContainsUnbound(x, infer, depth + 1));
            case UnionType u: return u.Members.Any(x => ContainsUnbound(x, infer, depth + 1));
            case InstType i: return i.Args.Any(x => ContainsUnbound(x, infer, depth + 1));
            case FuncType f:
                return f.Params.Any(x => ContainsUnbound(x, infer, depth + 1)) || f.Returns.Any(x => ContainsUnbound(x, infer, depth + 1))
                    || (f.VarArg != null && ContainsUnbound(f.VarArg, infer, depth + 1));
        }
        return false;
    }

    private static Dictionary<TypeVar, TType> Bound(Dictionary<TypeVar, TType?> infer, bool unboundToUnknown)
    {
        var map = new Dictionary<TypeVar, TType>();
        foreach (var kv in infer)
        {
            if (kv.Value != null) map[kv.Key] = kv.Value;
            else if (unboundToUnknown) map[kv.Key] = PrimType.Unknown;
        }
        return map;
    }

    // pcall(f, ...) / xpcall(f, h, ...): arguments of f are numbered as arguments of pcall
    private int _argOffset;

    /// <summary>Checks a call to a single function signature and returns its result types.</summary>
    private Multi Apply(Node at, FuncType f, List<Expr> args, TType? self, TType? expected = null)
    {
        int argOffset = _argOffset;
        _argOffset = 0;
        var infer = new Dictionary<TypeVar, TType?>();
        if (f.TypeParams != null) foreach (TypeVar tv in f.TypeParams) infer[tv] = null;
        List<TType> ps = f.Params;
        int pi = 0;
        if (self != null)
        {
            if (ps.Count > 0)
            {
                if (!Assignable(self, ps[0], infer) && !IsUnknown(ps[0]) && !f.IsMethod)
                    Err(at, $"self: {Mismatch(self, Subst(ps[0], Bound(infer, false)))}");
                pi = 1;
            }
            else if (f.VarArg != null) Assignable(self, f.VarArg, infer);
        }
        bool openEnded = false;
        bool tooMany = false;
        bool argError = false; // like tl, report only the first mismatching argument of a call
        var emptyFix = new List<(VarInfo v, TType param)>();
        for (int ai = 0; ai < args.Count; ai++)
        {
            Expr arg = args[ai];
            int pidx = pi + ai;
            TType? pt = pidx < ps.Count ? ps[pidx] : f.VarArg;
            bool last = ai == args.Count - 1;
            if (last && IsMultiExpr(arg))
            {
                TType? want = pt == null || ContainsUnbound(pt, infer) ? null : Subst(pt, Bound(infer, true));
                Multi m = CheckMulti(arg, want);
                openEnded = true;
                if (pt == null && !(arg is VarargExpr) && m.Types.Count > 0 && !f.LaxArity) tooMany = true;
                for (int j = 0; j < m.Types.Count; j++)
                {
                    TType? ptj = pidx + j < ps.Count ? ps[pidx + j] : f.VarArg;
                    if (ptj == null) break;
                    if (!Assignable(m.Types[j], ptj, infer) && !argError)
                    {
                        argError = true;
                        Err(arg, $"argument {ai + j + 1 + argOffset}: {Mismatch(m.Types[j], Subst(ptj, Bound(infer, false)))}");
                    }
                }
                continue;
            }
            if (pt == null)
            {
                CheckExpr(arg);
                tooMany = true;
                continue;
            }
            bool unbound = ContainsUnbound(pt, infer);
            TType? expectedArg = unbound && arg is not FunctionExpr && !(arg is TableExpr && pt is RecordType or InstType)
                ? null : Subst(pt, Bound(infer, true));
            TType atype = CheckExpr(arg, expectedArg);
            if (!Assignable(atype, pt, infer) && !argError)
            {
                argError = true;
                Err(arg, $"argument {ai + 1 + argOffset}: {Mismatch(atype, Subst(pt, Bound(infer, false)))}");
            }
            if (atype is EmptyTableType && arg is NameExpr an && Look(_scope, an.Name) is { } av && av.Type is EmptyTableType)
                emptyFix.Add((av, pt));
        }
        int given = args.Count;
        int max = ps.Count - pi;
        int min = Math.Max(0, f.MinArgs - pi);
        if (!f.LaxArity && !argError)
        {
            if (tooMany && f.VarArg == null)
                Err(at, $"wrong number of arguments (given {given}, expects {ArityText(min, max)})");
            else if (!openEnded && given < min)
                Err(at, $"wrong number of arguments (given {given}, expects {(f.VarArg != null ? $"at least {min}" : ArityText(min, max))})");
        }
        foreach (var (v, param) in emptyFix)
        {
            if (v.Type is not EmptyTableType || ContainsUnbound(param, infer)) continue;
            TType t = Subst(param, Bound(infer, true));
            if ((t is ArrayType || t is MapType) && !ContainsUnknown(t)) v.Type = t;
        }
        if (expected != null && f.Returns.Count > 0 && ContainsUnbound(f.Returns[0], infer))
        {
            var retInfer = new Dictionary<TypeVar, TType?>(infer);
            if (Assignable(expected, f.Returns[0], retInfer))
            {
                foreach (var kv in retInfer)
                    if (infer[kv.Key] == null && kv.Value != null)
                        infer[kv.Key] = kv.Value;
            }
        }
        var map = Bound(infer, true);
        var res = new Multi { VarTail = f.RetVarArg };
        foreach (TType r in f.Returns) res.Types.Add(Subst(r, map));
        return res;
    }

    private static string ArityText(int min, int max) =>
        min == max ? max.ToString() : $"at least {min} and at most {max}";
}
