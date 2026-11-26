using System.Text;
using Cameo.Runtime.Syntax;

namespace Cameo.Runtime.Types;

public enum Prim { Any, Nil, Boolean, Integer, Number, String, Thread, Unknown }

/// <summary>Base class of semantic types produced by the type checker.</summary>
public closed class TType
{
    public abstract string Show();
    public override string ToString() => Show();
}

/// <summary>Primitive types. <c>Unknown</c> is the checker's "gave up" type, compatible with everything.</summary>
public sealed class PrimType : TType
{
    public readonly Prim Kind;
    private PrimType(Prim k) { Kind = k; }
    public static readonly PrimType Any = new(Prim.Any);
    public static readonly PrimType Nil = new(Prim.Nil);
    public static readonly PrimType Boolean = new(Prim.Boolean);
    public static readonly PrimType Integer = new(Prim.Integer);
    public static readonly PrimType Number = new(Prim.Number);
    public static readonly PrimType String = new(Prim.String);
    public static readonly PrimType Thread = new(Prim.Thread);
    public static readonly PrimType Unknown = new(Prim.Unknown);
    public override string Show() => Kind switch
    {
        Prim.Any => "any", Prim.Nil => "nil", Prim.Boolean => "boolean", Prim.Integer => "integer",
        Prim.Number => "number", Prim.String => "string", Prim.Thread => "thread", _ => "<unknown>",
    };
}

/// <summary>The type of a string literal expression (a string that may also satisfy an enum).</summary>
public sealed class StringLitType : TType
{
    public readonly string Value;
    public StringLitType(string v) { Value = v; }
    public override string Show() => "string \"" + Value + "\"";
}

public sealed class ArrayType : TType
{
    public TType Elem;
    public ArrayType(TType e) { Elem = e; }
    public override string Show() => "{" + Elem.Show() + "}";
}

public sealed class TupleType : TType
{
    public List<TType> Elems;
    public TupleType(List<TType> e) { Elems = e; }
    public override string Show() => "{" + string.Join(", ", Elems.Select(e => e.Show())) + "}";
}

public sealed class MapType : TType
{
    public TType Key, Value;
    public MapType(TType k, TType v) { Key = k; Value = v; }
    public override string Show() => "{" + Key.Show() + " : " + Value.Show() + "}";
}

public sealed class EnumType : TType
{
    public string Name = "enum";
    public HashSet<string> Values = new();
    public List<string> Order = new();
    public override string Show() => Name;
}

public sealed class TypeVar : TType
{
    public string Name;
    public TType? Constraint;
    public TypeVar(string n) { Name = n; }
    public override string Show() => Name;
}

public sealed class FuncType : TType
{
    public List<TypeVar>? TypeParams;
    public List<TType> Params = new();
    public List<bool> Optional = new();
    public TType? VarArg;
    public List<TType> Returns = new();
    public bool RetVarArg;
    public bool IsBare;
    public bool IsMethod;
    /// <summary>Declared with a first parameter named <c>self</c> (so it is meant to be called with ':').</summary>
    public bool SelfNamed;
    public bool IsMacroExp;
    /// <summary>Declaration of the macroexp (and the file it came from), for expansion at use sites.</summary>
    public Cameo.Runtime.Syntax.MacroExp? Macro;
    public string? MacroFile;
    /// <summary>Declared with arity checking disabled / unknown arity (e.g. unannotated).</summary>
    public bool LaxArity;

    public int MinArgs
    {
        get
        {
            int n = Params.Count;
            while (n > 0 && Optional[n - 1]) n--;
            return n;
        }
    }

    public override string Show()
    {
        if (IsBare) return "function";
        var sb = new StringBuilder("function");
        if (TypeParams != null) sb.Append('<').Append(string.Join(", ", TypeParams.Select(t => t.Name))).Append('>');
        sb.Append('(');
        for (int i = 0; i < Params.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            if (Optional[i]) sb.Append("? ");
            sb.Append(Params[i].Show());
        }
        if (VarArg != null)
        {
            if (Params.Count > 0) sb.Append(", ");
            sb.Append(VarArg.Show()).Append("...");
        }
        sb.Append(')');
        if (Returns.Count > 0)
        {
            sb.Append(": ");
            sb.Append(string.Join(", ", Returns.Select(r => r.Show())));
            if (RetVarArg) sb.Append("...");
        }
        return sb.ToString();
    }
}

/// <summary>A set of overloads (a record field declared several times).</summary>
public sealed class PolyType : TType
{
    public List<FuncType> Overloads = new();
    public override string Show() => "polymorphic function (with types " + string.Join(" and ", Overloads.Select(o => o.Show())) + ")";
}

public sealed class UnionType : TType
{
    public List<TType> Members;
    public UnionType(List<TType> m) { Members = m; }
    public override string Show() => string.Join(" | ", Members.Select(m => m.Show()));
}

public sealed class RecordType : TType
{
    public string Name = "record";
    public bool IsInterface;
    public bool IsUserdata;
    /// <summary>Inferred from a table literal (structural).</summary>
    public bool IsAnonymous;
    public List<TypeVar>? TypeParams;
    public Dictionary<string, TType> Fields = new();
    public List<string> FieldOrder = new();
    public Dictionary<string, TType> MetaFields = new();
    public Dictionary<string, TType> Nested = new();
    public TType? ArrayElem;
    /// <summary>Element types when the record's array part is a tuple (<c>is {A, B}</c>);
    /// <see cref="ArrayElem"/> is then the union of these.</summary>
    public List<TType>? TupleElems;
    public List<TType> Interfaces = new();
    /// <summary>Members of a class that are protected (1) or private (2).</summary>
    public Dictionary<string, int>? Visibility;
    /// <summary>Properties of a class that have a getter and no setter.</summary>
    public HashSet<string>? ReadOnly;
    /// <summary>Abstract functions a class declares or inherits and does not define: it cannot be instantiated.</summary>
    public HashSet<string>? Unimplemented;
    internal HashSet<string>? ClassDefines;
    /// <summary>For a class: where its declaration is, which is where its private members may be used.</summary>
    public (int StartLine, int StartCol, int EndLine, int EndCol)? ClassRange;
    public string? WhereSource;
    public string File = "";
    /// <summary>Set for Cameo records with inline methods so typed table literals attach the record metatable.</summary>
    public string? CameoMetatableVar;
    public string? CameoRecordId;
    /// <summary>True for Cameo braced records, enabling required-field checking on literals.</summary>
    public bool IsCameoRecord;
    /// <summary>Names of Cameo optional fields (<c>field?: T</c>) that may be omitted in literals.</summary>
    public HashSet<string>? OptionalFields;
    /// <summary>Names of inline Cameo methods attached via the record metatable (not required in table literals).</summary>
    public HashSet<string>? CameoMethodNames;

    // lazily resolved declaration
    internal RecordTypeNode? Decl;
    internal object? BodyScope;
    internal bool Filled = true;
    internal bool Filling;

    [ThreadStatic] private static int _showDepth;

    public override string Show()
    {
        if (!IsAnonymous) return Name;
        if (_showDepth > 2) return "record (...)";
        _showDepth++;
        try { return ShowAnonymous(); }
        finally { _showDepth--; }
    }

    private string ShowAnonymous()
    {
        var sb = new StringBuilder("record (");
        bool first = true;
        if (ArrayElem != null) { sb.Append('{').Append(ArrayElem.Show()).Append('}'); first = false; }
        foreach (string f in FieldOrder)
        {
            if (!first) sb.Append("; ");
            first = false;
            sb.Append(f).Append(": ").Append(Fields[f].Show());
        }
        return sb.Append(')').ToString();
    }
}

/// <summary>An instantiation of a generic record: <c>Def&lt;Args&gt;</c>.</summary>
public sealed class InstType : TType
{
    public RecordType Def;
    public List<TType> Args;
    public InstType(RecordType d, List<TType> a) { Def = d; Args = a; }
    public override string Show() => Def.Name + "<" + string.Join(", ", Args.Select(a => a.Show())) + ">";
}

/// <summary>The type of <c>{}</c> before its use determines an array/map type.</summary>
public sealed class EmptyTableType : TType
{
    public override string Show() => "{}";
}

/// <summary>Zero or more values (function results, varargs).</summary>
public sealed class Multi
{
    public List<TType> Types = new();
    /// <summary>The last type repeats indefinitely.</summary>
    public bool VarTail;
    public Multi() { }
    public Multi(TType t) { Types.Add(t); }
    public TType First => Types.Count > 0 ? Types[0] : PrimType.Nil;
    /// <summary>Type of the i-th value; null when there is certainly no such value.</summary>
    public TType? At(int i)
    {
        if (i < Types.Count) return Types[i];
        if (VarTail && Types.Count > 0) return Types[Types.Count - 1];
        return null;
    }
}

public sealed class ModuleSource
{
    public string Name = "";
    public string Path = "";
    public Chunk Ast = null!;
    public bool IsDeclarationFile;
    /// <summary>A plain Lua module without declarations: its value is untyped (Ast is unused).</summary>
    public bool IsUntyped;
}

public sealed class CheckResult
{
    public List<Diagnostic> Errors = new();
    public List<Diagnostic> Warnings = new();
    public bool Ok => Errors.Count == 0;
    /// <summary>The type of the value returned by the chunk (module type), if any.</summary>
    public TType? ModuleType;
}
