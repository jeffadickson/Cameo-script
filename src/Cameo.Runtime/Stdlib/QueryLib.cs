using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

/// <summary>A sequence being queried (userdata): an array of items that query operators transform.</summary>
public sealed class LuaQuery : IUserdata
{
    public readonly Table Items;
    private readonly LuaState _state;
    public LuaQuery(LuaState state, Table items) { _state = state; Items = items; }
    public string TypeName => "Query";
    public Table? Metatable { get => _state.QueryMeta; set { } }
}

/// <summary>
/// LINQ-style operations on sequences, in two forms: plain functions added to <c>table</c>
/// (<c>table.sortby(t, key)</c>) and the chainable <c>Query</c>
/// (<c>Query.from(t):where(f):select(g):orderBy(k):toList()</c>). Both are eager: each step
/// builds its result at once, and none changes the sequence it was given.
/// </summary>
public static class QueryLib
{
    public static void Open(LuaState L)
    {
        Table t = L.Globals.GetStr("table").AsTable;
        L.Register(t, "sortby", static (ref CallContext c) => Out(ref c, SortBy(c.L, c.CheckTable(0), c.CheckFunction(1), c[2].IsTruthy)));
        L.Register(t, "groupby", static (ref CallContext c) => Out(ref c, GroupBy(c.L, c.CheckTable(0), c.CheckFunction(1), asGroups: false)));
        L.Register(t, "distinct", static (ref CallContext c) => Out(ref c, Distinct(c.L, c.CheckTable(0), c.IsNoneOrNil(1) ? default : c.CheckFunction(1))));
        L.Register(t, "partition", static (ref CallContext c) =>
        {
            Table src = c.CheckTable(0);
            Value fn = c.CheckFunction(1);
            Table yes = new(), no = new();
            for (int i = 1, n = src.Length; i <= n; i++)
            {
                Value v = src.GetInt(i);
                (c.L.Call1(fn, v, i).IsTruthy ? yes : no).Append(v);
            }
            return c.Return(Value.FromTable(yes), Value.FromTable(no));
        });
        L.Register(t, "flatmap", static (ref CallContext c) => Out(ref c, FlatMap(c.L, c.CheckTable(0), c.CheckFunction(1))));
        L.Register(t, "zip", static (ref CallContext c) =>
        {
            Table a = c.CheckTable(0), b = c.CheckTable(1);
            int n = Math.Min(a.Length, b.Length);
            var dst = new Table(n, 0);
            for (int i = 1; i <= n; i++)
            {
                var pair = new Table(2, 0);
                pair.Append(a.GetInt(i));
                pair.Append(b.GetInt(i));
                dst.Append(Value.FromTable(pair));
            }
            return Out(ref c, dst);
        });
        L.Register(t, "take", static (ref CallContext c) => Out(ref c, Take(c.CheckTable(0), c.CheckInt(1))));
        L.Register(t, "skip", static (ref CallContext c) => Out(ref c, Skip(c.CheckTable(0), c.CheckInt(1))));
        L.Register(t, "first", static (ref CallContext c) => c.Return(First(c.L, c.CheckTable(0), Pred(ref c, 1), last: false)));
        L.Register(t, "last", static (ref CallContext c) => c.Return(First(c.L, c.CheckTable(0), Pred(ref c, 1), last: true)));
        L.Register(t, "any", static (ref CallContext c) => c.Return(Count(c.L, c.CheckTable(0), Pred(ref c, 1), stopAtFirst: true) > 0));
        L.Register(t, "all", static (ref CallContext c) => c.Return(All(c.L, c.CheckTable(0), c.CheckFunction(1))));
        L.Register(t, "count", static (ref CallContext c) => c.Return(Count(c.L, c.CheckTable(0), Pred(ref c, 1), stopAtFirst: false)));
        L.Register(t, "sumby", static (ref CallContext c) => c.Return(Sum(c.L, c.CheckTable(0), c.CheckFunction(1))));
        L.Register(t, "minby", static (ref CallContext c) => c.Return(Extreme(c.L, c.CheckTable(0), c.CheckFunction(1), min: true)));
        L.Register(t, "maxby", static (ref CallContext c) => c.Return(Extreme(c.L, c.CheckTable(0), c.CheckFunction(1), min: false)));

        var q = new Table();
        L.Register(q, "from", static (ref CallContext c) =>
        {
            if (c[0].O is LuaQuery existing) return Q(ref c, existing.Items);
            if (c[0].O is LuaList list)
            {
                var copy = new Table(list.Items.Count, 0);
                for (int i = 0; i < list.Items.Count; i++) copy.Append(list.Items[i]);
                return Q(ref c, copy);
            }
            Table src = c.CheckTable(0);
            return Q(ref c, Take(src, src.Length)); // a copy: later changes to the table do not reach the query
        });
        // range(first, last [, step]): the integers from first to last
        L.Register(q, "range", static (ref CallContext c) =>
        {
            long first = c.CheckInt(0), last = c.CheckInt(1), step = c.OptInt(2, 1);
            if (step == 0) throw c.ArgError(2, "step must not be zero");
            var dst = new Table();
            for (long i = first; step > 0 ? i <= last : i >= last; i += step)
            {
                dst.Append(i);
                c.L.CheckArrayLength(dst.Length);
                if (step > 0 ? i > long.MaxValue - step : i < long.MinValue - step) break;
            }
            return Q(ref c, dst);
        });

        L.RegisterInstance<LuaQuery>(q, "where", static (ref CallContext c) =>
        {
            Table src = Self(ref c);
            Value fn = c.CheckFunction(1);
            var dst = new Table();
            for (int i = 1, n = src.Length; i <= n; i++)
            {
                Value v = src.GetInt(i);
                if (c.L.Call1(fn, v, i).IsTruthy) dst.Append(v);
            }
            return Q(ref c, dst);
        });
        L.RegisterInstance<LuaQuery>(q, "select", static (ref CallContext c) =>
        {
            Table src = Self(ref c);
            Value fn = c.CheckFunction(1);
            int n = src.Length;
            var dst = new Table(n, 0);
            for (int i = 1; i <= n; i++)
            {
                Value v = c.L.Call1(fn, src.GetInt(i), i);
                if (v.IsNil) throw new LuaException($"Query.select: the function returned nil for item {i} (a sequence cannot hold nil; use where to drop items)");
                dst.Append(v);
            }
            return Q(ref c, dst);
        });
        L.RegisterInstance<LuaQuery>(q, "flatMap", static (ref CallContext c) => Q(ref c, FlatMap(c.L, Self(ref c), c.CheckFunction(1))));
        L.RegisterInstance<LuaQuery>(q, "orderBy", static (ref CallContext c) => Q(ref c, SortBy(c.L, Self(ref c), c.CheckFunction(1), descending: false)));
        L.RegisterInstance<LuaQuery>(q, "orderByDescending", static (ref CallContext c) => Q(ref c, SortBy(c.L, Self(ref c), c.CheckFunction(1), descending: true)));
        L.RegisterInstance<LuaQuery>(q, "distinct", static (ref CallContext c) => Q(ref c, Distinct(c.L, Self(ref c), c.IsNoneOrNil(1) ? default : c.CheckFunction(1))));
        L.RegisterInstance<LuaQuery>(q, "groupBy", static (ref CallContext c) => Q(ref c, GroupBy(c.L, Self(ref c), c.CheckFunction(1), asGroups: true)));
        L.RegisterInstance<LuaQuery>(q, "take", static (ref CallContext c) => Q(ref c, Take(Self(ref c), c.CheckInt(1))));
        L.RegisterInstance<LuaQuery>(q, "skip", static (ref CallContext c) => Q(ref c, Skip(Self(ref c), c.CheckInt(1))));
        L.RegisterInstance<LuaQuery>(q, "reverse", static (ref CallContext c) =>
        {
            Table src = Self(ref c);
            int n = src.Length;
            var dst = new Table(n, 0);
            for (int i = n; i >= 1; i--) dst.Append(src.GetInt(i));
            return Q(ref c, dst);
        });
        L.RegisterInstance<LuaQuery>(q, "concat", static (ref CallContext c) =>
        {
            Table a = Self(ref c);
            Table b = c[1].O is LuaQuery other ? other.Items : c.CheckTable(1);
            var dst = Take(a, a.Length);
            for (int i = 1, n = b.Length; i <= n; i++) dst.Append(b.GetInt(i));
            return Q(ref c, dst);
        });

        L.RegisterInstance<LuaQuery>(q, "toList", static (ref CallContext c) => { Table s = Self(ref c); return Out(ref c, Take(s, s.Length)); });
        // toMap(key [, value]): a table from key(item) to item (or to value(item)); later items win
        L.RegisterInstance<LuaQuery>(q, "toMap", static (ref CallContext c) =>
        {
            Table src = Self(ref c);
            Value key = c.CheckFunction(1);
            Value val = c.IsNoneOrNil(2) ? default : c.CheckFunction(2);
            var dst = new Table();
            for (int i = 1, n = src.Length; i <= n; i++)
            {
                Value item = src.GetInt(i);
                Value k = c.L.Call1(key, item, i);
                if (k.IsNil) throw new LuaException($"Query.toMap: the key function returned nil for item {i}");
                dst.Set(k, val.IsNil ? item : c.L.Call1(val, item, i));
            }
            return Out(ref c, dst);
        });
        L.RegisterInstance<LuaQuery>(q, "first", static (ref CallContext c) => c.Return(First(c.L, Self(ref c), Pred(ref c, 1), last: false)));
        L.RegisterInstance<LuaQuery>(q, "last", static (ref CallContext c) => c.Return(First(c.L, Self(ref c), Pred(ref c, 1), last: true)));
        L.RegisterInstance<LuaQuery>(q, "any", static (ref CallContext c) => c.Return(Count(c.L, Self(ref c), Pred(ref c, 1), stopAtFirst: true) > 0));
        L.RegisterInstance<LuaQuery>(q, "all", static (ref CallContext c) => c.Return(All(c.L, Self(ref c), c.CheckFunction(1))));
        L.RegisterInstance<LuaQuery>(q, "count", static (ref CallContext c) => c.Return(Count(c.L, Self(ref c), Pred(ref c, 1), stopAtFirst: false)));
        L.RegisterInstance<LuaQuery>(q, "sum", static (ref CallContext c) => c.Return(Sum(c.L, Self(ref c), Pred(ref c, 1))));
        L.RegisterInstance<LuaQuery>(q, "min", static (ref CallContext c) => c.Return(Extreme(c.L, Self(ref c), Pred(ref c, 1), min: true)));
        L.RegisterInstance<LuaQuery>(q, "max", static (ref CallContext c) => c.Return(Extreme(c.L, Self(ref c), Pred(ref c, 1), min: false)));
        L.RegisterInstance<LuaQuery>(q, "contains", static (ref CallContext c) =>
        {
            Table src = Self(ref c);
            for (int i = 1, n = src.Length; i <= n; i++)
                if (c.L.Equals(src.GetInt(i), c[1])) return c.Return(true);
            return c.Return(false);
        });
        // aggregate(fn, initial): fn(accumulator, item) over the items, left to right
        L.RegisterInstance<LuaQuery>(q, "aggregate", static (ref CallContext c) =>
        {
            Table src = Self(ref c);
            Value fn = c.CheckFunction(1);
            Value acc = c[2];
            for (int i = 1, n = src.Length; i <= n; i++) acc = c.L.Call1(fn, acc, src.GetInt(i), i);
            return c.Return(acc);
        });
        L.RegisterInstance<LuaQuery>(q, "forEach", static (ref CallContext c) =>
        {
            Table src = Self(ref c);
            Value fn = c.CheckFunction(1);
            for (int i = 1, n = src.Length; i <= n; i++) c.L.Call1(fn, src.GetInt(i), i);
            return c.Return();
        });

        Table mt = L.QueryMeta;
        mt.SetStr("__index", q);
        mt.SetStr("__name", "Query");
        L.Register(mt, "__len", static (ref CallContext c) => c.Return(Self(ref c).Length));
        L.Register(mt, "__tostring", static (ref CallContext c) => c.Return($"Query({Self(ref c).Length} items)"));
        L.Globals.SetStr("Query", q);
        L.LoadedModules.SetStr("Query", q);
    }

    // ------------------------------------------------------------------ plumbing

    private static Table Self(ref CallContext c) => (c[0].O as LuaQuery ?? throw c.TypeError(0, "Query")).Items;
    private static int Q(ref CallContext c, Table items) => c.Return(Value.FromUserdata(new LuaQuery(c.L, items)));
    private static int Out(ref CallContext c, Table t) => c.Return(Value.FromTable(t));
    private static Value Pred(ref CallContext c, int arg) => c.IsNoneOrNil(arg) ? default : c.CheckFunction(arg);

    // ------------------------------------------------------------------ operators

    private static Table Take(Table src, long count)
    {
        int n = (int)Math.Clamp(count, 0, src.Length);
        var dst = new Table(n, 0);
        for (int i = 1; i <= n; i++) dst.Append(src.GetInt(i));
        return dst;
    }

    private static Table Skip(Table src, long count)
    {
        int n = src.Length;
        var dst = new Table();
        for (long i = Math.Max(count, 0) + 1; i <= n; i++) dst.Append(src.GetInt(i));
        return dst;
    }

    private static Table FlatMap(LuaState L, Table src, Value fn)
    {
        var dst = new Table();
        for (int i = 1, n = src.Length; i <= n; i++)
        {
            Value inner = L.Call1(fn, src.GetInt(i), i);
            if (inner.IsNil) continue; // nothing for this item
            Table t = inner.O is LuaQuery q ? q.Items : inner.O as Table
                ?? throw new LuaException($"flatMap: the function returned a {LuaState.TypeNameOf(inner)} for item {i}, not a list");
            for (int j = 1, m = t.Length; j <= m; j++) dst.Append(t.GetInt(j));
        }
        return dst;
    }

    // A stable sort by key(item): items with equal keys keep their order. Keys are compared
    // with <, so they must all be numbers or all be strings (or have __lt).
    private static Table SortBy(LuaState L, Table src, Value keyFn, bool descending)
    {
        int n = src.Length;
        var keys = new Value[n];
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            keys[i] = L.Call1(keyFn, src.GetInt(i + 1), i + 1);
            order[i] = i;
        }
        var scratch = new int[n];
        MergeSort(L, keys, order, scratch, 0, n, descending);
        var dst = new Table(n, 0);
        foreach (int i in order) dst.Append(src.GetInt(i + 1));
        return dst;
    }

    private static void MergeSort(LuaState L, Value[] keys, int[] order, int[] scratch, int from, int to, bool descending)
    {
        if (to - from < 2) return;
        int mid = (from + to) >>> 1;
        MergeSort(L, keys, order, scratch, from, mid, descending);
        MergeSort(L, keys, order, scratch, mid, to, descending);
        int a = from, b = mid, k = from;
        while (a < mid && b < to)
        {
            // take from the right half only when it is strictly before the left: that keeps ties in order
            bool rightFirst = descending ? L.LessThan(keys[order[a]], keys[order[b]]) : L.LessThan(keys[order[b]], keys[order[a]]);
            scratch[k++] = rightFirst ? order[b++] : order[a++];
        }
        while (a < mid) scratch[k++] = order[a++];
        while (b < to) scratch[k++] = order[b++];
        Array.Copy(scratch, from, order, from, to - from);
    }

    // Items with a key not seen before, in order of first appearance.
    private static Table Distinct(LuaState L, Table src, Value keyFn)
    {
        var seen = new Table();
        var dst = new Table();
        for (int i = 1, n = src.Length; i <= n; i++)
        {
            Value item = src.GetInt(i);
            Value key = keyFn.IsNil ? item : L.Call1(keyFn, item, i);
            if (key.IsNil || (key.IsFloat && double.IsNaN(key.N))) { dst.Append(item); continue; } // cannot be table keys: never equal to anything
            if (!seen.Get(key).IsNil) continue;
            seen.Set(key, Value.True);
            dst.Append(item);
        }
        return dst;
    }

    // Groups in order of first appearance. For `table.groupby`: a table from key to list of
    // items. For `Query.groupBy`: a list of {key=, items=} records, so that it can be queried on.
    private static Table GroupBy(LuaState L, Table src, Value keyFn, bool asGroups)
    {
        var byKey = new Table();
        var groups = new Table();
        for (int i = 1, n = src.Length; i <= n; i++)
        {
            Value item = src.GetInt(i);
            Value key = L.Call1(keyFn, item, i);
            if (key.IsNil || (key.IsFloat && double.IsNaN(key.N))) throw new LuaException($"groupBy: the key function returned {key} for item {i}");
            Value list = byKey.Get(key);
            if (list.IsNil)
            {
                list = Value.FromTable(new Table());
                byKey.Set(key, list);
                if (asGroups)
                {
                    var g = new Table(0, 2);
                    g.SetStr("key", key);
                    g.SetStr("items", list);
                    groups.Append(Value.FromTable(g));
                }
            }
            list.AsTable.Append(item);
        }
        return asGroups ? groups : byKey;
    }

    private static Value First(LuaState L, Table src, Value pred, bool last)
    {
        int n = src.Length;
        for (int k = 0; k < n; k++)
        {
            int i = last ? n - k : k + 1;
            Value v = src.GetInt(i);
            if (pred.IsNil || L.Call1(pred, v, i).IsTruthy) return v;
        }
        return default;
    }

    private static long Count(LuaState L, Table src, Value pred, bool stopAtFirst)
    {
        int n = src.Length;
        if (pred.IsNil) return n;
        long count = 0;
        for (int i = 1; i <= n; i++)
        {
            if (!L.Call1(pred, src.GetInt(i), i).IsTruthy) continue;
            count++;
            if (stopAtFirst) break;
        }
        return count;
    }

    private static bool All(LuaState L, Table src, Value pred)
    {
        for (int i = 1, n = src.Length; i <= n; i++)
            if (!L.Call1(pred, src.GetInt(i), i).IsTruthy) return false;
        return true;
    }

    // Sum of fn(item) (or of the items): an integer while everything is an integer, like `+`.
    private static Value Sum(LuaState L, Table src, Value fn)
    {
        Value acc = Value.FromInt(0);
        for (int i = 1, n = src.Length; i <= n; i++)
        {
            Value v = src.GetInt(i);
            acc = L.Arith(OpCode.Add, acc, fn.IsNil ? v : L.Call1(fn, v, i));
        }
        return acc;
    }

    // The item whose fn(item) (or the item itself) is smallest/largest; the first one on a tie; nil when empty.
    private static Value Extreme(LuaState L, Table src, Value fn, bool min)
    {
        Value best = default, bestKey = default;
        for (int i = 1, n = src.Length; i <= n; i++)
        {
            Value v = src.GetInt(i);
            Value key = fn.IsNil ? v : L.Call1(fn, v, i);
            if (i == 1 || (min ? L.LessThan(key, bestKey) : L.LessThan(bestKey, key))) { best = v; bestKey = key; }
        }
        return best;
    }
}
