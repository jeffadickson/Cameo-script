using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>string.pack / string.unpack / string.packsize round-trips and layouts (CsCheck).</summary>
public class PackProperties
{
    private readonly LuaHost _lua = new();

    // ------------------------------------------------------------------ pack / unpack (CsCheck)

    private sealed record PackItem(string Fmt, Value Val, int Size);

    private static Gen<PackItem> IntItem(string fmt, int bytes, bool signed)
    {
        Gen<long> g;
        if (bytes >= 8) g = signed ? Gen.Long : Gen.Long[0, long.MaxValue];
        else
        {
            long max = signed ? (1L << (bytes * 8 - 1)) - 1 : (1L << (bytes * 8)) - 1;
            long min = signed ? -max - 1 : 0;
            g = Gen.OneOf(Gen.Long[min, max], Gen.Const(min), Gen.Const(max));
        }
        return g.Select(v => new PackItem(fmt, Value.FromInt(v), bytes));
    }

    private static readonly Gen<string> GenPackString = Gen.String[Gen.Char[(char)1, (char)255], 0, 9];

    private static readonly Gen<PackItem> GenPackItem = Gen.OneOf(
        IntItem("b", 1, true), IntItem("B", 1, false), IntItem("h", 2, true), IntItem("H", 2, false),
        IntItem("i", 4, true), IntItem("I", 4, false), IntItem("l", 8, true), IntItem("L", 8, false),
        IntItem("j", 8, true), IntItem("J", 8, false), IntItem("T", 8, false),
        Gen.Int[1, 8].SelectMany(n => IntItem("i" + n, n, true)),
        Gen.Int[1, 8].SelectMany(n => IntItem("I" + n, n, false)),
        Gen.Float.Where(f => !float.IsNaN(f)).Select(f => new PackItem("f", Value.FromFloat(f), 4)),
        Gen.Double.Where(d => !double.IsNaN(d)).Select(d => new PackItem("d", Value.FromFloat(d), 8)),
        Gen.Double.Where(d => !double.IsNaN(d)).Select(d => new PackItem("n", Value.FromFloat(d), 8)),
        GenPackString.Select(s => new PackItem("z", Value.FromString(s), -(s.Length + 1))),
        Gen.Select(Gen.OneOf(1, 2, 4, 8), GenPackString, (n, s) => new PackItem("s" + n, Value.FromString(s), -(n + s.Length))),
        GenPackString.Select(s => new PackItem("s", Value.FromString(s), -(8 + s.Length))));

    private static bool SameValue(Value a, Value b) =>
        a.Kind == b.Kind && (a.IsString ? a.AsString == b.AsString : a.IsFloat ? a.N.Equals(b.N) : a.I == b.I);

    [Fact]
    public void Pack_and_unpack_round_trip()
    {
        Prop.Cs(Gen.Select(Gen.OneOf("<", ">", "=", ""), GenPackItem.Array[0, 8]), x =>
        {
            var (endian, items) = x;
            string fmt = endian + string.Concat(items.Select(i => i.Fmt + " "));
            var args = new List<Value> { fmt };
            args.AddRange(items.Select(i => i.Val));
            string packed = _lua.Call1("string.pack", args.ToArray()).AsString;
            // without alignment the size is the sum of the item sizes
            Assert.Equal(items.Sum(i => Math.Abs(i.Size)), packed.Length);
            if (items.All(i => i.Size > 0)) Assert.Equal(packed.Length, _lua.Call1("string.packsize", fmt).I);
            Value[] back = _lua.Call("string.unpack", fmt, packed);
            Assert.Equal(items.Length + 1, back.Length);
            for (int k = 0; k < items.Length; k++)
                Assert.True(SameValue(items[k].Val, back[k]), $"{fmt}: item {k} {items[k].Val} came back as {back[k]}");
            Assert.Equal(packed.Length + 1, back[^1].I);
            // every item can also be unpacked on its own from its offset
            int off = 1;
            foreach (PackItem it in items)
            {
                Value[] one = _lua.Call("string.unpack", endian + it.Fmt, packed, off);
                Assert.True(SameValue(it.Val, one[0]));
                off += Math.Abs(it.Size);
                Assert.Equal(off, one[1].I);
            }
        }, iter: 1500);
    }

    [Fact]
    public void Pack_integer_layout_matches_BinaryPrimitives()
    {
        Prop.Cs(Gen.Select(Gen.Long, Gen.Int[1, 8], Gen.Bool), x =>
        {
            var (v, n, little) = x;
            long truncated = n == 8 ? v : v & ((1L << (n * 8)) - 1);
            string packed = _lua.Call1("string.pack", (little ? "<" : ">") + "I" + n, n == 8 ? Value.FromInt(v) : Value.FromInt(truncated)).AsString;
            var expected = new byte[n];
            for (int k = 0; k < n; k++) expected[little ? k : n - 1 - k] = (byte)(truncated >> (8 * k));
            Assert.Equal(expected, LuaText.ToBytes(packed));
            // floats and doubles use IEEE layouts
            Assert.Equal(BitConverter.GetBytes((double)v), LuaText.ToBytes(_lua.Call1("string.pack", "<d", (double)v).AsString));
            Assert.Equal(BitConverter.GetBytes((float)v), LuaText.ToBytes(_lua.Call1("string.pack", "<f", (double)(float)v).AsString));
        }, iter: 1000);
    }

    [Fact]
    public void Pack_overflow_and_alignment()
    {
        Assert.Contains("overflow", _lua.ErrorOf(() => _lua.Call("string.pack", "b", 128)));
        Assert.Contains("overflow", _lua.ErrorOf(() => _lua.Call("string.pack", "B", -1)));
        Assert.Contains("overflow", _lua.ErrorOf(() => _lua.Call("string.pack", "i2", 40000)));
        Assert.Contains("data string too short", _lua.ErrorOf(() => _lua.Call("string.unpack", "i4", "ab")));
        Assert.Contains("invalid format option", _lua.ErrorOf(() => _lua.Call("string.pack", "q", 1)));
        Assert.Contains("contains zeros", _lua.ErrorOf(() => _lua.Call("string.pack", "z", "a\0b")));
        // "!" aligns to the item size: one byte, three bytes of padding, then the int
        Assert.Equal(8, _lua.Call1("string.pack", "!bi4", 1, 2).AsString.Length);
        Assert.Equal(8, _lua.Call1("string.packsize", "!bi4").I);
        Assert.Equal(16, _lua.Call1("string.packsize", "!bd").I);
        Assert.Equal(3, _lua.Call1("string.packsize", "bxb").I);
    }
}
