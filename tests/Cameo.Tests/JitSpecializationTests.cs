using CsCheck;
using Cameo.Jit;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Checks optimizer decisions as well as observable integer/float semantics.
/// A conversion of an integer argument to double must fail these tests above 2^53.</summary>
public class JitSpecializationTests
{
    [Fact]
    public void Checker_numeric_hints_reach_prototypes_and_primitive_locals()
    {
        using var engine = new ScriptEngine();
        Proto p = engine.Compile("local function f(x: integer, y: number): number local a = x * x return a + y end", "numeric").Protos[0];
        Assert.Equal(new ValueKind?[] { ValueKind.Integer, ValueKind.Float }, p.NumericParameters);
        ValueKind?[] kinds = NumericAnalysis.Analyze(p);
        Assert.Equal(ValueKind.Integer, kinds[0]);
        Assert.Equal(ValueKind.Float, kinds[1]);
        Assert.Contains(ValueKind.Integer, kinds);
        Assert.Contains(ValueKind.Float, kinds);
    }

    [Fact]
    public void Entry_guards_preserve_dynamic_arguments_and_run_side_effects_once()
    {
        using var engine = new ScriptEngine();
        JitLibrary.Enable(engine);
        engine.RunString("""
            global calls = 0
            global function f(x: integer, y: number): number
                calls = calls + 1
                return x + y
            end
            """);
        Value original = engine.State.Globals.GetStr("f");
        Assert.True(JitLibrary.TryCompile(original, out Value fast, out _));
        engine.State.Globals.SetStr("fast", fast);
        engine.Options.TypeCheck = false;
        Value[] result = engine.RunString("""
            local mt = {__add = function(a,b) return 73 end}
            return fast(4, 0.5), fast(9007199254740993, 2), fast(1, '2'), fast(setmetatable({}, mt), 0.5), calls
            """);
        Assert.Equal(4.5, result[0].N);
        Assert.Equal(ValueKind.Integer, result[1].Kind);
        Assert.Equal(9007199254740995, result[1].I);
        Assert.True(result[2].TryToDouble(out double coerced) && coerced == 3);
        Assert.Equal(73, result[3].I);
        Assert.Equal(4, result[4].I);
    }

    [Theory]
    [InlineData("local x if flag then x = 3 end return x", "return f(false), f(true)")]
    [InlineData("local x = 1 if flag then x = 'text' else x = 2.5 end return x", "return f(false), f(true)")]
    [InlineData("local x = 2 do local y = 'text' x = y end return x", "return f(false), f(true)")]
    [InlineData("local x = 0 for i = 1, 4 do x = x + i end return x", "return f(false), f(true)")]
    public void Register_joins_and_reuse_preserve_values(string body, string call)
    {
        Value[] Run(bool compiled)
        {
            using var e = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
            JitLibrary.Enable(e);
            return e.RunString("function f(flag) " + body + " end\n" + (compiled ? "f=assert(jit.compile(f))\n" : "") + call);
        }
        Assert.Equal(Run(false), Run(true));
    }

    [Fact]
    public void Guarded_integer_arithmetic_matches_unchecked_reference()
    {
        using var engine = new ScriptEngine();
        JitLibrary.Enable(engine);
        engine.RunString("global function f(a: integer, b: integer): integer return (a + b) * (a - b) end");
        Value original = engine.State.Globals.GetStr("f");
        Assert.True(JitLibrary.TryCompile(original, out Value fast, out _));
        // Fixed replay seed: the independent reference uses CLR unchecked Int64 operations.
        Gen.Select(Gen.Long, Gen.Long).Sample(pair =>
        {
            Value actual = engine.State.Call(fast, [Value.FromInt(pair.Item1), Value.FromInt(pair.Item2)])[0];
            Assert.Equal(ValueKind.Integer, actual.Kind);
            Assert.Equal(unchecked((pair.Item1 + pair.Item2) * (pair.Item1 - pair.Item2)), actual.I);
        }, iter: 500, threads: 1, seed: Properties.Prop.Seeds.CsCheckSeed);
    }

    [Fact]
    public void Guarded_float_comparisons_preserve_nan_and_signed_zero()
    {
        using var engine = new ScriptEngine();
        JitLibrary.Enable(engine);
        engine.RunString("global function f(a: number, b: number): boolean, boolean, boolean, number return a < b, a <= b, a == b, a / b end");
        Value original = engine.State.Globals.GetStr("f");
        Assert.True(JitLibrary.TryCompile(original, out Value fast, out _));
        foreach (double a in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, 0.0, double.Epsilon, -2.5 })
        foreach (double b in new[] { double.NaN, double.PositiveInfinity, -0.0, 0.0, 1.0 })
        {
            Value[] values = engine.State.Call(fast, [Value.FromFloat(a), Value.FromFloat(b)]);
            Assert.Equal(a < b, values[0].IsTruthy);
            Assert.Equal(a <= b, values[1].IsTruthy);
            Assert.Equal(a == b, values[2].IsTruthy);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a / b), BitConverter.DoubleToInt64Bits(values[3].N));
        }
    }

    [Fact]
    public void Missing_arguments_use_generic_errors_and_do_not_poison_later_calls()
    {
        using var engine = new ScriptEngine();
        JitLibrary.Enable(engine);
        engine.RunString("global function f(a: integer): integer return a + 1 end");
        Assert.True(JitLibrary.TryCompile(engine.State.Globals.GetStr("f"), out Value fast, out _));
        Assert.Throws<LuaException>(() => engine.State.Call(fast, []));
        Assert.Equal(42, engine.State.Call(fast, [Value.FromInt(41)])[0].I);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compiled_guards_are_a_snapshot_of_checker_hints(bool replaceArray)
    {
        using var engine = new ScriptEngine();
        engine.RunString("global function f(a: integer): integer return a + 1 end");
        Value original = engine.State.Globals.GetStr("f");
        var proto = Assert.IsType<Closure>(original.O).Proto;
        Assert.True(JitLibrary.TryCompile(original, out Value fast, out _));
        // Hosts may retain and edit this public prototype. Such edits must not weaken
        // guards protecting already emitted unchecked primitive loads.
        if (replaceArray) proto.NumericParameters = [];
        else proto.NumericParameters[0] = null;
        Value actual = engine.State.Call(fast, [Value.FromFloat(1.5)])[0];
        Assert.Equal(ValueKind.Float, actual.Kind);
        Assert.Equal(2.5, actual.N);
    }

    [Fact]
    public void Unhinted_parameters_do_not_hide_later_numeric_guards()
    {
        using var engine = new ScriptEngine();
        engine.RunString("global function f(a: any, b: integer): any, number return a, b + 1 end");
        Assert.True(JitLibrary.TryCompile(engine.State.Globals.GetStr("f"), out Value fast, out _));
        Value[] actual = engine.State.Call(fast, [Value.FromString("kept"), Value.FromFloat(1.5)]);
        Assert.Equal("kept", actual[0].ToText());
        Assert.Equal(ValueKind.Float, actual[1].Kind);
        Assert.Equal(2.5, actual[1].N);
    }
}
