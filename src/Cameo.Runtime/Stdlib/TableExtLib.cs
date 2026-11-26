using System.Runtime.CompilerServices;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>
/// Table helpers added to <c>table</c>: map/filter/reduce over arrays, keys/values, searching,
/// slicing, copying and deep comparison. They are ordinary built-ins written in C#, like
/// <c>table.sort</c>; none of them changes its argument.
/// </summary>
public static class TableExtLib
{
    public static void Open(LuaState L)
    {
        Table t = L.Globals.GetStr("table").AsTable;
        L.Register(t, "map", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            Value fn = c.CheckFunction(1);
            int n = src.Length;
            var dst = new Table(n, 0);
            // results are written by index so that a nil result leaves a hole instead of shifting
            for (int i = 1; i <= n; i++) dst.SetInt(i, c.L.Call1(fn, src.GetInt(i), i));
            return c.Return(Value.FromTable(dst));
        });
        L.Register(t, "filter", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            Value fn = c.CheckFunction(1);
            int n = src.Length;
            var dst = new Table();
            for (int i = 1; i <= n; i++)
            {
                Value v = src.GetInt(i);
                if (c.L.Call1(fn, v, i).IsTruthy) dst.Append(v);
            }
            return c.Return(Value.FromTable(dst));
        });
        L.Register(t, "reduce", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            Value fn = c.CheckFunction(1);
            Value acc = c[2];
            int n = src.Length;
            for (int i = 1; i <= n; i++) acc = c.L.Call1(fn, acc, src.GetInt(i), i);
            return c.Return(acc);
        });
        L.Register(t, "find", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            Value fn = c.CheckFunction(1);
            int n = src.Length;
            for (int i = 1; i <= n; i++)
            {
                Value v = src.GetInt(i);
                if (c.L.Call1(fn, v, i).IsTruthy) return c.Return(v, i);
            }
            return c.Return(Value.Nil, Value.Nil);
        });
        L.Register(t, "keys", static (ref CallContext c) =>
        {
            var dst = new Table();
            foreach (KeyValuePair<Value, Value> kv in c.CheckTable(0).Pairs()) dst.Append(kv.Key);
            return c.Return(Value.FromTable(dst));
        });
        L.Register(t, "values", static (ref CallContext c) =>
        {
            var dst = new Table();
            foreach (KeyValuePair<Value, Value> kv in c.CheckTable(0).Pairs()) dst.Append(kv.Value);
            return c.Return(Value.FromTable(dst));
        });
        L.Register(t, "indexof", static (ref CallContext c) =>
        {
            long i = IndexOf(ref c);
            return c.Return(i == 0 ? Value.Nil : Value.FromInt(i));
        });
        L.Register(t, "contains", static (ref CallContext c) => c.Return(IndexOf(ref c) != 0));
        L.Register(t, "slice", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            long n = src.Length;
            long i = c.OptInt(1, 1), j = c.OptInt(2, -1);
            if (i < 0) i = Math.Max(n + i + 1, 1); else if (i == 0) i = 1;
            if (j < 0) j = n + j + 1; else if (j > n) j = n;
            var dst = new Table((int)Math.Max(j - i + 1, 0), 0);
            for (long k = i; k <= j; k++) dst.Append(src.GetInt(k));
            return c.Return(Value.FromTable(dst));
        });
        L.Register(t, "reverse", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            int n = src.Length;
            var dst = new Table(n, 0);
            for (int i = n; i >= 1; i--) dst.SetInt(n - i + 1, src.GetInt(i));
            return c.Return(Value.FromTable(dst));
        });
        L.Register(t, "copy", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            var dst = new Table(src.ArrayCount, 0) { Metatable = src.Metatable };
            foreach (KeyValuePair<Value, Value> kv in src.Pairs()) dst.Set(kv.Key, kv.Value);
            return c.Return(Value.FromTable(dst));
        });
        L.Register(t, "deepcopy", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            try
            {
                return c.Return(Value.FromTable(DeepCopy(src, new Dictionary<Table, Table>(ReferenceEqualityComparer.Instance))));
            }
            catch (InsufficientExecutionStackException)
            {
                throw c.ArgError(0, "table is too deeply nested");
            }
        });
        L.Register(t, "equals", static (ref CallContext c) =>
        {
            c.CheckAny(0);
            c.CheckAny(1);
            try
            {
                return c.Return(DeepEquals(c.L, c[0], c[1], new HashSet<(Table, Table)>()));
            }
            catch (InsufficientExecutionStackException)
            {
                throw c.ArgError(0, "table is too deeply nested");
            }
        });
    }

    // Position of the first element equal to the value (== semantics, so __eq applies); 0 if absent.
    private static long IndexOf(ref CallContext c)
    {
        Table src = c.CheckTable(0);
        c.CheckAny(1);
        Value v = c[1];
        int n = src.Length;
        for (int i = 1; i <= n; i++)
            if (c.L.Equals(src.GetInt(i), v)) return i;
        return 0;
    }

    // Copies tables all the way down, keeping shared and cyclic structure; keys that are tables
    // are copied too. Metatables are shared with the original (they describe behaviour, not data).
    private static Table DeepCopy(Table src, Dictionary<Table, Table> done)
    {
        if (done.TryGetValue(src, out Table? seen)) return seen;
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var dst = new Table(src.ArrayCount, 0) { Metatable = src.Metatable };
        done[src] = dst;
        foreach (KeyValuePair<Value, Value> kv in src.Pairs())
        {
            Value k = kv.Key.IsTable ? Value.FromTable(DeepCopy(kv.Key.AsTable, done)) : kv.Key;
            Value v = kv.Value.IsTable ? Value.FromTable(DeepCopy(kv.Value.AsTable, done)) : kv.Value;
            dst.Set(k, v);
        }
        return dst;
    }

    // Structural equality: tables are equal when they have the same keys with equal values
    // (keys compare by ==). A pair of tables with an __eq metamethod is compared by it. A pair
    // already being compared further up is taken as equal, which makes cyclic structures work.
    private static bool DeepEquals(LuaState L, in Value a, in Value b, HashSet<(Table, Table)> active)
    {
        if (!a.IsTable || !b.IsTable) return L.Equals(a, b);
        Table x = a.AsTable, y = b.AsTable;
        if (ReferenceEquals(x, y)) return true;
        if (!L.GetMetamethod(a, "__eq").IsNil || !L.GetMetamethod(b, "__eq").IsNil) return L.Equals(a, b);
        if (!active.Add((x, y))) return true;
        RuntimeHelpers.EnsureSufficientExecutionStack();
        int count = 0;
        foreach (KeyValuePair<Value, Value> kv in x.Pairs())
        {
            count++;
            Value other = y.Get(kv.Key);
            if (other.IsNil || !DeepEquals(L, kv.Value, other, active)) return false;
        }
        int countY = 0;
        foreach (KeyValuePair<Value, Value> _ in y.Pairs()) countY++;
        active.Remove((x, y));
        return count == countY;
    }
}
