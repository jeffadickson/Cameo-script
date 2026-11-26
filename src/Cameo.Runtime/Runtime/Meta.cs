namespace Cameo.Runtime.Vm;

/// <summary>A metamethod (or other well-known field) name with its hash computed once, so that
/// lookups do not re-hash the name on every use. (Profiling showed string hashing and comparison
/// of names like "__index" at ~7% of an OOP-style workload.)</summary>
public readonly struct MetaKey
{
    public readonly string Name;
    public readonly int Hash;
    public MetaKey(string name) { Name = string.Intern(name); Hash = Name.GetHashCode(); }
    public override string ToString() => Name;
}

/// <summary>The well-known metamethod names.</summary>
public static class Meta
{
    public static readonly MetaKey Index = new("__index");
    public static readonly MetaKey Newindex = new("__newindex");
    public static readonly MetaKey Call = new("__call");
    public static readonly MetaKey Eq = new("__eq");
    public static readonly MetaKey Lt = new("__lt");
    public static readonly MetaKey Le = new("__le");
    public static readonly MetaKey Len = new("__len");
    public static readonly MetaKey Unm = new("__unm");
    public static readonly MetaKey Bnot = new("__bnot");
    public static readonly MetaKey Concat = new("__concat");
    public static readonly MetaKey Close = new("__close");
    public static readonly MetaKey Tostring = new("__tostring");
    public static readonly MetaKey Name = new("__name");
    public static readonly MetaKey Pairs = new("__pairs");
    public static readonly MetaKey Metatable = new("__metatable");
    public static readonly MetaKey Mode = new("__mode");
    public static readonly MetaKey Gc = new("__gc");
    public static readonly MetaKey Add = new("__add");
    public static readonly MetaKey Sub = new("__sub");
    public static readonly MetaKey Mul = new("__mul");
    public static readonly MetaKey Div = new("__div");
    public static readonly MetaKey Mod = new("__mod");
    public static readonly MetaKey Pow = new("__pow");
    public static readonly MetaKey Idiv = new("__idiv");
    public static readonly MetaKey Band = new("__band");
    public static readonly MetaKey Bor = new("__bor");
    public static readonly MetaKey Bxor = new("__bxor");
    public static readonly MetaKey Shl = new("__shl");
    public static readonly MetaKey Shr = new("__shr");

    /// <summary>Events for the binary arithmetic/bitwise opcodes, indexed by <c>op - OpCode.Add</c>.</summary>
    public static readonly MetaKey[] Arith = { Add, Sub, Mul, Div, Mod, Pow, Idiv, Band, Bor, Bxor, Shl, Shr };
}
