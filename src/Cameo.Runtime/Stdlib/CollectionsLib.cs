using System.Runtime.CompilerServices;
using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>Value equality comparer for <see cref="LuaDict"/> matching Lua key semantics (integer 1 == float 1.0).</summary>
internal sealed class LuaValueKeyComparer : IEqualityComparer<Value>
{
    public static readonly LuaValueKeyComparer Instance = new();
    public bool Equals(Value x, Value y) => x.RawEquals(y);
    public int GetHashCode(Value obj) => obj.GetHashCode();
}

/// <summary>
/// First-class C#-backed list (<c>List&lt;T&gt;</c>) with 1-based indexing, exact O(1) length
/// (safe with nil elements), unified Cameo dot methods, deep freezing, JSON array encoding,
/// and cross-VM value transfer.
/// </summary>
public sealed class LuaList : IUserdata, IFreezable, IValueTransferable
{
    private readonly LuaState _state;
    internal readonly List<Value> Items;
    internal bool Frozen;

    public LuaList(LuaState state, int capacity = 0)
    {
        _state = state;
        Items = capacity > 0 ? new List<Value>(capacity) : new List<Value>();
    }

    internal LuaList(LuaState state, List<Value> items, bool frozen = false)
    {
        _state = state;
        Items = items;
        Frozen = frozen;
    }

    public string TypeName => "List";
    public Table? Metatable { get => _state.ListMeta; set { } }
    public bool IsFrozen => Frozen;
    public int Count => Items.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckMutable() { if (Frozen) ThrowFrozen(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFrozen() => throw new LuaException("cannot mutate frozen List");

    public void Freeze(HashSet<object> visited)
    {
        if (!visited.Add(this)) return;
        Frozen = true;
        for (int i = 0; i < Items.Count; i++)
            CollectionsLib.FreezeValue(Items[i], visited);
    }

    public Value TransferTo(LuaState target, Func<Value, Value> copyChild)
    {
        var copy = new List<Value>(Items.Count);
        for (int i = 0; i < Items.Count; i++) copy.Add(copyChild(Items[i]));
        return Value.FromUserdata(new LuaList(target, copy, Frozen));
    }
}

/// <summary>
/// First-class C#-backed insertion-ordered dictionary (<c>Dict&lt;K, V&gt;</c>) with exact O(1)
/// length, deterministic iteration order (Starlark-grade), unified Cameo dot methods, deep
/// freezing, JSON object encoding, and cross-VM value transfer.
/// </summary>
public sealed class LuaDict : IUserdata, IFreezable, IValueTransferable
{
    private readonly LuaState _state;
    internal readonly OrderedDictionary<Value, Value> Entries;
    internal bool Frozen;

    public LuaDict(LuaState state, int capacity = 0)
    {
        _state = state;
        Entries = capacity > 0
            ? new OrderedDictionary<Value, Value>(capacity, LuaValueKeyComparer.Instance)
            : new OrderedDictionary<Value, Value>(LuaValueKeyComparer.Instance);
    }

    internal LuaDict(LuaState state, OrderedDictionary<Value, Value> entries, bool frozen = false)
    {
        _state = state;
        Entries = entries;
        Frozen = frozen;
    }

    public string TypeName => "Dict";
    public Table? Metatable { get => _state.DictMeta; set { } }
    public bool IsFrozen => Frozen;
    public int Count => Entries.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CheckMutable() { if (Frozen) ThrowFrozen(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFrozen() => throw new LuaException("cannot mutate frozen Dict");

    internal static Value NormalizeKey(in Value key)
    {
        if (key.IsNil) throw new LuaException("Dict key cannot be nil");
        if (key.Kind == ValueKind.Float)
        {
            if (double.IsNaN(key.N)) throw new LuaException("Dict key cannot be NaN");
            if (LuaNumber.FloatToInteger(key.N, out long i)) return Value.FromInt(i);
        }
        return key;
    }

    public void Freeze(HashSet<object> visited)
    {
        if (!visited.Add(this)) return;
        Frozen = true;
        foreach (KeyValuePair<Value, Value> kv in Entries)
        {
            CollectionsLib.FreezeValue(kv.Key, visited);
            CollectionsLib.FreezeValue(kv.Value, visited);
        }
    }

    public Value TransferTo(LuaState target, Func<Value, Value> copyChild)
    {
        var copy = new OrderedDictionary<Value, Value>(Entries.Count, LuaValueKeyComparer.Instance);
        foreach (KeyValuePair<Value, Value> kv in Entries)
            copy[NormalizeKey(copyChild(kv.Key))] = copyChild(kv.Value);
        return Value.FromUserdata(new LuaDict(target, copy, Frozen));
    }
}

/// <summary>
/// Registers C#-backed <c>List</c>, <c>Dict</c>, deep <c>freeze</c>/<c>isFrozen</c>,
/// Cameo collection iterators (<c>__cameo_iter1</c>/<c>__cameo_iter2</c>), and chunked
/// deterministic <c>parallel.map</c>.
/// </summary>
public static class CollectionsLib
{
    public static void Open(LuaState L)
    {
        OpenList(L);
        OpenDict(L);
        OpenParallel(L);
        L.Register(L.Globals, "freeze", static (ref CallContext c) =>
        {
            c.CheckAny(0);
            FreezeValue(c[0], new HashSet<object>(ReferenceEqualityComparer.Instance));
            return c.Return(c[0]);
        });
        L.Register(L.Globals, "isFrozen", static (ref CallContext c) =>
            c.Return(IsValueFrozen(c[0])));
        L.Register(L.Globals, "__cameo_iter1", static (ref CallContext c) => Iter1(ref c));
        L.Register(L.Globals, "__cameo_iter2", static (ref CallContext c) => Iter2(ref c));
    }

    /// <summary>Recursively freezes tables and <see cref="IFreezable"/> userdata values.</summary>
    public static void FreezeValue(in Value v, HashSet<object> visited)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (v.Kind == ValueKind.Table)
        {
            Table t = v.AsTable;
            if (!visited.Add(t)) return;
            t.Frozen = true;
            foreach (KeyValuePair<Value, Value> kv in t.Pairs())
            {
                FreezeValue(kv.Key, visited);
                FreezeValue(kv.Value, visited);
            }
        }
        else if (v.Kind == ValueKind.Userdata && v.O is IFreezable f)
        {
            f.Freeze(visited);
        }
        else if (v.Kind == ValueKind.Userdata && v.O is ResultLib.TaggedResult r)
        {
            if (visited.Add(r)) FreezeValue(r.Payload, visited);
        }
    }

    public static bool IsValueFrozen(in Value v) => v.Kind switch
    {
        ValueKind.Nil or ValueKind.Boolean or ValueKind.Integer or ValueKind.Float or ValueKind.String or ValueKind.Function => true,
        ValueKind.Table => v.AsTable.Frozen,
        ValueKind.Userdata when v.O is IFreezable f => f.IsFrozen,
        ValueKind.Userdata when v.O is Utf16String or ResultLib.TaggedResult => true,
        _ => false,
    };

    // ------------------------------------------------------------------ List<T>

    private static LuaList CheckList(ref CallContext c, int i)
    {
        if (c[i].O is LuaList l) return l;
        throw c.TypeError(i, "List");
    }

    private static void OpenList(LuaState L)
    {
        var lib = new Table();
        L.Register(lib, "new", static (ref CallContext c) =>
        {
            int cap = c.IsNoneOrNil(0) ? 0 : (int)Math.Clamp(c.CheckInt(0), 0, 1 << 24);
            return c.Return(Value.FromUserdata(new LuaList(c.L, cap)));
        });
        L.Register(lib, "of", static (ref CallContext c) =>
        {
            var list = new LuaList(c.L, c.Count);
            for (int i = 0; i < c.Count; i++) list.Items.Add(c[i]);
            return c.Return(Value.FromUserdata(list));
        });
        L.Register(lib, "from", static (ref CallContext c) =>
        {
            if (c[0].O is LuaList src)
                return c.Return(Value.FromUserdata(new LuaList(c.L, new List<Value>(src.Items))));
            if (c[0].O is FloatArray fa)
            {
                var list = new LuaList(c.L, fa.Data.Length);
                foreach (double d in fa.Data) list.Items.Add(Value.FromFloat(d));
                return c.Return(Value.FromUserdata(list));
            }
            Table t = c.CheckTable(0);
            int n = t.Length;
            var res = new LuaList(c.L, n);
            for (int i = 1; i <= n; i++) res.Items.Add(t.GetInt(i));
            return c.Return(Value.FromUserdata(res));
        });

        L.RegisterInstance<LuaList>(lib, "push", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            for (int i = 1; i < c.Count; i++) l.Items.Add(c[i]);
            return c.Return(Value.FromInt(l.Items.Count));
        });
        L.RegisterInstance<LuaList>(lib, "pop", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            if (l.Items.Count == 0) return c.Return(Value.Nil);
            int last = l.Items.Count - 1;
            Value v = l.Items[last];
            l.Items.RemoveAt(last);
            return c.Return(v);
        });
        L.RegisterInstance<LuaList>(lib, "len", static (ref CallContext c) =>
            c.Return(Value.FromInt(CheckList(ref c, 0).Items.Count)));
        L.RegisterInstance<LuaList>(lib, "isEmpty", static (ref CallContext c) =>
            c.Return(CheckList(ref c, 0).Items.Count == 0));
        L.RegisterInstance<LuaList>(lib, "get", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            long i = c.CheckInt(1);
            if ((ulong)(i - 1) >= (ulong)l.Items.Count) return c.Return(Value.Nil);
            return c.Return(l.Items[(int)i - 1]);
        });
        L.RegisterInstance<LuaList>(lib, "set", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            long i = c.CheckInt(1);
            if ((ulong)(i - 1) >= (ulong)l.Items.Count)
                throw c.ArgError(1, $"index {i} out of range 1..{l.Items.Count}");
            l.Items[(int)i - 1] = c[2];
            return c.Return();
        });
        L.RegisterInstance<LuaList>(lib, "insert", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            long pos = c.CheckInt(1);
            if (pos < 1 || pos > l.Items.Count + 1)
                throw c.ArgError(1, $"position {pos} out of range 1..{l.Items.Count + 1}");
            l.Items.Insert((int)pos - 1, c[2]);
            return c.Return();
        });
        L.RegisterInstance<LuaList>(lib, "remove", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            if (l.Items.Count == 0) return c.Return(Value.Nil);
            long pos = c.OptInt(1, l.Items.Count);
            if (pos < 1 || pos > l.Items.Count)
                throw c.ArgError(1, $"position {pos} out of range 1..{l.Items.Count}");
            Value v = l.Items[(int)pos - 1];
            l.Items.RemoveAt((int)pos - 1);
            return c.Return(v);
        });
        L.RegisterInstance<LuaList>(lib, "clear", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            l.Items.Clear();
            return c.Return();
        });
        L.RegisterInstance<LuaList>(lib, "contains", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value target = c[1];
            for (int i = 0; i < l.Items.Count; i++)
                if (c.L.Equals(l.Items[i], target)) return c.Return(true);
            return c.Return(false);
        });
        L.RegisterInstance<LuaList>(lib, "indexOf", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value target = c[1];
            for (int i = 0; i < l.Items.Count; i++)
                if (c.L.Equals(l.Items[i], target)) return c.Return(Value.FromInt(i + 1));
            return c.Return(Value.Nil);
        });
        L.RegisterInstance<LuaList>(lib, "slice", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            int n = l.Items.Count;
            long a = c.OptInt(1, 1);
            long b = c.OptInt(2, n);
            if (a < 0) a = n + a + 1;
            if (b < 0) b = n + b + 1;
            int from = (int)Math.Clamp(a, 1, n + 1);
            int to = (int)Math.Clamp(b, 0, n);
            var res = new LuaList(c.L, Math.Max(0, to - from + 1));
            for (int i = from; i <= to; i++) res.Items.Add(l.Items[i - 1]);
            return c.Return(Value.FromUserdata(res));
        });
        L.RegisterInstance<LuaList>(lib, "join", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            string sep = c.OptString(1, "");
            var sb = new StringBuilder();
            for (int i = 0; i < l.Items.Count; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(c.L.ToStringMeta(l.Items[i]));
                c.L.CheckStringLength(sb.Length);
            }
            return c.Return(sb.ToString());
        });
        L.RegisterInstance<LuaList>(lib, "reverse", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            l.Items.Reverse();
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaList>(lib, "sort", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            Value comp = c.IsNoneOrNil(1) ? Value.Nil : c.CheckFunction(1);
            var arr = l.Items.ToArray();
            LuaState state = c.L;
            MergeSort(state, arr, comp);
            l.Items.Clear();
            l.Items.AddRange(arr);
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaList>(lib, "map", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value fn = c.CheckFunction(1);
            var res = new LuaList(c.L, l.Items.Count);
            for (int i = 0; i < l.Items.Count; i++)
            {
                if (c.L.PollStep()) c.L.CheckAbortOrStep();
                res.Items.Add(c.L.Call1(fn, l.Items[i], Value.FromInt(i + 1)));
            }
            return c.Return(Value.FromUserdata(res));
        });
        L.RegisterInstance<LuaList>(lib, "filter", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value fn = c.CheckFunction(1);
            var res = new LuaList(c.L);
            for (int i = 0; i < l.Items.Count; i++)
            {
                if (c.L.PollStep()) c.L.CheckAbortOrStep();
                if (c.L.Call1(fn, l.Items[i], Value.FromInt(i + 1)).IsTruthy)
                    res.Items.Add(l.Items[i]);
            }
            return c.Return(Value.FromUserdata(res));
        });
        L.RegisterInstance<LuaList>(lib, "reduce", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value acc = c[1];
            Value fn = c.CheckFunction(2);
            for (int i = 0; i < l.Items.Count; i++)
            {
                if (c.L.PollStep()) c.L.CheckAbortOrStep();
                acc = c.L.Call1(fn, acc, l.Items[i], Value.FromInt(i + 1));
            }
            return c.Return(acc);
        });
        L.RegisterInstance<LuaList>(lib, "any", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value fn = c.CheckFunction(1);
            for (int i = 0; i < l.Items.Count; i++)
            {
                if (c.L.PollStep()) c.L.CheckAbortOrStep();
                if (c.L.Call1(fn, l.Items[i], Value.FromInt(i + 1)).IsTruthy) return c.Return(true);
            }
            return c.Return(false);
        });
        L.RegisterInstance<LuaList>(lib, "all", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value fn = c.CheckFunction(1);
            for (int i = 0; i < l.Items.Count; i++)
            {
                if (c.L.PollStep()) c.L.CheckAbortOrStep();
                if (!c.L.Call1(fn, l.Items[i], Value.FromInt(i + 1)).IsTruthy) return c.Return(false);
            }
            return c.Return(true);
        });
        L.RegisterInstance<LuaList>(lib, "extend", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            if (c[1].O is LuaList other)
            {
                if (ReferenceEquals(l, other)) l.Items.AddRange(other.Items.ToArray());
                else l.Items.AddRange(other.Items);
            }
            else
            {
                Table t = c.CheckTable(1);
                int n = t.Length;
                for (int i = 1; i <= n; i++) l.Items.Add(t.GetInt(i));
            }
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaList>(lib, "clone", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            return c.Return(Value.FromUserdata(new LuaList(c.L, new List<Value>(l.Items))));
        });
        L.RegisterInstance<LuaList>(lib, "toTable", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            var t = new Table(l.Items.Count, 0);
            for (int i = 0; i < l.Items.Count; i++) t.SetInt(i + 1, l.Items[i]);
            return c.Return(Value.FromTable(t));
        });
        L.RegisterInstance<LuaList>(lib, "freeze", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.Freeze(new HashSet<object>(ReferenceEqualityComparer.Instance));
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaList>(lib, "isFrozen", static (ref CallContext c) =>
            c.Return(CheckList(ref c, 0).Frozen));
        L.RegisterInstance<LuaList>(lib, "parallelMap", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value fn = c.CheckFunction(1);
            LuaList result = ParallelMapList(c.L, l.Items, fn);
            return c.Return(Value.FromUserdata(result));
        });

        Table mt = L.ListMeta;
        mt.SetStr("__name", "List");
        L.Register(mt, "__len", static (ref CallContext c) => c.Return(Value.FromInt(CheckList(ref c, 0).Items.Count)));
        L.Register(mt, "__index", (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value k = c[1];
            if (k.Kind == ValueKind.String) return c.Return(lib.GetStr(k.AsString));
            if (k.TryToInteger(out long i))
                return c.Return((ulong)(i - 1) < (ulong)l.Items.Count ? l.Items[(int)i - 1] : Value.Nil);
            return c.Return(Value.Nil);
        });
        L.Register(mt, "__newindex", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            l.CheckMutable();
            Value k = c[1];
            if (k.IsString || !k.TryToInteger(out long i))
                throw new LuaException($"attempt to set field '{k}' of a List");
            if (i >= 1 && i <= l.Items.Count) { l.Items[(int)i - 1] = c[2]; return 0; }
            if (i == l.Items.Count + 1) { l.Items.Add(c[2]); return 0; }
            throw new LuaException($"index {i} is outside the List (length {l.Items.Count})");
        });
        L.Register(mt, "__pairs", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            Value iter = Value.FromFunction(static (ref CallContext ic) =>
            {
                LuaList list = CheckList(ref ic, 0);
                long idx = ic[1].IsNil ? 1 : ic.CheckInt(1) + 1;
                if (idx < 1 || idx > list.Items.Count) return ic.Return(Value.Nil);
                return ic.Return(Value.FromInt(idx), list.Items[(int)idx - 1]);
            }, "list_iter");
            return c.Return(iter, c[0], Value.Nil);
        });
        L.Register(mt, "__eq", static (ref CallContext c) =>
        {
            if (c[0].O is not LuaList a || c[1].O is not LuaList b) return c.Return(false);
            if (ReferenceEquals(a, b)) return c.Return(true);
            if (a.Items.Count != b.Items.Count) return c.Return(false);
            for (int i = 0; i < a.Items.Count; i++)
                if (!c.L.Equals(a.Items[i], b.Items[i])) return c.Return(false);
            return c.Return(true);
        });
        L.Register(mt, "__tostring", static (ref CallContext c) =>
        {
            LuaList l = CheckList(ref c, 0);
            var sb = new StringBuilder("[");
            for (int i = 0; i < l.Items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(c.L.ToStringMeta(l.Items[i]));
                if (sb.Length > 4096) { sb.Append(", ..."); break; }
            }
            sb.Append(']');
            return c.Return(sb.ToString());
        });

        L.Globals.SetStr("List", lib);
        L.LoadedModules.SetStr("List", lib);
    }

    // ------------------------------------------------------------------ Dict<K, V>

    private static LuaDict CheckDict(ref CallContext c, int i)
    {
        if (c[i].O is LuaDict d) return d;
        throw c.TypeError(i, "Dict");
    }

    private static void OpenDict(LuaState L)
    {
        var lib = new Table();
        L.Register(lib, "new", static (ref CallContext c) =>
        {
            int cap = c.IsNoneOrNil(0) ? 0 : (int)Math.Clamp(c.CheckInt(0), 0, 1 << 24);
            return c.Return(Value.FromUserdata(new LuaDict(c.L, cap)));
        });
        L.Register(lib, "from", static (ref CallContext c) =>
        {
            if (c[0].O is LuaDict src)
            {
                var copy = new OrderedDictionary<Value, Value>(src.Entries.Count, LuaValueKeyComparer.Instance);
                foreach (KeyValuePair<Value, Value> kv in src.Entries) copy[kv.Key] = kv.Value;
                return c.Return(Value.FromUserdata(new LuaDict(c.L, copy)));
            }
            Table t = c.CheckTable(0);
            var dict = new LuaDict(c.L);
            foreach (KeyValuePair<Value, Value> kv in t.Pairs())
                dict.Entries[LuaDict.NormalizeKey(kv.Key)] = kv.Value;
            return c.Return(Value.FromUserdata(dict));
        });

        L.RegisterInstance<LuaDict>(lib, "get", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            if (c[1].IsNil) return c.Return(Value.Nil);
            Value k = LuaDict.NormalizeKey(c[1]);
            return c.Return(d.Entries.TryGetValue(k, out Value v) ? v : Value.Nil);
        });
        L.RegisterInstance<LuaDict>(lib, "getOr", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            if (c[1].IsNil) return c.Return(c[2]);
            Value k = LuaDict.NormalizeKey(c[1]);
            return c.Return(d.Entries.TryGetValue(k, out Value v) ? v : c[2]);
        });
        L.RegisterInstance<LuaDict>(lib, "set", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            d.CheckMutable();
            Value k = LuaDict.NormalizeKey(c[1]);
            d.Entries[k] = c[2];
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaDict>(lib, "has", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            if (c[1].IsNil) return c.Return(false);
            if (c[1].Kind == ValueKind.Float && double.IsNaN(c[1].N)) return c.Return(false);
            return c.Return(d.Entries.ContainsKey(LuaDict.NormalizeKey(c[1])));
        });
        L.RegisterInstance<LuaDict>(lib, "delete", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            d.CheckMutable();
            if (c[1].IsNil) return c.Return(false);
            return c.Return(d.Entries.Remove(LuaDict.NormalizeKey(c[1])));
        });
        L.RegisterInstance<LuaDict>(lib, "clear", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            d.CheckMutable();
            d.Entries.Clear();
            return c.Return();
        });
        L.RegisterInstance<LuaDict>(lib, "len", static (ref CallContext c) =>
            c.Return(Value.FromInt(CheckDict(ref c, 0).Entries.Count)));
        L.RegisterInstance<LuaDict>(lib, "isEmpty", static (ref CallContext c) =>
            c.Return(CheckDict(ref c, 0).Entries.Count == 0));
        L.RegisterInstance<LuaDict>(lib, "keys", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            var list = new LuaList(c.L, d.Entries.Count);
            foreach (KeyValuePair<Value, Value> kv in d.Entries) list.Items.Add(kv.Key);
            return c.Return(Value.FromUserdata(list));
        });
        L.RegisterInstance<LuaDict>(lib, "values", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            var list = new LuaList(c.L, d.Entries.Count);
            foreach (KeyValuePair<Value, Value> kv in d.Entries) list.Items.Add(kv.Value);
            return c.Return(Value.FromUserdata(list));
        });
        L.RegisterInstance<LuaDict>(lib, "items", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            var list = new LuaList(c.L, d.Entries.Count);
            foreach (KeyValuePair<Value, Value> kv in d.Entries)
            {
                var pair = new Table(2, 0);
                pair.SetInt(1, kv.Key);
                pair.SetInt(2, kv.Value);
                list.Items.Add(Value.FromTable(pair));
            }
            return c.Return(Value.FromUserdata(list));
        });
        L.RegisterInstance<LuaDict>(lib, "merge", static (ref CallContext c) =>
        {
            LuaDict a = CheckDict(ref c, 0);
            var merged = new OrderedDictionary<Value, Value>(a.Entries.Count, LuaValueKeyComparer.Instance);
            foreach (KeyValuePair<Value, Value> kv in a.Entries) merged[kv.Key] = kv.Value;
            if (c[1].O is LuaDict b)
                foreach (KeyValuePair<Value, Value> kv in b.Entries) merged[kv.Key] = kv.Value;
            else
                foreach (KeyValuePair<Value, Value> kv in c.CheckTable(1).Pairs())
                    merged[LuaDict.NormalizeKey(kv.Key)] = kv.Value;
            return c.Return(Value.FromUserdata(new LuaDict(c.L, merged)));
        });
        L.RegisterInstance<LuaDict>(lib, "clone", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            var copy = new OrderedDictionary<Value, Value>(d.Entries.Count, LuaValueKeyComparer.Instance);
            foreach (KeyValuePair<Value, Value> kv in d.Entries) copy[kv.Key] = kv.Value;
            return c.Return(Value.FromUserdata(new LuaDict(c.L, copy)));
        });
        L.RegisterInstance<LuaDict>(lib, "toTable", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            var t = new Table(0, d.Entries.Count);
            foreach (KeyValuePair<Value, Value> kv in d.Entries) t.Set(kv.Key, kv.Value);
            return c.Return(Value.FromTable(t));
        });
        L.RegisterInstance<LuaDict>(lib, "freeze", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            d.Freeze(new HashSet<object>(ReferenceEqualityComparer.Instance));
            return c.Return(c[0]);
        });
        L.RegisterInstance<LuaDict>(lib, "isFrozen", static (ref CallContext c) =>
            c.Return(CheckDict(ref c, 0).Frozen));

        Table mt = L.DictMeta;
        mt.SetStr("__name", "Dict");
        L.Register(mt, "__len", static (ref CallContext c) => c.Return(Value.FromInt(CheckDict(ref c, 0).Entries.Count)));
        L.Register(mt, "__index", (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            Value k = c[1];
            if (k.Kind == ValueKind.String)
            {
                Value method = lib.GetStr(k.AsString);
                if (!method.IsNil) return c.Return(method);
            }
            if (k.IsNil || (k.Kind == ValueKind.Float && double.IsNaN(k.N))) return c.Return(Value.Nil);
            return c.Return(d.Entries.TryGetValue(LuaDict.NormalizeKey(k), out Value v) ? v : Value.Nil);
        });
        L.Register(mt, "__newindex", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            d.CheckMutable();
            Value k = LuaDict.NormalizeKey(c[1]);
            if (c[2].IsNil) d.Entries.Remove(k);
            else d.Entries[k] = c[2];
            return 0;
        });
        L.Register(mt, "__pairs", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            var snap = new KeyValuePair<Value, Value>[d.Entries.Count];
            int idx = 0;
            foreach (KeyValuePair<Value, Value> kv in d.Entries) snap[idx++] = kv;
            int pos = 0;
            Value iter = Value.FromFunction((ref CallContext ic) =>
            {
                if (pos >= snap.Length) return ic.Return(Value.Nil);
                var kv = snap[pos++];
                return ic.Return(kv.Key, kv.Value);
            }, "dict_iter");
            return c.Return(iter, c[0], Value.Nil);
        });
        L.Register(mt, "__eq", static (ref CallContext c) =>
        {
            if (c[0].O is not LuaDict a || c[1].O is not LuaDict b) return c.Return(false);
            if (ReferenceEquals(a, b)) return c.Return(true);
            if (a.Entries.Count != b.Entries.Count) return c.Return(false);
            foreach (KeyValuePair<Value, Value> kv in a.Entries)
                if (!b.Entries.TryGetValue(kv.Key, out Value bv) || !c.L.Equals(kv.Value, bv)) return c.Return(false);
            return c.Return(true);
        });
        L.Register(mt, "__tostring", static (ref CallContext c) =>
        {
            LuaDict d = CheckDict(ref c, 0);
            var sb = new StringBuilder("{");
            int i = 0;
            foreach (KeyValuePair<Value, Value> kv in d.Entries)
            {
                if (i++ > 0) sb.Append(", ");
                sb.Append(c.L.ToStringMeta(kv.Key)).Append(" = ").Append(c.L.ToStringMeta(kv.Value));
                if (sb.Length > 4096) { sb.Append(", ..."); break; }
            }
            sb.Append('}');
            return c.Return(sb.ToString());
        });

        L.Globals.SetStr("Dict", lib);
        L.LoadedModules.SetStr("Dict", lib);
    }

    // ------------------------------------------------------------------ parallel.map (chunked deterministic worker pool)

    private static void OpenParallel(LuaState L)
    {
        var lib = new Table();
        L.Register(lib, "map", static (ref CallContext c) =>
        {
            IReadOnlyList<Value> items = ExtractItems(ref c, 0);
            Value fn = c.CheckFunction(1);
            LuaList result = ParallelMapList(c.L, items, fn);
            return c.Return(Value.FromUserdata(result));
        });
        L.Globals.SetStr("parallel", lib);
        L.LoadedModules.SetStr("parallel", lib);
    }

    private static IReadOnlyList<Value> ExtractItems(ref CallContext c, int argIndex)
    {
        Value src = c[argIndex];
        if (src.O is LuaList list) return list.Items;
        if (src.O is FloatArray fa)
        {
            var arr = new Value[fa.Data.Length];
            for (int i = 0; i < fa.Data.Length; i++) arr[i] = Value.FromFloat(fa.Data[i]);
            return arr;
        }
        Table t = c.CheckTable(argIndex);
        int n = t.Length;
        var items = new Value[n];
        for (int i = 0; i < n; i++) items[i] = t.GetInt(i + 1);
        return items;
    }

    private sealed class ChunkWork
    {
        public required LuaState Worker;
        public required Value Fn;
        public required Value[] Inputs;
        public required int Offset;
        public Value[]? Outputs;
        public Value? ErrorValue;
        public AbortReason? Aborted;
        public string? Crash;
    }

    /// <summary>
    /// Runs <paramref name="fn"/> over <paramref name="items"/> across at most
    /// <see cref="VmLimits.MaxParallelWorkers"/> isolated worker VMs, returning a deterministic-order
    /// <see cref="LuaList"/> and inheriting all parent safety policies and budgets.
    /// </summary>
    internal static LuaList ParallelMapList(LuaState L, IReadOnlyList<Value> items, Value fn)
    {
        int n = items.Count;
        if (n == 0) return new LuaList(L, 0);

        int chunkCount = Math.Min(n, Math.Max(1, L.Limits.MaxParallelWorkers));
        var chunks = new ChunkWork[chunkCount];
        int created = 0;
        try
        {
            for (int ci = 0; ci < chunkCount; ci++)
            {
                int start = (int)((long)ci * n / chunkCount);
                int end = (int)((long)(ci + 1) * n / chunkCount);
                int len = end - start;
                LuaState W = L.NewWorker(isolated: L.IsSandboxed || L.IsHermetic);
                created++;
                var send = new ValueTransfer(L, W);
                if (!send.TryCopy(fn, out Value wfn, out string? fnErr))
                    throw new LuaException("parallel.map: function: " + fnErr);
                var winputs = new Value[len];
                for (int j = 0; j < len; j++)
                {
                    if (!send.TryCopy(items[start + j], out winputs[j], out string? itemErr))
                        throw new LuaException($"parallel.map: item {start + j + 1}: {itemErr}");
                }
                chunks[ci] = new ChunkWork { Worker = W, Fn = wfn, Inputs = winputs, Offset = start };
            }

            // Execute each chunk on its own worker thread via the parent's worker limiter.
            using var done = new CountdownEvent(chunkCount);
            for (int ci = 0; ci < chunkCount; ci++)
            {
                ChunkWork work = chunks[ci];
                L.StartWorker(work.Worker, () =>
                {
                    try
                    {
                        var outs = new Value[work.Inputs.Length];
                        for (int j = 0; j < work.Inputs.Length; j++)
                        {
                            if (work.Worker.PollStep()) work.Worker.CheckAbortOrStep();
                            outs[j] = work.Worker.Call1(work.Fn, work.Inputs[j]);
                        }
                        work.Outputs = outs;
                    }
                    catch (LuaAbortException e)
                    {
                        work.Aborted = e.Reason;
                        foreach (ChunkWork sibling in chunks) sibling?.Worker.RequestAbort(e.Reason);
                    }
                    catch (LuaException e)
                    {
                        work.ErrorValue = e.Value;
                        foreach (ChunkWork sibling in chunks) sibling?.Worker.RequestAbort(AbortReason.Cancelled);
                    }
                    catch (LuaExitException)
                    {
                        work.Crash = "os.exit was called in parallel.map";
                        foreach (ChunkWork sibling in chunks) sibling?.Worker.RequestAbort(AbortReason.Cancelled);
                    }
                    catch (Exception e)
                    {
                        work.Crash = LuaText.Encode(e.Message);
                        foreach (ChunkWork sibling in chunks) sibling?.Worker.RequestAbort(AbortReason.Cancelled);
                    }
                    finally
                    {
                        done.Signal();
                    }
                });
            }

            // Wait while remaining responsive to parent cancellation/watchdog aborts.
            while (!done.Wait(10))
            {
                if (L.AbortRequested)
                {
                    foreach (ChunkWork w in chunks) w.Worker.RequestAbort(AbortReason.Cancelled);
                    done.Wait();
                    L.CheckAbort();
                }
            }

            // Check outcomes in deterministic chunk order.
            foreach (ChunkWork w in chunks)
            {
                if (w.Aborted is { } reason) throw new LuaAbortException(reason);
                if (w.ErrorValue is { } errVal)
                {
                    var recv = new ValueTransfer(w.Worker, L);
                    Value copy = recv.TryCopy(errVal, out Value cv, out _) ? cv : Value.FromString(w.Worker.ToStringMeta(errVal));
                    throw new LuaException(copy);
                }
                if (w.Crash != null) throw new LuaException("parallel.map: " + w.Crash);
            }

            var result = new List<Value>(n);
            for (int ci = 0; ci < chunkCount; ci++)
            {
                ChunkWork w = chunks[ci];
                var recv = new ValueTransfer(w.Worker, L);
                Value[] outs = w.Outputs!;
                for (int j = 0; j < outs.Length; j++)
                {
                    if (!recv.TryCopy(outs[j], out Value rv, out string? rErr))
                        throw new LuaException($"parallel.map: result {w.Offset + j + 1}: {rErr}");
                    result.Add(rv);
                }
            }
            return new LuaList(L, result);
        }
        finally
        {
            for (int i = 0; i < created; i++)
                if (chunks[i]?.Worker is { } w) L.WorkerFinished(w);
        }
    }

    // ------------------------------------------------------------------ Cameo collection iterators

    // __cameo_iter1(iterable): 1-variable `for x in iterable` — yields values for List/Array/FloatArray/Query, keys for Dict/Map, or passes through a function.
    private static int Iter1(ref CallContext c)
    {
        Value src = c[0];
        if (src.Kind == ValueKind.Function)
            return c.Return(src, c.Count > 1 ? c[1] : Value.Nil, c.Count > 2 ? c[2] : Value.Nil);
        if (src.O is LuaList list)
        {
            int idx = 0;
            Value iter = Value.FromFunction((ref CallContext ic) =>
                idx < list.Items.Count ? ic.Return(list.Items[idx++]) : ic.Return(Value.Nil), "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        if (src.O is LuaDict dict)
        {
            var keys = new Value[dict.Entries.Count];
            int k = 0;
            foreach (KeyValuePair<Value, Value> kv in dict.Entries) keys[k++] = kv.Key;
            int idx = 0;
            Value iter = Value.FromFunction((ref CallContext ic) =>
                idx < keys.Length ? ic.Return(keys[idx++]) : ic.Return(Value.Nil), "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        if (src.O is FloatArray fa)
        {
            int idx = 0;
            Value iter = Value.FromFunction((ref CallContext ic) =>
                idx < fa.Data.Length ? ic.Return(Value.FromFloat(fa.Data[idx++])) : ic.Return(Value.Nil), "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        if (src.O is LuaQuery q)
        {
            Table items = q.Items;
            int len = items.Length;
            int idx = 1;
            Value iter = Value.FromFunction((ref CallContext ic) =>
                idx <= len ? ic.Return(items.GetInt(idx++)) : ic.Return(Value.Nil), "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        if (src.Kind == ValueKind.Table)
        {
            Table t = src.AsTable;
            int len = t.Length;
            if (len > 0)
            {
                int idx = 1;
                Value iter = Value.FromFunction((ref CallContext ic) =>
                {
                    if (idx > len) return ic.Return(Value.Nil);
                    Value v = t.GetInt(idx++);
                    return v.IsNil ? ic.Return(Value.Nil) : ic.Return(v);
                }, "cameo_iter");
                return c.Return(iter, Value.Nil, Value.Nil);
            }
            // Empty or map table: iterate keys
            Value cursor = Value.Nil;
            Value iterMap = Value.FromFunction((ref CallContext ic) =>
            {
                if (!t.Next(cursor, out Value nk, out _)) return ic.Return(Value.Nil);
                cursor = nk;
                return ic.Return(nk);
            }, "cameo_iter");
            return c.Return(iterMap, Value.Nil, Value.Nil);
        }
        throw new LuaException($"cannot iterate over {LuaState.TypeNameOf(src)}");
    }

    // __cameo_iter2(iterable): 2-variable `for k, v in iterable` — yields (i, v) for List/Array/FloatArray/Query, (k, v) for Dict/Table, or passes through a function.
    private static int Iter2(ref CallContext c)
    {
        Value src = c[0];
        if (src.Kind == ValueKind.Function)
            return c.Return(src, c.Count > 1 ? c[1] : Value.Nil, c.Count > 2 ? c[2] : Value.Nil);
        if (src.O is LuaList list)
        {
            int idx = 0;
            Value iter = Value.FromFunction((ref CallContext ic) =>
            {
                if (idx >= list.Items.Count) return ic.Return(Value.Nil);
                int cur = idx++;
                return ic.Return(Value.FromInt(cur + 1), list.Items[cur]);
            }, "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        if (src.O is LuaDict dict)
        {
            var snap = new KeyValuePair<Value, Value>[dict.Entries.Count];
            int k = 0;
            foreach (KeyValuePair<Value, Value> kv in dict.Entries) snap[k++] = kv;
            int idx = 0;
            Value iter = Value.FromFunction((ref CallContext ic) =>
            {
                if (idx >= snap.Length) return ic.Return(Value.Nil);
                var kv = snap[idx++];
                return ic.Return(kv.Key, kv.Value);
            }, "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        if (src.O is FloatArray fa)
        {
            int idx = 0;
            Value iter = Value.FromFunction((ref CallContext ic) =>
            {
                if (idx >= fa.Data.Length) return ic.Return(Value.Nil);
                int cur = idx++;
                return ic.Return(Value.FromInt(cur + 1), Value.FromFloat(fa.Data[cur]));
            }, "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        if (src.O is LuaQuery q)
        {
            Table items = q.Items;
            int len = items.Length;
            int idx = 1;
            Value iter = Value.FromFunction((ref CallContext ic) =>
            {
                if (idx > len) return ic.Return(Value.Nil);
                int cur = idx++;
                return ic.Return(Value.FromInt(cur), items.GetInt(cur));
            }, "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        if (src.Kind == ValueKind.Table)
        {
            Table t = src.AsTable;
            Value cursor = Value.Nil;
            Value iter = Value.FromFunction((ref CallContext ic) =>
            {
                if (!t.Next(cursor, out Value nk, out Value nv)) return ic.Return(Value.Nil);
                cursor = nk;
                return ic.Return(nk, nv);
            }, "cameo_iter");
            return c.Return(iter, Value.Nil, Value.Nil);
        }
        throw new LuaException($"cannot iterate over {LuaState.TypeNameOf(src)}");
    }

    // Stable merge sort matching TableLib semantics.
    private static void MergeSort(LuaState L, Value[] a, Value comp)
    {
        if (a.Length <= 1) return;
        var tmp = new Value[a.Length];
        SortRange(L, a, tmp, 0, a.Length, comp);
    }

    private static void SortRange(LuaState L, Value[] a, Value[] tmp, int lo, int hi, Value comp)
    {
        if (hi - lo <= 1) return;
        int mid = lo + ((hi - lo) >> 1);
        SortRange(L, a, tmp, lo, mid, comp);
        SortRange(L, a, tmp, mid, hi, comp);
        int i = lo, j = mid, k = lo;
        while (i < mid && j < hi)
        {
            bool rightLess = comp.IsNil ? L.LessThan(a[j], a[i]) : L.Call1(comp, a[j], a[i]).IsTruthy;
            tmp[k++] = rightLess ? a[j++] : a[i++];
        }
        while (i < mid) tmp[k++] = a[i++];
        while (j < hi) tmp[k++] = a[j++];
        Array.Copy(tmp, lo, a, lo, hi - lo);
    }
}
