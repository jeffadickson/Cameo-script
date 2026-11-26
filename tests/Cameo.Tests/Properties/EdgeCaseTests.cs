using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests.Properties;

/// <summary>
/// Example-based tests for boundary cases the random generators reach rarely or never.
/// Each of these was added to kill a mutant that survived a Stryker.NET run.
/// </summary>
public class EdgeCaseTests
{
    private readonly LuaHost _lua = new();

    private static string Bytes(params int[] b) => new string(b.Select(x => (char)x).ToArray());

    // ------------------------------------------------------------------ Table

    private static int DeleteAll(Table t)
    {
        int visited = 0;
        Value key = default;
        while (t.Next(key, out Value nk, out _))
        {
            visited++;
            t.Set(nk, Value.Nil);
            key = nk;
        }
        return visited;
    }

    [Fact]
    public void Table_traversal_after_constructor_range_reuses_a_deleted_key()
    {
        var t = new Table();
        t.Set("x", 1);
        t.Set(Value.FromInt(2), "old");
        t.Set(Value.FromInt(2), Value.Nil);                       // deleted entry for key 2 stays in the hash part
        t.SetRange(0, new Value[] { 10, 20, 30 });                // keys 1..3 now live in the array part
        Assert.Equal(3, t.Length);
        Assert.Equal(4, DeleteAll(t));
    }

    [Fact]
    public void Table_traversal_after_insert_reuses_a_deleted_key()
    {
        var t = new Table();
        t.Set("x", 1);
        t.Set(Value.FromInt(2), "old");                           // in the hash part: the array part is still empty
        t.Set(Value.FromInt(2), Value.Nil);                       // ...and deleted again
        t.Append(10);
        t.Insert(1, 5);                                           // key 2 now lives in the array part
        Assert.Equal(2, t.Length);
        Assert.Equal(3, DeleteAll(t));
    }

    [Fact]
    public void Table_insert_pulls_following_keys_out_of_the_hash_part()
    {
        var t = new Table();
        t.Append(10);
        t.Set(Value.FromInt(3), 30);                              // waits in the hash part
        t.Insert(1, 5);                                           // 5, 10 -> key 3 becomes adjacent
        Assert.Equal(3, t.Length);
        Assert.Equal(3, t.ArrayCount);
        Assert.Equal(30, t.GetInt(3).I);
    }

    [Fact]
    public void Table_next_accepts_integral_float_keys_and_rejects_unknown_keys()
    {
        var t = new Table();
        t.Append("a");
        t.Append("b");
        Assert.True(t.Next(Value.FromFloat(1.0), out Value k, out Value v));
        Assert.Equal(2, k.I);
        Assert.Equal("b", v.AsString);
        // a key that was never in the table is an error, whatever its payload looks like
        Assert.Throws<LuaException>(() => t.Next(Value.True, out _, out _));
        Assert.Throws<LuaException>(() => t.Next(Value.FromFloat(1.5), out _, out _));
        Assert.Throws<LuaException>(() => t.Next(Value.FromInt(-1), out _, out _));
    }

    [Fact]
    public void Table_remove_trims_trailing_holes_and_capacity_can_be_reserved()
    {
        var t = new Table();
        t.SetRange(0, new Value[] { 1, Value.Nil, 3 });
        Assert.Equal(3, t.Length);
        Assert.Equal(3, t.RemoveAt(3).I);
        Assert.Equal(1, t.Length);                                // the hole at 2 is not a border
        t.EnsureArrayCapacity(100);
        Assert.Equal(1, t.Length);
        t.Append(2);
        Assert.Equal(2, t.ArraySpan.Length);
        Assert.Equal(2, t.ArraySpan[1].I);
        // string fields: overwrite, delete, re-add
        t.SetStr("f", 1);
        t.SetStr("f", 2);
        Assert.Equal(2, t.GetStr("f").I);
        t.SetStr("f", Value.Nil);
        Assert.True(t.GetStr("f").IsNil);
        t.SetStr("f", 3);
        Assert.Equal(3, t["f"].I);
        Assert.Equal(2, t[2].I);
        t[Value.FromInt(5)] = 50;
        Assert.Equal(50, t[5L].I);
    }

    // ------------------------------------------------------------------ Value

    [Fact]
    public void Value_kinds_conversions_and_text()
    {
        var L = new LuaState();
        Value u = Value.FromUtf16(L, "héllo");
        Assert.True(u.IsUtf16);
        Assert.True(u.IsUserdata);
        Assert.False(Value.FromInt(1).IsUtf16);
        Assert.False(Value.FromUserdata(new object()).IsUtf16);
        Assert.False(Value.FromInt(1).IsUserdata);
        Assert.Equal("héllo", u.ToText());
        Assert.Equal("12", Value.FromInt(12).ToText());
        Assert.Equal("é", Value.FromText("é").ToText());
        Assert.Equal(2, Value.FromText("é").AsString.Length);

        Value th = Value.FromThread(L.MainThread);
        Assert.True(th.IsThread);
        Assert.False(u.IsThread);
        Assert.Equal("thread", th.TypeName);

        LuaFunction? none = null;
        Assert.True(((Value)none).IsNil);
        var native = new NativeFunction("f", (ref CallContext c) => 0);
        Value fn = native;
        Assert.True(fn.IsFunction);
        Assert.StartsWith("builtin: 0x", fn.ToString());
        Value closure = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false }).RunString("return function() end")[0];
        Assert.StartsWith("function: 0x", closure.ToString());
        Assert.StartsWith("table: 0x", Value.FromTable(new Table()).ToString());

        Assert.True(Value.FromString("1.5").TryToDouble(out double d));
        Assert.Equal(1.5, d);
        Assert.False(Value.FromString("x").TryToDouble(out _));
        Assert.False(Value.True.TryToDouble(out _));
        Assert.False(Value.FromString("1.5").TryToInteger(out _));

        // booleans hash differently from each other (and are distinct table keys)
        Assert.NotEqual(Value.True.GetHashCode(), Value.False.GetHashCode());
        Assert.Equal(Value.True.GetHashCode(), Value.FromBool(true).GetHashCode());
    }

    [Fact]
    public void Number_formatting_boundaries()
    {
        // PUC Lua prints the sign bit of a NaN
        Assert.Equal("-nan", LuaNumber.FloatToString(double.NaN));
        Assert.Equal("nan", LuaNumber.FloatToString(BitConverter.Int64BitsToDouble(0x7FF8000000000000)));
        Assert.Equal("NAN", LuaNumber.FormatG(double.NaN, 6, false, upper: true));
        Assert.Equal("nan", LuaNumber.FormatG(double.NaN, 6, false, upper: false));
        Assert.Equal("0", LuaNumber.FormatG(0, 1, true));            // no fraction digits to keep
        Assert.Equal("0.0", LuaNumber.FormatG(0, 2, true));
        Assert.Equal("1E+20", LuaNumber.FormatG(1e20, 6, false, upper: true));
        Assert.Equal("1e-05", LuaNumber.FormatG(1e-5, 6, false));
        Assert.Equal("0.0001", LuaNumber.FormatG(1e-4, 6, false));
        Assert.Equal("100000", LuaNumber.FormatG(1e5, 6, false));
        Assert.Equal("1e+06", LuaNumber.FormatG(1e6, 6, false));
        Assert.Equal("1.00000e+06", LuaNumber.FormatG(1e6, 6, true));
        Assert.Equal("10", LuaNumber.FormatG(10, 6, false));
        Assert.Equal("10.0000", LuaNumber.FormatG(10, 6, true));
    }

    [Fact]
    public void Hexadecimal_numerals()
    {
        Assert.True(LuaNumber.TryParse("0x1P4", out Value v) && v.IsFloat && v.N == 16.0);
        Assert.True(LuaNumber.TryParse("0x1p+4", out v) && v.N == 16.0);
        Assert.True(LuaNumber.TryParse("0x1p-1", out v) && v.N == 0.5);
        Assert.False(LuaNumber.TryParse("0x1p4x", out _));
        Assert.False(LuaNumber.TryParse("0x1px", out _));
        Assert.False(LuaNumber.TryParse("0x10 1", out _));
        Assert.False(LuaNumber.TryParse("0x1.2.3", out _));
        // a huge exponent saturates instead of overflowing the exponent counter
        Assert.True(LuaNumber.TryParse("0x1p99999999999", out v) && double.IsPositiveInfinity(v.N));
        Assert.True(LuaNumber.TryParse("0x1p-99999999999", out v) && v.N == 0.0);
        Assert.True(LuaNumber.TryParse("0x1p100000", out v) && double.IsPositiveInfinity(v.N));
        // a very long hexadecimal float mantissa is infinite; as an integer it wraps
        string many = new string('f', 300);
        Assert.True(LuaNumber.TryParse("0x" + many + ".0", out v) && double.IsPositiveInfinity(v.N));
        Assert.True(LuaNumber.TryParse("0x" + many, out v) && v.IsInteger && v.I == -1);
        Assert.True(LuaNumber.TryParse("0x" + new string('1', 250) + "p0", out v) && v.IsFloat && !double.IsInfinity(v.N));
    }

    // ------------------------------------------------------------------ utf8 library

    [Fact]
    public void Utf8_boundaries()
    {
        // the largest code point takes six bytes and is only valid in lax mode
        Value max = _lua.Call1("utf8.char", 0x7FFFFFFF);
        Assert.Equal(1, _lua.Call1("utf8.len", max, 1, -1, true).I);
        Assert.Equal(0x7FFFFFFF, _lua.Call1("utf8.codepoint", max, 1, 1, true).I);
        Assert.True(_lua.Call1("utf8.len", max).IsNil);
        // 0xFE announces seven bytes, which is never valid (and must not crash)
        Value[] fe = _lua.Call("utf8.len", Bytes(0xFE, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80), 1, -1, true);
        Assert.True(fe[0].IsNil);
        Assert.Equal(1, fe[1].I);
        // both ends of the surrogate range are rejected in strict mode
        Assert.True(_lua.Call1("utf8.len", _lua.Call1("utf8.char", 0xD800)).IsNil);
        Assert.True(_lua.Call1("utf8.len", _lua.Call1("utf8.char", 0xDFFF)).IsNil);
        Assert.Equal(1, _lua.Call1("utf8.len", _lua.Call1("utf8.char", 0xE000)).I);
        Assert.Equal(1, _lua.Call1("utf8.len", _lua.Call1("utf8.char", 0xD7FF)).I);
        Assert.Equal(1, _lua.Call1("utf8.len", _lua.Call1("utf8.char", 0x10FFFF)).I);
        // position 0 is not a valid start
        Assert.Contains("initial position out of bounds", _lua.ErrorOf(() => _lua.Call("utf8.len", "abc", 0)));
        Assert.Equal(1, _lua.Call1("utf8.len", "abc", -1).I);
        Assert.Equal(3, _lua.Call1("utf8.len", "abc", -3).I);
        Assert.Contains("initial position out of bounds", _lua.ErrorOf(() => _lua.Call("utf8.len", "abc", -4)));
        // offset: n = 0 defaults to the start; walking back stops at the beginning
        Assert.Equal(1, _lua.Call1("utf8.offset", "abc", 0).I);
        Assert.Equal(3, _lua.Call1("utf8.offset", "abc", 0, 3).I);
        Assert.Equal(1, _lua.Call1("utf8.offset", Bytes(0x80, 0x61), 0, 1).I);
        Assert.True(_lua.Call1("utf8.offset", "ab", -5).IsNil);
        Assert.True(_lua.Call1("utf8.offset", "ab", -3).IsNil);
        Assert.Equal(1, _lua.Call1("utf8.offset", Bytes(0x80, 0x80), -1).I);   // stops at the first byte
        Assert.Equal(1, _lua.Call1("utf8.offset", "ab", -2).I);
        Assert.Equal(3, _lua.Call1("utf8.offset", "ab", 3).I);
        Assert.True(_lua.Call1("utf8.offset", "ab", 4).IsNil);
        // codes() is strict unless asked otherwise
        Value sur = _lua.Call1("utf8.char", 0xD800);
        Value[] strict = _lua.Call("utf8.codes", sur);
        Assert.Contains("invalid UTF-8 code", _lua.ErrorOf(() => _lua.L.Call(strict[0], strict[1], strict[2])));
        Value[] lax = _lua.Call("utf8.codes", sur, true);
        Assert.Equal(0xD800, _lua.L.Call(lax[0], lax[1], lax[2])[1].I);
        Assert.Contains("invalid UTF-8 code", _lua.ErrorOf(() => _lua.Call("utf8.codes", Bytes(0x80))));
        // a truncated sequence at the end
        Value[] cut = _lua.Call("utf8.len", Bytes(0x61, 0xC3));
        Assert.True(cut[0].IsNil);
        Assert.Equal(2, cut[1].I);
        // overlong encodings are rejected
        Assert.True(_lua.Call1("utf8.len", Bytes(0xC0, 0x80)).IsNil);
        Assert.True(_lua.Call1("utf8.len", Bytes(0xE0, 0x80, 0x80)).IsNil);
        Assert.Equal(1, _lua.Call1("utf8.len", Bytes(0xC2, 0x80)).I);
    }
}
