using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>The table library against List&lt;T&gt; models (CsCheck): sort, insert/remove, concat, unpack, move.</summary>
public class TableLibProperties
{
    private readonly LuaHost _lua = new();

    private static Table ToTable(IEnumerable<Value> items)
    {
        var t = new Table();
        foreach (Value v in items) t.Append(v);
        return t;
    }

    private static List<Value> ToList(Table t)
    {
        var list = new List<Value>();
        for (int i = 1; i <= t.Length; i++) list.Add(t.GetInt(i));
        return list;
    }

    [Fact]
    public void Sort_orders_integers_and_is_a_permutation()
    {
        Prop.Cs(Gen.Long[-50, 50].Array[0, 60], xs =>
        {
            Table t = ToTable(xs.Select(x => Value.FromInt(x)));
            _lua.Call("table.sort", t);
            long[] expected = (long[])xs.Clone();
            Array.Sort(expected);
            Assert.Equal(expected, ToList(t).Select(v => v.I));
            Assert.Equal(xs.Length, t.Length);

            // descending with a comparison function written in the language
            Table d = ToTable(xs.Select(x => Value.FromInt(x)));
            _lua.Call("table.sort", d, _lua.Run("return function(a, b) return a > b end")[0]);
            Assert.Equal(expected.Reverse(), ToList(d).Select(v => v.I));

            // a native comparator on a derived key: result is ordered by the key and is a permutation
            Table k = ToTable(xs.Select(x => Value.FromInt(x)));
            Value byAbs = Value.FromFunction((ref CallContext c) => c.Return(Math.Abs(c[0].I) < Math.Abs(c[1].I)), "byAbs");
            _lua.Call("table.sort", k, byAbs);
            var sorted = ToList(k).Select(v => v.I).ToList();
            for (int i = 1; i < sorted.Count; i++) Assert.True(Math.Abs(sorted[i - 1]) <= Math.Abs(sorted[i]));
            Assert.Equal(expected, sorted.OrderBy(v => v));
        }, iter: 600);
    }

    [Fact]
    public void Sort_orders_mixed_numbers_and_strings_bytewise()
    {
        Prop.Cs(Gen.Select(Gen.Double[-100, 100].Array[0, 30], Gen.String[Gen.Char[(char)1, (char)255], 0, 5].Array[0, 30]), x =>
        {
            var (nums, strs) = x;
            // integers and floats sort together numerically
            var mixed = nums.Select((d, i) => i % 2 == 0 ? Value.FromInt((long)d) : Value.FromFloat(d)).ToList();
            Table t = ToTable(mixed);
            _lua.Call("table.sort", t);
            var got = ToList(t).Select(v => v.AsDouble).ToList();
            Assert.Equal(mixed.Select(v => v.AsDouble).OrderBy(d => d), got);

            Table s = ToTable(strs.Select(v => Value.FromString(v)));
            _lua.Call("table.sort", s);
            Assert.Equal(strs.OrderBy(v => v, StringComparer.Ordinal), ToList(s).Select(v => v.AsString));

            // numbers and strings together cannot be compared
            if (nums.Length > 0 && strs.Length > 0)
            {
                Table bad = ToTable(new[] { Value.FromFloat(nums[0]), Value.FromString(strs[0]) });
                Assert.Contains("attempt to compare", _lua.ErrorOf(() => _lua.Call("table.sort", bad)));
            }
        }, iter: 300);
    }

    private abstract record Op;
    private sealed record Push(long V) : Op;
    private sealed record InsertAt(int Pos, long V) : Op;
    private sealed record Pop : Op;
    private sealed record RemoveAt(int Pos) : Op;

    private static readonly Gen<Op> GenOp = Gen.Frequency(
        (4, Gen.Long[0, 99].Select(v => (Op)new Push(v))),
        (3, Gen.Select(Gen.Int[0, 1000], Gen.Long[0, 99], (p, v) => (Op)new InsertAt(p, v))),
        (2, Gen.Const((Op)new Pop())),
        (2, Gen.Int[0, 1000].Select(p => (Op)new RemoveAt(p))));

    [Fact]
    public void Insert_and_remove_behave_like_a_list()
    {
        Prop.Cs(Gen.Select(GenOp.Array[0, 40], Gen.Bool), x =>
        {
            var (ops, proxied) = x;
            var model = new List<long>();
            var t = new Table();
            // with a metatable present the library must go through the generic (slow) path
            if (proxied) t.Metatable = new Table();
            Value tv = t;
            foreach (Op op in ops)
            {
                switch (op)
                {
                    case Push p:
                        _lua.Call("table.insert", tv, p.V);
                        model.Add(p.V);
                        break;
                    case InsertAt i:
                        {
                            int pos = i.Pos % (model.Count + 1) + 1;
                            _lua.Call("table.insert", tv, pos, i.V);
                            model.Insert(pos - 1, i.V);
                            break;
                        }
                    case Pop:
                        {
                            Value r = _lua.Call1("table.remove", tv);
                            if (model.Count == 0) Assert.True(r.IsNil);
                            else
                            {
                                Assert.Equal(model[^1], r.I);
                                model.RemoveAt(model.Count - 1);
                            }
                            break;
                        }
                    case RemoveAt r:
                        {
                            if (model.Count == 0) break;
                            int pos = r.Pos % model.Count + 1;
                            Assert.Equal(model[pos - 1], _lua.Call1("table.remove", tv, pos).I);
                            model.RemoveAt(pos - 1);
                            break;
                        }
                }
                Assert.Equal(model.Count, _lua.L.Len(tv).I);
            }
            Assert.Equal(model, ToList(t).Select(v => v.I));
            // concat, unpack and pack agree with the model
            Assert.Equal(string.Join(",", model), _lua.Call1("table.concat", tv, ",").AsString);
            Assert.Equal(string.Join("", model), _lua.Call1("table.concat", tv).AsString);
            Assert.Equal(model, _lua.Call("table.unpack", tv).Select(v => v.I));
            Value packed = _lua.Call1("table.pack", model.Select(v => Value.FromInt(v)).ToArray());
            Assert.Equal(model.Count, packed.AsTable.GetStr("n").I);
            Assert.Equal(model, ToList(packed.AsTable).Select(v => v.I));
            // out-of-range positions are rejected
            Assert.Contains("position out of bounds", _lua.ErrorOf(() => _lua.Call("table.insert", tv, model.Count + 2, 0)));
            Assert.Contains("position out of bounds", _lua.ErrorOf(() => _lua.Call("table.insert", tv, 0, 0)));
        }, iter: 600);
    }

    [Fact]
    public void Unpack_ranges_and_move_copy_the_right_elements()
    {
        Prop.Cs(Gen.Select(Gen.Long[0, 99].Array[0, 12], Gen.Int[1, 14], Gen.Int[0, 14], Gen.Int[1, 14]), x =>
        {
            var (xs, i, j, dest) = x;
            Table t = ToTable(xs.Select(v => Value.FromInt(v)));
            // unpack(t, i, j) yields t[i..j], with nils beyond the end
            Value[] range = _lua.Call("table.unpack", t, i, j);
            Assert.Equal(Math.Max(0, j - i + 1), range.Length);
            for (int k = 0; k < range.Length; k++)
            {
                int idx = i + k;
                if (idx <= xs.Length) Assert.Equal(xs[idx - 1], range[k].I); else Assert.True(range[k].IsNil);
            }
            // move(t, i, j, dest, {}) copies the range to a new table at dest
            Value target = _lua.Call1("table.move", t, i, j, dest, new Table());
            for (int k = 0; k < Math.Max(0, j - i + 1); k++)
            {
                Value v = target.AsTable.GetInt(dest + k);
                int idx = i + k;
                if (idx <= xs.Length) Assert.Equal(xs[idx - 1], v.I); else Assert.True(v.IsNil);
            }
            // overlapping move within the same table behaves like a copy made first
            long[] copy = (long[])xs.Clone();
            if (xs.Length > 0)
            {
                int from = Math.Min(i, xs.Length), to = xs.Length;
                _lua.Call("table.move", t, from, to, from + 1);
                var expected = new List<long>(copy);
                expected.Add(0);
                for (int k = to; k >= from; k--) expected[k] = copy[k - 1];
                Assert.Equal(expected.Take(to + 1), ToList(t).Take(to + 1).Select(v => v.I));
            }
        }, iter: 500);
    }
}
