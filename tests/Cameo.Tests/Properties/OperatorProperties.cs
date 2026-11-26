using System.Globalization;
using Hedgehog;
using Hedgehog.Linq;
using Cameo.Runtime.Vm;
using Xunit;
using Gen = Hedgehog.Linq.Gen;
using Range = Hedgehog.Linq.Range;

namespace Cameo.Tests.Properties;

/// <summary>
/// Operator semantics outside the plain number paths (Hedgehog): string-to-number coercion
/// in arithmetic, metamethod dispatch, and the error raised for every unsupported combination.
/// </summary>
public class OperatorProperties
{
    private readonly LuaState L = new();

    private static readonly OpCode[] Arithmetic = { OpCode.Add, OpCode.Sub, OpCode.Mul, OpCode.Div, OpCode.Mod, OpCode.Pow, OpCode.IDiv };
    private static readonly OpCode[] Bitwise = { OpCode.BAnd, OpCode.BOr, OpCode.BXor, OpCode.Shl, OpCode.Shr };

    private static readonly Gen<long> GenInt = Gen.Frequency(
        (4, Gen.Int64(Range.LinearFromInt64(0, -1000, 1000))),
        (1, Gen.Int64(Range.ConstantBoundedInt64())));

    private static readonly Gen<double> GenFloat = Gen.Frequency(
        (4, Gen.Double(Range.LinearFromDouble(0, -1000, 1000))),
        (1, Gen.Item(0.5, -2.5, 1e100, 3.0)));

    private static readonly Gen<Value> GenNumber = Gen.Choice(GenInt.Select(i => Value.FromInt(i)), GenFloat.Select(d => Value.FromFloat(d)));

    private static bool Same(Value a, Value b) => a.Kind == b.Kind && (a.IsFloat ? a.N.Equals(b.N) : a.I == b.I);

    /// <summary>The numeral as a program would write it, possibly padded with spaces.</summary>
    private static Value AsString(Value n, bool pad)
    {
        string s = n.IsInteger ? n.I.ToString(CultureInfo.InvariantCulture) : n.N.ToString("R", CultureInfo.InvariantCulture);
        if (n.IsFloat && !s.Contains('.') && !s.Contains('E')) s += ".0";
        return Value.FromString(pad ? "  " + s + " " : s);
    }

    [Fact]
    public void Numeric_strings_behave_like_their_numbers_in_arithmetic()
    {
        var gen = from a in GenNumber from b in GenNumber from op in Gen.Item(Arithmetic) from pad in Gen.Item(false, true) from which in Gen.Int32(Range.LinearInt32(0, 2))
                  select (a, b, op, pad, which);
        Prop.Hh(gen, x =>
        {
            var (a, b, op, pad, which) = x;
            bool zeroDivide = b.IsInteger && b.I == 0 && a.IsInteger && op is OpCode.Mod or OpCode.IDiv && b.IsInteger && b.I == 0 && a.IsInteger;
            Value sa = which != 1 ? AsString(a, pad) : a, sb = which != 0 ? AsString(b, pad) : b;
            if (zeroDivide)
            {
                Assert.Throws<LuaException>(() => L.Arith(op, a, b));
                Assert.Throws<LuaException>(() => L.Arith(op, sa, sb));
                return;
            }
            Value expected = L.Arith(op, a, b);
            Assert.True(Same(expected, L.Arith(op, sa, sb)), $"{op} {sa} {sb}: expected {expected}, got {L.Arith(op, sa, sb)}");
            // unary minus coerces too
            Assert.True(Same(L.Unm(a), L.Unm(AsString(a, pad))));
        }, 600);
    }

    [Fact]
    public void Strings_are_not_coerced_by_bitwise_operators_or_comparisons()
    {
        var gen = from a in GenInt from b in GenInt from op in Gen.Item(Bitwise) select (a, b, op);
        Prop.Hh(gen, x =>
        {
            var (a, b, op) = x;
            Value sa = AsString(Value.FromInt(a), false);
            var e = Assert.Throws<LuaException>(() => L.Arith(op, sa, Value.FromInt(b)));
            Assert.Equal("attempt to perform bitwise operation on a string value", e.Message);
            e = Assert.Throws<LuaException>(() => L.Arith(op, Value.FromInt(b), sa));
            Assert.Equal("attempt to perform bitwise operation on a string value", e.Message);
            Assert.Equal("attempt to perform bitwise operation on a string value", Assert.Throws<LuaException>(() => L.BNot(sa)).Message);
            // "10" == 10 is false, and ordering a string against a number is an error
            Assert.False(L.Equals(sa, Value.FromInt(a)));
            Assert.Equal("attempt to compare string with number", Assert.Throws<LuaException>(() => L.LessThan(sa, Value.FromInt(b))).Message);
            Assert.Equal("attempt to compare number with string", Assert.Throws<LuaException>(() => L.LessEqual(Value.FromInt(b), sa)).Message);
            Assert.Equal("attempt to compare string with number", Assert.Throws<LuaException>(() => L.LessEqual(sa, Value.FromInt(b))).Message);
            Assert.Equal("attempt to compare number with string", Assert.Throws<LuaException>(() => L.LessThan(Value.FromFloat(b), sa)).Message);
            // concatenation converts numbers to text
            Assert.Equal(a.ToString(CultureInfo.InvariantCulture) + b.ToString(CultureInfo.InvariantCulture), L.Concat2(Value.FromInt(a), Value.FromInt(b)).AsString);
            Assert.Equal(sa.AsString + "1.5", L.Concat2(sa, Value.FromFloat(1.5)).AsString);
        }, 200);
    }

    [Fact]
    public void Bitwise_operators_accept_integral_floats_only()
    {
        var gen = from a in GenInt from b in Gen.Int64(Range.LinearFromInt64(0, -100000, 100000)) from frac in Gen.Item(0.5, 0.25, -0.75) from op in Gen.Item(Bitwise) select (a, b, frac, op);
        Prop.Hh(gen, x =>
        {
            var (a, b, frac, op) = x;
            Value expected = L.Arith(op, Value.FromInt(a), Value.FromInt(b));
            Assert.True(Same(expected, L.Arith(op, Value.FromInt(a), Value.FromFloat(b))));
            Assert.Equal(~b, L.BNot(Value.FromFloat(b)).I);
            Assert.Equal("number has no integer representation", Assert.Throws<LuaException>(() => L.Arith(op, Value.FromInt(a), Value.FromFloat(b + frac))).Message);
            Assert.Equal("number has no integer representation", Assert.Throws<LuaException>(() => L.Arith(op, Value.FromFloat(b + frac), Value.FromInt(a))).Message);
            Assert.Equal("number has no integer representation", Assert.Throws<LuaException>(() => L.BNot(Value.FromFloat(b + frac))).Message);
            Assert.Equal("number has no integer representation", Assert.Throws<LuaException>(() => L.Arith(op, Value.FromFloat(1e300), Value.FromInt(a))).Message);
        }, 200);
    }

    // ------------------------------------------------------------------ metamethods

    private static readonly string[] Events =
    {
        "__add", "__sub", "__mul", "__div", "__mod", "__pow", "__idiv", "__band", "__bor", "__bxor", "__shl", "__shr",
    };

    private Value WithMeta(string ev, string tag, List<string> log)
    {
        var mt = new Table();
        mt.SetStr(ev, Value.FromFunction((ref CallContext c) =>
        {
            log.Add(tag + ":" + c[0].TypeName + "," + c[1].TypeName);
            return c.Return(tag);
        }, ev));
        return new Table { Metatable = mt };
    }

    [Fact]
    public void Binary_metamethods_are_taken_from_the_left_operand_first()
    {
        var gen = from i in Gen.Int32(Range.LinearInt32(0, Events.Length - 1)) from other in Gen.Choice(GenNumber, Gen.Item("x", "12").Select(s => Value.FromString(s)), Gen.Constant(Value.Nil), Gen.Constant(Value.True)) select (i, other);
        Prop.Hh(gen, x =>
        {
            var (i, other) = x;
            OpCode op = (OpCode)((int)OpCode.Add + i);
            var log = new List<string>();
            Value left = WithMeta(Events[i], "L", log), right = WithMeta(Events[i], "R", log);
            Assert.Equal("L", L.Arith(op, left, right).AsString);
            Assert.Equal("L", L.Arith(op, left, other).AsString);
            Assert.Equal("R", L.Arith(op, other, right).AsString);
            Assert.Equal(new[] { "L:table,table", "L:table," + other.TypeName, "R:" + other.TypeName + ",table" }, log);
            // a table without that metamethod falls through to the other operand's
            Value plain = new Table { Metatable = new Table() };
            Assert.Equal("R", L.Arith(op, plain, right).AsString);
            // and with none at all it is an error naming the table
            string expected = i >= 7 ? "attempt to perform bitwise operation on a table value" : "attempt to perform arithmetic on a table value";
            if (!other.IsString)
            {
                Assert.Equal(expected, Assert.Throws<LuaException>(() => L.Arith(op, plain, other.IsNil || other.IsBoolean ? Value.FromInt(1) : other)).Message);
                Assert.Equal(expected, Assert.Throws<LuaException>(() => L.Arith(op, other.IsNil || other.IsBoolean ? Value.FromInt(1) : other, plain)).Message);
            }
        }, 300);
    }

    [Fact]
    public void Unary_length_concat_and_comparison_metamethods()
    {
        var log = new List<string>();
        Value Meta(params string[] events)
        {
            var mt = new Table();
            foreach (string ev in events)
            {
                string name = ev;
                mt.SetStr(ev, Value.FromFunction((ref CallContext c) =>
                {
                    log.Add(name + "/" + c.Count);
                    return name is "__eq" or "__lt" or "__le" ? c.Return(Value.FromInt(0)) : c.Return(name);   // 0 is truthy
                }, ev));
            }
            return new Table { Metatable = mt };
        }
        Value t = Meta("__unm", "__bnot", "__len", "__concat", "__eq", "__lt", "__le"), u = Meta("__eq", "__lt", "__le", "__concat");
        Assert.Equal("__unm", L.Unm(t).AsString);
        Assert.Equal("__bnot", L.BNot(t).AsString);
        Assert.Equal("__len", L.Len(t).AsString);
        Assert.Equal("__concat", L.Concat2(t, "s").AsString);
        Assert.Equal("__concat", L.Concat2(1, u).AsString);
        Assert.True(L.Equals(t, u));           // result is converted to a boolean
        Assert.True(L.Equals(t, t));           // identical values never call __eq
        Assert.True(L.LessThan(t, u));
        Assert.True(L.LessEqual(t, 5));
        Assert.True(L.LessThan(5, u));
        Assert.Equal(new[] { "__unm/2", "__bnot/2", "__len/1", "__concat/2", "__concat/2", "__eq/2", "__lt/2", "__le/2", "__lt/2" }, log);
        // __eq is only tried for two tables (or two userdata), never across kinds
        Assert.False(L.Equals(t, 5));
        Assert.False(L.Equals("x", t));
        // __eq may come from the right operand, and works for userdata as well
        log.Clear();
        Value noEq = new Table { Metatable = new Table() };
        Assert.True(L.Equals(noEq, u));
        Assert.False(L.Equals(noEq, new Table { Metatable = new Table() }));
        Assert.False(L.Equals(noEq, new Table()));
        Assert.Equal(new[] { "__eq/2" }, log);
        var ud1 = new Handle { Metatable = u.AsTable.Metatable };
        var ud2 = new Handle();
        Assert.True(L.Equals(Value.FromUserdata(ud1), Value.FromUserdata(ud2)));
        Assert.False(L.Equals(Value.FromUserdata(ud2), Value.FromUserdata(new Handle())));
        Assert.False(L.Equals(Value.FromUserdata(ud1), u));       // userdata vs table: different kinds
        Assert.Equal("Handle", LuaState.TypeNameOf(Value.FromUserdata(ud2)));
        Assert.Equal(u.AsTable.Metatable, L.GetMetatable(Value.FromUserdata(ud1)));
        Assert.Null(L.GetMetatable(Value.FromInt(1)));
        Assert.Same(L.StringMeta, L.GetMetatable("s"));
        // the plain cases
        Assert.Equal(3, L.Len("abc").I);
        Assert.Equal(2, L.Len(new Table { [1] = 1, [2] = 2 }).I);
        Assert.Equal(-5, L.Unm(5).I);
        Assert.Equal(-1.5, L.Unm(1.5).N);
        Assert.Equal(-2, L.Unm("2").I);
        Assert.Equal(-6, L.BNot(5).I);
    }

    private sealed class Handle : IUserdata
    {
        public string TypeName => "Handle";
        public Table? Metatable { get; set; }
    }

    [Fact]
    public void Operators_report_the_operand_they_cannot_handle()
    {
        Value tbl = new Table(), nil = Value.Nil;
        string Msg(Action a) => Assert.Throws<LuaException>(a).Message;
        Assert.Equal("attempt to perform arithmetic on a nil value", Msg(() => L.Arith(OpCode.Add, nil, 1)));
        Assert.Equal("attempt to perform arithmetic on a nil value", Msg(() => L.Arith(OpCode.Add, 1, nil)));
        Assert.Equal("attempt to perform arithmetic on a boolean value", Msg(() => L.Arith(OpCode.Mul, 1.5, Value.True)));
        Assert.Equal("attempt to sub a 'string' with a 'table'", Msg(() => L.Arith(OpCode.Sub, "10", tbl)));
        Assert.Equal("attempt to add a 'nil' with a 'string'", Msg(() => L.Arith(OpCode.Add, nil, "10")));
        Assert.Equal("attempt to div a 'string' with a 'boolean'", Msg(() => L.Arith(OpCode.Div, "x", Value.True)));
        Assert.Equal("attempt to add a 'string' with a 'number'", Msg(() => L.Arith(OpCode.Add, "a", 1)));
        Assert.Equal("attempt to mul a 'number' with a 'string'", Msg(() => L.Arith(OpCode.Mul, 1, "a")));
        Assert.Equal("attempt to pow a 'string' with a 'string'", Msg(() => L.Arith(OpCode.Pow, "a", "b")));
        Assert.Equal("attempt to perform arithmetic on a nil value", Msg(() => L.Unm(nil)));
        Assert.Equal("attempt to perform arithmetic on a string value", Msg(() => L.Unm("x")));
        Assert.Equal("attempt to perform bitwise operation on a nil value", Msg(() => L.Arith(OpCode.BAnd, nil, 1)));
        Assert.Equal("attempt to perform bitwise operation on a boolean value", Msg(() => L.Arith(OpCode.Shl, 1, Value.False)));
        Assert.Equal("attempt to perform bitwise operation on a table value", Msg(() => L.BNot(tbl)));
        Assert.Equal("attempt to get length of a number value", Msg(() => L.Len(5)));
        Assert.Equal("attempt to get length of a nil value", Msg(() => L.Len(nil)));
        Assert.Equal("attempt to concatenate a table value", Msg(() => L.Concat2("a", tbl)));
        Assert.Equal("attempt to concatenate a nil value", Msg(() => L.Concat2(nil, "a")));
        Assert.Equal("attempt to concatenate a boolean value", Msg(() => L.Concat2(1, Value.True)));
        Assert.Equal("attempt to compare two table values", Msg(() => L.LessThan(tbl, new Table())));
        Assert.Equal("attempt to compare two nil values", Msg(() => L.LessEqual(nil, nil)));
        Assert.Equal("attempt to compare nil with number", Msg(() => L.LessThan(nil, 1)));
        Assert.Equal("attempt to compare number with nil", Msg(() => L.LessEqual(1, nil)));
        Assert.Equal("attempt to compare table with number", Msg(() => L.LessThan(tbl, 1.5)));
        Assert.Equal("attempt to compare boolean with string", Msg(() => L.LessThan(Value.True, "a")));
        Assert.Equal("attempt to divide by zero", Msg(() => L.Arith(OpCode.IDiv, 1, 0)));
        Assert.Equal("attempt to perform 'n%0'", Msg(() => L.Arith(OpCode.Mod, 1, 0)));
        // float division and modulo by zero are not errors
        Assert.True(double.IsPositiveInfinity(L.Arith(OpCode.IDiv, 1.0, 0).N));
        Assert.True(double.IsNaN(L.Arith(OpCode.Mod, 1, 0.0).N));
        Assert.Equal(long.MinValue, L.Arith(OpCode.IDiv, long.MinValue, -1).I);
        Assert.Equal(0, L.Arith(OpCode.Mod, long.MinValue, -1).I);
        // a metatable name is used in messages
        Value named = new Table { Metatable = new Table { ["__name"] = "Point" } };
        Assert.Equal("attempt to perform arithmetic on a Point value", Msg(() => L.Arith(OpCode.Add, named, 1)));
        Assert.Equal("attempt to compare two Point values", Msg(() => L.LessThan(named, named)));
        Assert.Equal("Point", LuaState.TypeNameOf(named));
    }
}
