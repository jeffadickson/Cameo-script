using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>
/// Model-based tests of <see cref="Table"/> (CsCheck): random operation sequences are applied to
/// a table and to a plain dictionary model, and the two must agree after every step.
/// </summary>
public class TableProperties
{
    private abstract record Op;
    private sealed record SetOp(Value Key, Value Val) : Op;
    private sealed record GetOp(Value Key) : Op;
    private sealed record AppendOp(Value Val) : Op;
    private sealed record InsertOp(int Pos, Value Val) : Op;
    private sealed record RemoveAtOp(int Pos) : Op;
    private sealed record TraverseOp(int DeleteMask) : Op;
    private sealed record SetRangeOp(Value[] Vals) : Op { public override string ToString() => "SetRange[" + string.Join(",", Vals) + "]"; }

    // Keys deliberately collide across kinds: 2, 2.0 and "2" must be two keys, not three or one.
    private static readonly Gen<Value> GenKey = Gen.Frequency(
        (6, Gen.Int[-2, 12].Select(i => Value.FromInt(i))),
        (2, Gen.Int[1, 6].Select(i => Value.FromFloat(i))),
        (1, Gen.Double[-3, 9].Select(d => Value.FromFloat(d))),
        (2, Gen.OneOf("a", "b", "c", "1", "2").Select(s => Value.FromString(s))),
        (1, Gen.Bool.Select(b => Value.FromBool(b))),
        (1, Gen.Long.Select(l => Value.FromInt(l))));

    private static readonly Gen<Value> GenNonNil = Gen.OneOf(
        Gen.Int[0, 99].Select(i => Value.FromInt(i)),
        Gen.OneOf("x", "y").Select(s => Value.FromString(s)),
        Gen.Const(Value.False));

    private static readonly Gen<Value> GenVal = Gen.Frequency((4, GenNonNil), (1, Gen.Const(Value.Nil)));

    private static readonly Gen<Op> GenOp = Gen.Frequency(
        (8, Gen.Select(GenKey, GenVal, (k, v) => (Op)new SetOp(k, v))),
        (3, GenKey.Select(k => (Op)new GetOp(k))),
        (5, GenNonNil.Select(v => (Op)new AppendOp(v))),
        (2, Gen.Select(Gen.Int[0, 1000], GenNonNil, (p, v) => (Op)new InsertOp(p, v))),
        (2, Gen.Int[0, 1000].Select(p => (Op)new RemoveAtOp(p))),
        (1, Gen.Int.Select(m => (Op)new TraverseOp(m))),
        (1, GenVal.Array[0, 6].Select(a => (Op)new SetRangeOp(a))));

    /// <summary>Model key: independent of Value's own Equals/GetHashCode.</summary>
    private static object Norm(Value k) => k.Kind switch
    {
        ValueKind.Integer => k.I,
        ValueKind.Float => k.N == Math.Floor(k.N) && k.N >= -9.2e18 && k.N <= 9.2e18 ? (object)(long)k.N : k.N,
        ValueKind.String => k.AsString,
        ValueKind.Boolean => k.I != 0,
        _ => throw new InvalidOperationException(),
    };

    private static bool Same(Value a, Value b) =>
        a.Kind == b.Kind && (a.Kind == ValueKind.String ? a.AsString == b.AsString : a.Kind == ValueKind.Float ? a.N.Equals(b.N) : a.I == b.I);

    private static Value ModelGet(Dictionary<object, Value> m, long i) => m.TryGetValue(i, out Value v) ? v : Value.Nil;

    private static bool IsSequence(Dictionary<object, Value> m, int n)
    {
        int ints = 0;
        foreach (object k in m.Keys)
        {
            if (k is long l)
            {
                if (l < 1 || l > n) return false;
                ints++;
            }
        }
        return ints == n;
    }

    private static void CheckAgainstModel(Table t, Dictionary<object, Value> m)
    {
        // every model entry is in the table
        foreach (var kv in m)
        {
            Value key = kv.Key switch { long l => Value.FromInt(l), double d => Value.FromFloat(d), string s => Value.FromString(s), bool b => Value.FromBool(b), _ => default };
            Assert.True(Same(kv.Value, t.Get(key)), $"key {kv.Key}: expected {kv.Value}, got {t.Get(key)}");
        }
        // the table has nothing else, and traversal yields each key exactly once
        var seen = new HashSet<object>();
        foreach (var kv in t.Pairs())
        {
            Assert.False(kv.Value.IsNil);
            Assert.True(seen.Add(Norm(kv.Key)), $"key {kv.Key} visited twice");
            Assert.True(m.TryGetValue(Norm(kv.Key), out Value mv) && Same(mv, kv.Value), $"unexpected entry {kv.Key}={kv.Value}");
            // integer-valued float keys are stored as integers
            Assert.False(kv.Key.Kind == ValueKind.Float && Norm(kv.Key) is long);
        }
        Assert.Equal(m.Count, seen.Count);
        // the length is a border: t[n] ~= nil (or n == 0) and t[n+1] == nil
        int n = t.Length;
        Assert.True(n >= 0);
        Assert.True(n == 0 || !ModelGet(m, n).IsNil, $"length {n} is not a border (t[n] is nil)");
        Assert.True(ModelGet(m, n + 1).IsNil, $"length {n} is not a border (t[n+1] is not nil)");
    }

    private static void Apply(Table t, Dictionary<object, Value> m, Op op)
    {
        switch (op)
        {
            case SetOp s:
                t.Set(s.Key, s.Val);
                if (s.Val.IsNil) m.Remove(Norm(s.Key)); else m[Norm(s.Key)] = s.Val;
                break;
            case GetOp g:
                Assert.True(Same(m.TryGetValue(Norm(g.Key), out Value mv) ? mv : Value.Nil, t.Get(g.Key)));
                if (g.Key.Kind == ValueKind.Integer) Assert.True(Same(t.Get(g.Key), t.GetInt(g.Key.I)));
                if (g.Key.Kind == ValueKind.String) Assert.True(Same(t.Get(g.Key), t.GetStr(g.Key.AsString)));
                break;
            case AppendOp a:
                {
                    // Append is defined on the array part; mirror t[#t + 1] = v when they coincide
                    int n = t.Length;
                    if (n == t.ArrayCount)
                    {
                        t.Append(a.Val);
                        m[(long)n + 1] = a.Val;
                    }
                    else
                    {
                        t.SetInt(n + 1, a.Val);
                        m[(long)n + 1] = a.Val;
                    }
                    break;
                }
            case InsertOp i:
                {
                    int n = t.Length;
                    if (!IsSequence(m, n)) break;
                    int pos = i.Pos % (n + 1) + 1;
                    t.Insert(pos, i.Val);
                    for (long k = n; k >= pos; k--) m[k + 1] = m[k];
                    m[(long)pos] = i.Val;
                    break;
                }
            case RemoveAtOp r:
                {
                    int n = t.Length;
                    if (n == 0 || !IsSequence(m, n)) break;
                    int pos = r.Pos % n + 1;
                    Value removed = t.RemoveAt(pos);
                    Assert.True(Same(m[(long)pos], removed));
                    for (long k = pos; k < n; k++) m[k] = m[k + 1];
                    m.Remove((long)n);
                    break;
                }
            case TraverseOp tr:
                {
                    // next() must survive assigning nil to visited keys, visiting every entry once
                    int expected = m.Count, visited = 0, bit = 0;
                    Value key = default;
                    var seen = new HashSet<object>();
                    while (t.Next(key, out Value nk, out Value nv))
                    {
                        visited++;
                        Assert.True(seen.Add(Norm(nk)));
                        Assert.True(m.TryGetValue(Norm(nk), out Value tv) && Same(tv, nv));
                        if (((tr.DeleteMask >> (bit++ % 31)) & 1) != 0)
                        {
                            t.Set(nk, Value.Nil);
                            m.Remove(Norm(nk));
                        }
                        key = nk;
                    }
                    Assert.Equal(expected, visited);
                    break;
                }
            case SetRangeOp sr:
                {
                    // what a table constructor does: positional values after the current array part
                    int start = t.ArrayCount;
                    t.SetRange(start, sr.Vals);
                    for (int k = 0; k < sr.Vals.Length; k++)
                    {
                        if (sr.Vals[k].IsNil) m.Remove((long)start + 1 + k); else m[(long)start + 1 + k] = sr.Vals[k];
                    }
                    break;
                }
        }
    }

    [Fact]
    public void Table_agrees_with_dictionary_model()
    {
        Prop.Cs(Gen.OneOf(GenOp.Array[0, 8], GenOp.Array[0, 60]), ops =>
        {
            var t = new Table();
            var m = new Dictionary<object, Value>();
            foreach (Op op in ops)
            {
                Apply(t, m, op);
                CheckAgainstModel(t, m);
            }
        }, iter: 20000, print: ops => string.Join("; ", ops.Select(o => o.ToString())));
    }

    [Fact]
    public void Presized_table_agrees_with_dictionary_model()
    {
        Prop.Cs(Gen.Select(Gen.Int[0, 8], Gen.Int[0, 8], GenOp.Array[0, 40]), x =>
        {
            var t = new Table(x.Item1, x.Item2);
            var m = new Dictionary<object, Value>();
            foreach (Op op in x.Item3)
            {
                Apply(t, m, op);
                CheckAgainstModel(t, m);
            }
        }, iter: 500);
    }

    [Fact]
    public void Sequence_built_in_any_order_has_full_length()
    {
        // t[i] = v for a permutation of 1..n must end with #t == n (array/hash migration)
        Prop.Cs(Gen.Int[0, 40].SelectMany(n => Gen.Shuffle(Enumerable.Range(1, n).ToArray())), perm =>
        {
            var t = new Table();
            foreach (int i in perm) t.SetInt(i, Value.FromInt(i * 10));
            Assert.Equal(perm.Length, t.Length);
            for (int i = 1; i <= perm.Length; i++) Assert.Equal(i * 10, t.GetInt(i).I);
            // and removing from the end shrinks it one by one
            for (int i = perm.Length; i >= 1; i--)
            {
                t.SetInt(i, Value.Nil);
                Assert.Equal(i - 1, t.Length);
            }
            Assert.False(t.Next(Value.Nil, out _, out _));
        });
    }

    // Regressions found by Table_agrees_with_dictionary_model: a deleted hash entry for an integer
    // key that later lived in the array part made next() skip live hash entries.
    [Fact]
    public void Traversal_with_deletion_after_key_migrated_from_hash_to_array()
    {
        var t = new Table(4, 1);
        t.Set(Value.FromInt(7), "x");
        t.Set(Value.FromFloat(2.0), Value.False);   // goes to the hash part...
        t.Append(Value.FromInt(70));                // ...and migrates into the array part here
        AssertDeleteAllVisitsAll(t, 3);
    }

    [Fact]
    public void Traversal_with_deletion_after_deleted_hash_key_is_appended()
    {
        var t = new Table();
        t.Set(Value.FromFloat(-0.5), "x");
        t.Set(Value.FromInt(2), "y");
        t.Set(Value.FromInt(2), Value.Nil);         // leaves a deleted entry for key 2
        t.Append(Value.FromInt(69));
        t.Append(Value.FromInt(27));                // key 2 now lives in the array part
        AssertDeleteAllVisitsAll(t, 3);
    }

    private static void AssertDeleteAllVisitsAll(Table t, int expected)
    {
        int visited = 0;
        Value key = default;
        while (t.Next(key, out Value nk, out _))
        {
            visited++;
            t.Set(nk, Value.Nil);
            key = nk;
        }
        Assert.Equal(expected, visited);
        Assert.False(t.Next(Value.Nil, out _, out _));
    }

    [Fact]
    public void Nil_and_NaN_keys_are_rejected_and_reads_of_them_are_nil()
    {
        var t = new Table();
        Assert.Throws<LuaException>(() => t.Set(Value.Nil, Value.True));
        Assert.Throws<LuaException>(() => t.Set(Value.FromFloat(double.NaN), Value.True));
        Assert.True(t.Get(Value.Nil).IsNil);
        Assert.True(t.Get(Value.FromFloat(double.NaN)).IsNil);
        Assert.Throws<LuaException>(() => t.Next(Value.FromString("absent"), out _, out _));
        Assert.Equal(0, t.Length);
    }
}
