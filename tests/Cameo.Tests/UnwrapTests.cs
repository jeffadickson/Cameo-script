using Cameo;
using CsCheck;
using Cameo.Runtime.Vm;
using Cameo.Tests.Properties;
using Xunit;

namespace Cameo.Tests;

/// <summary>Shared nil-only issue protocol, including ordinary Lua result-pack boundaries.</summary>
public class UnwrapTests
{
    private static ScriptEngine Engine(bool cameo, bool check = true) => cameo
        ? new CameoEngine(new CameoRuntimeOptions { TypeCheck = check }).Runtime
        : new ScriptEngine(new CameoRuntimeOptions { TypeCheck = check });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Checked_generic_inference_and_final_call_expansion(bool cameo)
    {
        using var e = Engine(cameo);
        var r = e.RunString(cameo ? """
            fn pair() -> (integer, string) { return 42, nil }
            fn number(n: integer) -> integer { return n + 1 }
            let n = unwrap(pair())
            let s = unwrap('text')
            let b = unwrap(false, nil, 'ignored')
            fn text(s: string) -> string { return s }
            fn boolean(b: boolean) -> boolean { return b }
            return number(unwrap(unwrap(pair()))), text(s), boolean(b), select('#', unwrap(nil))
            """ : """
            local function pair(): integer, string return 42,nil end
            local function number(n: integer): integer return n + 1 end
            local n = unwrap(pair())
            local s = unwrap('text')
            local b = unwrap(false, nil, 'ignored')
            local function text(s: string): string return s end
            local function boolean(b: boolean): boolean return b end
            return number(unwrap(unwrap(pair()))), text(s), boolean(b), select('#', unwrap(nil))
            """);
        Assert.Equal(43, r[0].I);
        Assert.Equal("text", r[1].AsString);
        Assert.False(r[2].IsTruthy);
        Assert.Equal(1, r[3].I);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Truncating_a_call_discards_its_issue_and_extra_results_are_ignored(bool cameo, bool check)
    {
        using var e = Engine(cameo, check);
        var r = e.RunString(cameo ? """
            fn pair() -> (integer, string) { return 7, 'issue' }
            let ok, issue = pcall(() -> integer => { return unwrap(pair()) })
            return ok, issue, unwrap((pair())), unwrap(pair(), nil), unwrap(8, nil, 'ignored'), select('#', unwrap(9, nil, 10))
            """ : """
            local function pair(): integer, string return 7,'issue' end
            local ok, issue = pcall(function(): integer return unwrap(pair()) end)
            return ok, issue, unwrap((pair())), unwrap(pair(), nil), unwrap(8,nil,'ignored'), select('#',unwrap(9,nil,10))
            """);
        Assert.False(r[0].IsTruthy);
        Assert.Equal("issue", r[1].AsString);
        Assert.Equal(7, r[2].I);
        Assert.Equal(7, r[3].I);
        Assert.Equal(8, r[4].I);
        Assert.Equal(1, r[5].I);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_values_identity_trace_and_engine_reuse(bool cameo)
    {
        using var e = Engine(cameo, false);
        var table = new Table();
        Value issue = Value.FromTable(table);
        e.State.Globals.SetStr("issue", issue);
        string failSrc = cameo
            ? "fn fail() { let x = unwrap(1, issue) return x } return fail()"
            : "local function fail() local x = unwrap(1,issue) return x end return fail()";
        var ex = Assert.Throws<LuaException>(() => e.RunString(failSrc, "unwrap-trace"));
        Assert.Same(table, ex.Value.O);
        Assert.Contains("unwrap-trace", ex.Traceback);
        string bytes = "\0\u0080\u00ff";
        e.State.Globals.SetStr("issue", Value.FromString(bytes));
        Assert.Equal(bytes, Assert.Throws<LuaException>(() => e.RunString("return unwrap(nil,issue)")).Value.AsString);
        string tblSrc = cameo
            ? "let t = {} return rawequal(t, unwrap(t)), unwrap(nil), unwrap(false), unwrap(17)"
            : "local t = {} return rawequal(t,unwrap(t)), unwrap(nil), unwrap(false), unwrap(17)";
        var r = e.RunString(tblSrc);
        Assert.True(r[0].IsTruthy);
        Assert.True(r[1].IsNil);
        Assert.False(r[2].IsTruthy);
        Assert.Equal(17, r[3].I);
        Assert.False(Assert.Throws<LuaException>(() => e.RunString("return unwrap(1,false)")).Value.IsTruthy);
        Assert.Contains("value expected", Assert.Throws<LuaException>(() => e.RunString("return unwrap()")).Message);
        // pcall uses status/value rather than value/issue: it is deliberately incompatible.
        string pcallSrc = cameo
            ? "return unwrap(pcall(() => 'answer'))"
            : "return unwrap(pcall(function() return 'answer' end))";
        Assert.Equal("answer", Assert.Throws<LuaException>(() => e.RunString(pcallSrc)).Value.AsString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Checked_no_argument_call_is_rejected(bool cameo)
    {
        using var e = Engine(cameo);
        Assert.IsType<CompileFailed>(e.TryRunString("return unwrap()").Value);
    }

    [Fact]
    public void CsCheck_nil_only_issue_model() => Prop.Cs(Gen.Int, CheckModel, iter: 120);

    [Fact]
    public void Hedgehog_nil_only_issue_model() => Prop.Hh(Hedgehog.Linq.Gen.Int32(Hedgehog.Linq.Range.LinearInt32(-1000,1000)), CheckModel, tests: 100);

    private static void CheckModel(int sample)
    {
        foreach (bool cameo in new[] { false, true })
        {
            using var e = Engine(cameo, false);
            Value[] values = [Value.Nil, Value.False, Value.True, Value.FromInt(sample), Value.FromString("\0\u00ff"), Value.FromTable(new Table())];
            Value first = values[(int)((uint)sample % (uint)values.Length)];
            Value issue = values[(int)(((uint)sample / 7) % (uint)values.Length)];
            e.State.Globals.SetStr("first", first);
            e.State.Globals.SetStr("issue", issue);
            if (issue.IsNil)
            {
                var r = e.RunString("return unwrap(first,issue,'ignored')");
                Assert.Single(r);
                Assert.True(first.RawEquals(r[0]));
            }
            else
                Assert.True(issue.RawEquals(Assert.Throws<LuaException>(() => e.RunString("return unwrap(first,issue)")).Value));
        }
    }
}
