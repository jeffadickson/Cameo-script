using Cameo;
using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>New syntax is confined to Cameo, preserves captures and checks source arity.</summary>
public class CameoSyntaxTests
{
    [Theory]
    [InlineData("let f = () => 42 return f()", 42)]
    [InlineData("let f = (x: integer, y: integer) => x + y return f(19, 23)", 42)]
    [InlineData("fn apply(f: fn(integer) -> integer) -> integer { return f(21) } return apply((n) => n * 2)", 42)]
    [InlineData("return Query.from({1, 2, 3, 4}).where((n) => n % 2 == 0).select((n) => n * 10).sum()", 60)]
    [InlineData("var n: integer = 1 let f = () => n n = 42 return f()", 42)]
    [InlineData("let outer = (x: integer) => (y: integer) => x + y return outer(20)(22)", 42)]
    [InlineData("return ((x: integer) => x + 2)(40)", 42)]
    [InlineData("return select('#', (() => nil)())", 1)]
    public void Expression_functions_execute_with_context_or_typed_parameters(string source, long expected)
    {
        using var cameo = new CameoEngine();
        Assert.Equal(expected, cameo.RunString(source)[0].I);
        using var teal = new ScriptEngine();
        Assert.IsType<CompileFailed>(teal.TryRunString(source).Value);
    }

    [Fact]
    public void Interpolation_subparser_and_utf8_literals_keep_the_language()
    {
        using var engine = new CameoEngine();
        Assert.Equal("héllo 42", engine.RunString("return $\"héllo {((x: integer) => x + 1)(41)}\"")[0].ToText());
    }

    [Fact]
    public void Arrow_text_is_ordinary_inside_comments_and_strings()
    {
        const string cameoSource = "// (x) => x\nreturn '() => 42', (2 + 3) * 4";
        const string tealSource = "-- (x) => x\nreturn '() => 42', (2 + 3) * 4";
        using var engine = new CameoEngine();
        using var teal = new ScriptEngine();
        Assert.Equal(teal.RunString(tealSource)[0].ToText(), engine.RunString(cameoSource)[0].ToText());
        Assert.Equal(20, engine.RunString(cameoSource)[1].I);
    }

    [Theory]
    [InlineData("let f = (x) => x + 1", "CAMEO001")]
    [InlineData("let f = (x: any) => x", "CAMEO001")]
    [InlineData("fn pair() -> (integer, string) { return 1, 'failure' } let f = () => pair()", "CAMEO002")]
    [InlineData("fn pair() -> (integer, string) { return 1, 'failure' } let f = () => ((pair()))", "CAMEO002")]
    [InlineData("fn none() { } let f = () => none()", "CAMEO002")]
    [InlineData("fn many() -> integer... { return 1, 2 } let f = () => many()", "CAMEO002")]
    [InlineData("global raw: function let f = () => raw()", "CAMEO002")]
    [InlineData("fn pair() -> (integer, string) { return 1, 'failure' } let f = () => (pair() as integer)", "CAMEO004")]
    [InlineData("let f = (x: integer, x: integer) => x", "CAMEO005")]
    [InlineData("record R where ((x: integer) => x)(1) > 0 { n: integer }", "CAMEO003")]
    public void Unsafe_or_unsupported_forms_report_a_cameo_diagnostic(string source, string code)
    {
        using var engine = new CameoEngine();
        var failure = Assert.IsType<CompileFailed>(engine.TryRunString(source, "bad.cameo").Value);
        Assert.Contains(failure.Diagnostics, d => d.Message.Contains(code, StringComparison.Ordinal));
        Assert.Equal(7, engine.RunString("return 7")[0].I);
    }

    [Theory]
    [InlineData("let f = x => x")]
    [InlineData("let f = (x: integer,) => x")]
    [InlineData("let f = (...) => 1")]
    [InlineData("let f = () =>")]
    [InlineData("let f = () => do { return 1 }")]
    [InlineData("let f = () = > 1")]
    public void Malformed_expression_functions_fail_without_running(string source)
    {
        using var engine = new CameoEngine();
        Assert.IsType<CompileFailed>(engine.TryRunString(source).Value);
    }

    [Fact]
    public void Missing_context_preserves_parameter_source_position()
    {
        using var engine = new CameoEngine();
        var errors = engine.Check("// héllo\nreturn (x) => x", "position.cameo");
        var error = Assert.Single(errors, d => d.Message.Contains("CAMEO001"));
        Assert.Equal("position.cameo", error.File);
        Assert.Equal(2, error.Line);
        Assert.Equal(9, error.Col);
    }

    [Fact]
    public void Deep_or_unclosed_parentheses_terminate_with_compile_failure()
    {
        using var engine = new CameoEngine();
        foreach (string source in new[] {new string('(', 3000)+"1"+new string(')',3000), new string('(',3000)+"x"})
            Assert.IsType<CompileFailed>(engine.TryRunString(source).Value);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("*")]
    public void Seeded_arithmetic_agrees_with_ordinary_functions_and_clr(string operation)
    {
        using var cameo = new CameoEngine();
        using var teal = new ScriptEngine();
        Value actual = cameo.RunString($"return (x: integer, y: integer) => x {operation} y")[0];
        Value reference = teal.RunString($"return function(x: integer,y: integer): integer return x {operation} y end")[0];
        Gen.Select(Gen.Long,Gen.Long).Sample(pair =>
        {
            long a=pair.Item1,b=pair.Item2;
            long expected=operation switch {"+"=>unchecked(a+b),"-"=>unchecked(a-b),_=>unchecked(a*b)};
            Assert.Equal(expected,cameo.State.Call(actual,Value.FromInt(a),Value.FromInt(b))[0].I);
            Assert.Equal(expected,teal.State.Call(reference,Value.FromInt(a),Value.FromInt(b))[0].I);
        },iter:300,threads:1,seed:Properties.Prop.Seeds.CsCheckSeed);
    }

    [Fact]
    public void Seeded_malformed_input_checking_never_executes_source()
    {
        using var engine = new CameoEngine();
        Gen.String[Gen.Char[(char)0,(char)255],0,100].Sample(source =>
        {
            _=engine.Check(source,"fuzz.cameo");
        },iter:200,threads:1,seed:Properties.Prop.Seeds.CsCheckSeed);
    }
}
