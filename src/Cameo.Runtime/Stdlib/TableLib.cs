using System.Text;
using Cameo.Runtime.Vm;

namespace Cameo.Runtime.Stdlib;

public static class TableLib
{
    public static void Open(LuaState L)
    {
        var t = new Table();
        L.Register(t, "insert", Insert);
        L.Register(t, "remove", Remove);
        L.Register(t, "concat", Concat);
        L.Register(t, "unpack", Unpack);
        L.Register(t, "pack", Pack);
        L.Register(t, "sort", Sort);
        L.Register(t, "move", Move);
        L.Globals.SetStr("table", t);
        L.LoadedModules.SetStr("table", t);
    }

    // Length honouring __len.
    private static long LenOf(ref CallContext c, int i)
    {
        Table t = c.CheckTable(i);
        if (t.Metatable == null) return t.Length;
        Value n = c.L.Len(c[i]);
        if (n.Kind != ValueKind.Integer) throw new LuaException("object length is not an integer");
        return n.I;
    }

    private static Value GetI(LuaState L, in Value tv, Table t, long i) =>
        t.Metatable == null ? t.GetInt(i) : L.Index(tv, Value.FromInt(i));

    private static void SetI(LuaState L, in Value tv, Table t, long i, in Value v)
    {
        if (t.Metatable == null) t.SetInt(i, v);
        else L.SetIndex(tv, Value.FromInt(i), v);
    }

    private static int Insert(ref CallContext c)
    {
        Table t = c.CheckTable(0);
        Value tv = c[0];
        long e = LenOf(ref c, 0) + 1;
        switch (c.Count)
        {
            case 2:
                SetI(c.L, tv, t, e, c[1]);
                return 0;
            case 3:
                {
                    long pos = c.CheckInt(1);
                    // unsigned comparison as in Lua: 1 <= pos <= e
                    if ((ulong)(pos - 1) >= (ulong)e) throw c.ArgError(1, "position out of bounds");
                    Value v = c[2];
                    if (t.Metatable == null)
                    {
                        t.Insert(pos, v);
                    }
                    else
                    {
                        for (long i = e; i > pos; i--) SetI(c.L, tv, t, i, GetI(c.L, tv, t, i - 1));
                        SetI(c.L, tv, t, pos, v);
                    }
                    return 0;
                }
            default:
                throw new LuaException("wrong number of arguments to 'insert'");
        }
    }

    private static int Remove(ref CallContext c)
    {
        Table t = c.CheckTable(0);
        Value tv = c[0];
        long size = LenOf(ref c, 0);
        long pos = c.OptInt(1, size);
        if (c.Count > 1 && !c[1].IsNil && pos != size)
        {
            // 1 <= pos <= size + 1
            if ((ulong)(pos - 1) >= (ulong)(size + 1)) throw c.ArgError(1, "position out of bounds");
        }
        if (t.Metatable == null)
        {
            if (pos >= 1 && pos <= size) return c.Return(t.RemoveAt(pos));
            Value r = t.GetInt(pos);
            t.SetInt(pos, Value.Nil);
            return c.Return(r);
        }
        Value res = GetI(c.L, tv, t, pos);
        for (; pos < size; pos++) SetI(c.L, tv, t, pos, GetI(c.L, tv, t, pos + 1));
        SetI(c.L, tv, t, pos, Value.Nil);
        return c.Return(res);
    }

    private static int Concat(ref CallContext c)
    {
        Table t = c.CheckTable(0);
        Value tv = c[0];
        string sep = c.OptString(1, "");
        long i = c.OptInt(2, 1);
        long last = c.IsNoneOrNil(3) ? LenOf(ref c, 0) : c.CheckInt(3);
        var sb = new StringBuilder();
        for (; i <= last; i++)
        {
            Value v = GetI(c.L, tv, t, i);
            if (v.IsString) sb.Append(v.AsString);
            else if (v.IsNumber) sb.Append(v.ToString());
            else throw new LuaException($"invalid value ({LuaState.TypeNameOf(v)}) at index {i} in table for 'concat'");
            if (i != last) sb.Append(sep);
            c.L.CheckStringLength(sb.Length);
            if (i == long.MaxValue) break;
        }
        return c.Return(sb.ToString());
    }

    private static int Unpack(ref CallContext c)
    {
        Value tv = c[0];
        long i = c.OptInt(1, 1);
        long e;
        if (c.IsNoneOrNil(2))
        {
            if (tv.IsTable) e = LenOf(ref c, 0);
            else
            {
                Value n = c.L.Len(tv);
                if (n.Kind != ValueKind.Integer) throw new LuaException("object length is not an integer");
                e = n.I;
            }
        }
        else e = c.CheckInt(2);
        if (i > e) return 0;
        ulong n64 = (ulong)e - (ulong)i;
        if (n64 >= 10_000_000) throw new LuaException("too many results to unpack");
        int cnt = (int)n64 + 1;
        var r = new Value[cnt];
        if (tv.IsTable && tv.AsTable.Metatable == null)
        {
            Table t = tv.AsTable;
            for (int k = 0; k < cnt; k++) r[k] = t.GetInt(i + k);
        }
        else
        {
            for (int k = 0; k < cnt; k++) r[k] = c.L.Index(tv, Value.FromInt(i + k));
        }
        return c.Return(r);
    }

    private static int Pack(ref CallContext c)
    {
        int n = c.Count;
        var t = new Table(n, 1);
        if (n > 0) t.SetRange(0, c.ArgsFrom(0));
        t.SetStr("n", Value.FromInt(n));
        return c.Return(Value.FromTable(t));
    }

    private static int Move(ref CallContext c)
    {
        Table a1 = c.CheckTable(0);
        Value a1v = c[0];
        long f = c.CheckInt(1);
        long e = c.CheckInt(2);
        long t = c.CheckInt(3);
        int tt = c.IsNoneOrNil(4) ? 0 : 4;
        Table a2 = c.CheckTable(tt);
        Value a2v = c[tt];
        if (e >= f)
        {
            if (!(f > 0 || e < long.MaxValue + f)) throw c.ArgError(2, "too many elements to move");
            long n = e - f + 1;
            if (t > long.MaxValue - n + 1) throw c.ArgError(3, "destination wrap around");
            LuaState L = c.L;
            if (t > e || t <= f || (tt != 0 && !ReferenceEquals(a1, a2)))
            {
                for (long i = 0; i < n; i++) SetI(L, a2v, a2, t + i, GetI(L, a1v, a1, f + i));
            }
            else
            {
                for (long i = n - 1; i >= 0; i--) SetI(L, a2v, a2, t + i, GetI(L, a1v, a1, f + i));
            }
        }
        return c.Return(a2v);
    }

    // ------------------------------------------------------------------ sort

    private static int Sort(ref CallContext c)
    {
        Table t = c.CheckTable(0);
        t.CheckMutable();
        Value tv = c[0];
        long n64 = LenOf(ref c, 0);
        if (n64 <= 1)
        {
            if (!c.IsNoneOrNil(1)) c.CheckFunction(1);
            return 0;
        }
        if (n64 >= int.MaxValue) throw c.ArgError(0, "array too big");
        Value comp = Value.Nil;
        if (!c.IsNoneOrNil(1)) comp = c.CheckFunction(1);
        int n = (int)n64;
        LuaState L = c.L;
        bool raw = t.Metatable == null;

        // Sort a private copy so that an error in the comparator leaves the table untouched.
        var a = new Value[n];
        if (raw && t.ArrayCount == n) t.ArraySpan.CopyTo(a);
        else for (int i = 0; i < n; i++) a[i] = GetI(L, tv, t, i + 1);

        MergeSort(L, a, comp);

        if (raw && t.ArrayCount == n && t.Length == n) a.CopyTo(t.ArraySpan);
        else for (int i = 0; i < n; i++) SetI(L, tv, t, i + 1, a[i]);
        return 0;
    }

    private static bool Less(LuaState L, in Value comp, in Value a, in Value b)
    {
        if (comp.IsNil)
        {
            if (a.Kind == ValueKind.Integer && b.Kind == ValueKind.Integer) return a.I < b.I;
            return L.LessThan(a, b);
        }
        return L.Call1(comp, a, b).IsTruthy;
    }

    // Bottom-up merge sort: O(n log n) comparisons, terminates for any comparator.
    private static void MergeSort(LuaState L, Value[] a, Value comp)
    {
        int n = a.Length;
        const int Run = 8;
        for (int lo = 0; lo < n; lo += Run)
        {
            int hi = Math.Min(lo + Run, n);
            for (int i = lo + 1; i < hi; i++)
            {
                Value x = a[i];
                int j = i - 1;
                while (j >= lo && Less(L, comp, x, a[j]))
                {
                    a[j + 1] = a[j];
                    j--;
                }
                a[j + 1] = x;
            }
        }
        if (n <= Run) return;
        var src = a;
        var dst = new Value[n];
        for (int width = Run; width < n; width *= 2)
        {
            for (int lo = 0; lo < n; lo += 2 * width)
            {
                int mid = Math.Min(lo + width, n);
                int hi = Math.Min(lo + 2 * width, n);
                int i = lo, j = mid, k = lo;
                if (mid >= hi || !Less(L, comp, src[mid], src[mid - 1]))
                {
                    Array.Copy(src, lo, dst, lo, hi - lo); // already in order
                    continue;
                }
                while (i < mid && j < hi)
                {
                    if (Less(L, comp, src[j], src[i])) dst[k++] = src[j++];
                    else dst[k++] = src[i++];
                }
                while (i < mid) dst[k++] = src[i++];
                while (j < hi) dst[k++] = src[j++];
            }
            (src, dst) = (dst, src);
        }
        if (!ReferenceEquals(src, a)) Array.Copy(src, a, n);
    }
}
