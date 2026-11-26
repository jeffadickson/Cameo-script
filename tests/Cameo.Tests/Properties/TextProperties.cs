using System.Text;
using Hedgehog;
using Hedgehog.Linq;
using Cameo.Runtime.Vm;
using Xunit;
using Gen = Hedgehog.Linq.Gen;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>
/// Byte strings and UTF-8 (Hedgehog): <see cref="LuaText"/> against System.Text.Encoding.UTF8,
/// and the <c>utf8</c> library against an independent reference decoder, on valid text and
/// on corrupted byte strings.
/// </summary>
public class TextProperties
{
    private readonly LuaHost _lua = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // ------------------------------------------------------------------ generators

    /// <summary>Unicode scalar values, weighted towards the encoding-length boundaries.</summary>
    private static readonly Gen<int> GenScalar = Gen.Frequency(
        (4, Gen.Int32(Range.LinearInt32(0, 0x7F))),
        (2, Gen.Int32(Range.LinearInt32(0x80, 0x7FF))),
        (2, Gen.Int32(Range.LinearInt32(0x800, 0xD7FF))),
        (1, Gen.Int32(Range.LinearInt32(0xE000, 0xFFFF))),
        (2, Gen.Int32(Range.LinearInt32(0x10000, 0x10FFFF))),
        (1, Gen.Item(0, 0x7F, 0x80, 0x7FF, 0x800, 0xFFFF, 0x10000, 0x10FFFF)));

    private static readonly Gen<List<int>> GenScalars = GenScalar.List(Range.LinearInt32(0, 12));

    private static string TextOf(IEnumerable<int> scalars)
    {
        var sb = new StringBuilder();
        foreach (int cp in scalars) sb.Append(char.ConvertFromUtf32(cp));
        return sb.ToString();
    }

    /// <summary>Byte strings that are mostly UTF-8 with random damage.</summary>
    private static readonly Gen<byte[]> GenBytes =
        from scalars in GenScalars
        from damage in Gen.Frequency(
            (1, Gen.Constant(new List<(int, byte)>())),
            (3, (from pos in Gen.Int32(Range.LinearInt32(0, 60))
                 from b in Gen.Frequency(
                     (2, Gen.Byte(Range.Constant<byte>(0x80, 0xBF))),
                     (2, Gen.Byte(Range.Constant<byte>(0xC0, 0xFF))),
                     (1, Gen.Byte(Range.Constant<byte>(0, 0x7F))))
                 select (pos, b)).List(Range.LinearInt32(1, 4))))
        select Damage(Encoding.UTF8.GetBytes(TextOf(scalars)), damage);

    private static byte[] Damage(byte[] bytes, List<(int, byte)> damage)
    {
        var list = new List<byte>(bytes);
        foreach (var (pos, b) in damage)
        {
            if (list.Count == 0 || pos % 3 == 0) list.Insert(pos % (list.Count + 1), b);
            else list[pos % list.Count] = b;
        }
        return list.ToArray();
    }

    // ------------------------------------------------------------------ reference UTF-8 decoder (Lua's rules)

    private static readonly long[] MinForLength = { 0, 0, 0x80, 0x800, 0x10000, 0x200000, 0x4000000 };

    /// <summary>Decodes one sequence; returns its length or 0 when it is not valid.</summary>
    private static int RefDecode(byte[] s, int i, bool strict, out long code)
    {
        code = 0;
        if (i >= s.Length) return 0;
        int c = s[i];
        if (c < 0x80) { code = c; return 1; }
        if (c < 0xC0) return 0;                         // continuation byte
        int len = c < 0xE0 ? 2 : c < 0xF0 ? 3 : c < 0xF8 ? 4 : c < 0xFC ? 5 : c < 0xFE ? 6 : 0;
        if (len == 0 || i + len > s.Length) return 0;
        long v = c & (0x7F >> len);
        for (int k = 1; k < len; k++)
        {
            if ((s[i + k] & 0xC0) != 0x80) return 0;
            v = (v << 6) | (long)(s[i + k] & 0x3F);
        }
        if (v < MinForLength[len] || v > 0x7FFFFFFF) return 0;   // overlong / out of range
        if (strict && (v > 0x10FFFF || (v >= 0xD800 && v <= 0xDFFF))) return 0;
        code = v;
        return len;
    }

    private static Value Str(byte[] bytes) => Value.FromString(LuaText.FromBytes(bytes));

    // ------------------------------------------------------------------ LuaText

    [Fact]
    public void Encode_and_decode_agree_with_dotnet_utf8()
    {
        Prop.Hh(GenScalars, scalars =>
        {
            string text = TextOf(scalars);
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            string vm = LuaText.Encode(text);
            Assert.Equal(utf8, LuaText.ToBytes(vm));
            Assert.Equal(utf8.Length, vm.Length);
            Assert.Equal(text, LuaText.Decode(vm));
            Assert.Equal(vm, LuaText.FromBytes(utf8));
            // ASCII text is its own encoding (and no copy is made)
            if (scalars.TrueForAll(cp => cp < 0x80)) Assert.Same(text, vm);
            // values: FromText/ToText are the same conversions
            Assert.Equal(vm, Value.FromText(text).AsString);
            Assert.Equal(text, Value.FromText(text).ToText());
        }, 400);
    }

    [Fact]
    public void Decode_of_arbitrary_bytes_matches_dotnet_replacement_decoding()
    {
        Prop.Hh(GenBytes, bytes =>
        {
            string vm = LuaText.FromBytes(bytes);
            Assert.Equal(bytes, LuaText.ToBytes(vm));
            Assert.Equal(Encoding.UTF8.GetString(bytes), LuaText.Decode(vm));
        }, 400);
    }

    [Fact]
    public void AppendUtf8_encodes_every_code_point_up_to_31_bits()
    {
        var gen = Gen.Frequency(
            (3, GenScalar.Select(cp => (long)cp)),
            (2, Gen.Int64(Range.LinearInt64(0, 0x7FFFFFFF))),
            (1, Gen.Item(0xD800L, 0xDFFFL, 0x110000L, 0x1FFFFFL, 0x200000L, 0x3FFFFFFL, 0x4000000L, 0x7FFFFFFFL)));
        Prop.Hh(gen, cp =>
        {
            var sb = new StringBuilder();
            LuaText.AppendUtf8(sb, cp);
            byte[] bytes = LuaText.ToBytes(sb.ToString());
            int expectedLen = cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : cp < 0x200000 ? 4 : cp < 0x4000000 ? 5 : 6;
            Assert.Equal(expectedLen, bytes.Length);
            Assert.Equal(expectedLen, RefDecode(bytes, 0, false, out long back));
            Assert.Equal(cp, back);
            if (cp <= 0x10FFFF && !(cp >= 0xD800 && cp <= 0xDFFF))
                Assert.Equal(Encoding.UTF8.GetBytes(char.ConvertFromUtf32((int)cp)), bytes);
        }, 400);
    }

    // ------------------------------------------------------------------ utf8 library on valid text

    [Fact]
    public void Utf8_library_on_valid_text()
    {
        Prop.Hh(GenScalars, scalars =>
        {
            string text = TextOf(scalars);
            Value s = Value.FromText(text);
            int n = scalars.Count;
            // len counts code points; #s counts bytes
            Assert.Equal(n, _lua.Call1("utf8.len", s).I);
            Assert.Equal(Encoding.UTF8.GetByteCount(text), _lua.Call1("string.len", s).I);
            // codepoint(s, 1, -1) lists them; char() is the inverse
            Value[] cps = _lua.Call("utf8.codepoint", s, 1, -1);
            Assert.Equal(scalars.Select(cp => (long)cp), cps.Select(v => v.I));
            Assert.Equal(s.AsString, _lua.Call1("utf8.char", scalars.Select(cp => Value.FromInt(cp)).ToArray()).AsString);
            // offset(s, i) is the byte position of the i-th character; codes() walks the same positions
            var positions = new List<long>();
            long pos = 1;
            foreach (int cp in scalars)
            {
                positions.Add(pos);
                pos += Encoding.UTF8.GetByteCount(char.ConvertFromUtf32(cp));
            }
            for (int i = 1; i <= n; i++) Assert.Equal(positions[i - 1], _lua.Call1("utf8.offset", s, i).I);
            Assert.Equal(pos, _lua.Call1("utf8.offset", s, n + 1).I);                 // one past the end
            Assert.True(_lua.Call1("utf8.offset", s, n + 2).IsNil);
            for (int i = 1; i <= n; i++) Assert.Equal(positions[n - i], _lua.Call1("utf8.offset", s, -i).I);
            Value[] it = _lua.Call("utf8.codes", s);
            Value ctl = it[2];
            foreach (var (p, cp) in positions.Zip(scalars))
            {
                Value[] step = _lua.L.Call(it[0], it[1], ctl);
                Assert.Equal(p, step[0].I);
                Assert.Equal(cp, step[1].I);
                ctl = step[0];
            }
            Assert.Empty(_lua.L.Call(it[0], it[1], ctl));
            // the character pattern splits the string into exactly its characters
            Value[] g = _lua.Call("string.gmatch", s, _lua.L.GetGlobal("utf8").AsTable.GetStr("charpattern"));
            foreach (int cp in scalars)
                Assert.Equal(LuaText.Encode(char.ConvertFromUtf32(cp)), _lua.L.Call(g[0])[0].AsString);
            Assert.True(_lua.L.Call(g[0]).Length == 0 || _lua.L.Call(g[0])[0].IsNil);
        }, 300);
    }

    // ------------------------------------------------------------------ utf8 library on corrupted input

    [Fact]
    public void Utf8_len_finds_the_first_invalid_byte()
    {
        Prop.Hh(from bytes in GenBytes from lax in Gen.Item(false, true) select (bytes, lax), x =>
        {
            var (bytes, lax) = x;
            int i = 0;
            long count = 0;
            long? bad = null;
            while (i < bytes.Length)
            {
                int len = RefDecode(bytes, i, !lax, out _);
                if (len == 0) { bad = i + 1; break; }
                i += len;
                count++;
            }
            Value[] r = _lua.Call("utf8.len", Str(bytes), 1, -1, lax);
            if (bad == null)
            {
                Assert.Equal(count, r[0].I);
                // agreement with .NET: strict validity is exactly well-formed UTF-8
                if (!lax) StrictUtf8.GetString(bytes);
            }
            else
            {
                Assert.True(r[0].IsNil);
                Assert.Equal(bad.Value, r[1].I);
                if (!lax && IsWellFormed(bytes)) Assert.Fail("reference rejected well-formed UTF-8");
            }
        }, 600);
    }

    private static bool IsWellFormed(byte[] bytes)
    {
        try { StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    [Fact]
    public void Utf8_codepoint_and_codes_reject_exactly_the_invalid_strings()
    {
        Prop.Hh(GenBytes, bytes =>
        {
            var expected = new List<long>();
            bool valid = true;
            for (int i = 0; i < bytes.Length;)
            {
                int len = RefDecode(bytes, i, true, out long cp);
                if (len == 0) { valid = false; break; }
                expected.Add(cp);
                i += len;
            }
            Assert.Equal(valid, IsWellFormed(bytes));
            Value s = Str(bytes);
            if (valid)
            {
                Assert.Equal(expected, _lua.Call("utf8.codepoint", s, 1, -1).Select(v => v.I));
            }
            else
            {
                Assert.Contains("invalid UTF-8 code", _lua.ErrorOf(() => _lua.Call("utf8.codepoint", s, 1, -1)) ?? "no error");
            }
            // walking with codes() yields the same code points, or fails at the damage
            var seen = new List<long>();
            string? err = _lua.ErrorOf(() =>
            {
                Value[] it = _lua.Call("utf8.codes", s);
                Value ctl = it[2];
                while (true)
                {
                    Value[] step = _lua.L.Call(it[0], it[1], ctl);
                    if (step.Length == 0 || step[0].IsNil) break;
                    seen.Add(step[1].I);
                    ctl = step[0];
                }
                return null;
            });
            if (valid)
            {
                Assert.Null(err);
                Assert.Equal(expected, seen);
            }
            else
            {
                Assert.NotNull(err);
                Assert.Equal(expected.Take(seen.Count), seen);
            }
        }, 600);
    }

    [Fact]
    public void Utf8_char_rejects_out_of_range_values_and_argument_errors_are_reported()
    {
        Assert.Contains("value out of range", _lua.ErrorOf(() => _lua.Call("utf8.char", -1)));
        Assert.Contains("value out of range", _lua.ErrorOf(() => _lua.Call("utf8.char", 0x80000000L)));
        Assert.Equal(6, _lua.Call1("utf8.char", 0x7FFFFFFF).AsString.Length);
        Assert.Contains("out of bounds", _lua.ErrorOf(() => _lua.Call("utf8.codepoint", "abc", 4)));
        Assert.Contains("out of bounds", _lua.ErrorOf(() => _lua.Call("utf8.codepoint", "abc", 0)));
        Assert.Contains("initial position out of bounds", _lua.ErrorOf(() => _lua.Call("utf8.len", "abc", 5)));
        Assert.Contains("final position out of bounds", _lua.ErrorOf(() => _lua.Call("utf8.len", "abc", 1, 4)));
        Assert.Contains("position out of bounds", _lua.ErrorOf(() => _lua.Call("utf8.offset", "abc", 1, 5)));
        Assert.Contains("continuation byte", _lua.ErrorOf(() => _lua.Call("utf8.offset", Value.FromText("é"), 1, 2)));
        Assert.Equal(1, _lua.Call1("utf8.offset", Value.FromText("é"), 0, 2).I);   // start of the character containing byte 2
        Assert.Equal(0, _lua.Call1("utf8.len", "").I);
        Assert.Equal(0, _lua.Call1("utf8.len", "abc", 4).I);
        Assert.Empty(_lua.Call("utf8.codepoint", "abc", 3, 2));
        Assert.Equal("", _lua.Call1("utf8.char").AsString);
        // lax mode accepts surrogates and values above 10FFFF; strict mode does not
        Value sur = _lua.Call1("utf8.char", 0xD800), big = _lua.Call1("utf8.char", 0x110000);
        Assert.True(_lua.Call1("utf8.len", sur).IsNil);
        Assert.Equal(1, _lua.Call1("utf8.len", sur, 1, -1, true).I);
        Assert.True(_lua.Call1("utf8.len", big).IsNil);
        Assert.Equal(0x110000, _lua.Call1("utf8.codepoint", big, 1, 1, true).I);
    }
}
