using Cameo;
using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Stable Cameo diagnostics offer repairs without weakening arrow restrictions.</summary>
public class CameoDiagnosticTests
{
    [Theory]
    [InlineData("local f = (name) => name", "CAMEO001", "name: <concrete type>")]
    [InlineData("local f = (name: any) => name", "CAMEO001", "name: <concrete type>")]
    [InlineData("local function pair(): integer, string return 1,'failure' end local f = () => pair()", "CAMEO002", "select or forward")]
    [InlineData("global raw: function local f = () => raw()", "CAMEO002", "known, fixed single result")]
    [InlineData("local f = () => (1 as integer)", "CAMEO004", "return expression as Type")]
    [InlineData("local f = (name: integer, name: integer) => name", "CAMEO005", "rename or remove")]
    [InlineData("local f = (...) => 1", "CAMEO005", "function(...)")]
    [InlineData("local macro id!(x: Expression) return `$x` end local f = () => 1 return id!(f())", "CAMEO003", "all expression functions")]
    [InlineData("local f = () => 1 local record R where self.n > 0 n: integer end", "CAMEO003", "all expression functions")]
    public void Rejected_forms_keep_ids_and_offer_specific_repairs(string source, string code, string repair)
    {
        using var cameo = new CameoEngine();
        var failure = Assert.IsType<CompileFailed>(cameo.TryRunString(source, "repair.cameo").Value);
        var diagnostic = Assert.Single(failure.Diagnostics, d => d.Message.Contains(code, StringComparison.Ordinal));
        Assert.Contains(repair, diagnostic.Message, StringComparison.Ordinal);
        using var teal = new ScriptEngine();
        Assert.IsType<CompileFailed>(teal.TryRunString(source).Value);
        Assert.Equal(7, cameo.RunString("return 7")[0].I);
    }

    [Theory]
    [InlineData("local f = (name: integer) => name return f(42)", "local function f(name: integer): integer return name end return f(42)")]
    [InlineData("local function apply(f: function(integer): integer): integer return f(42) end return apply((name) => name)", "local function apply(f: function(integer): integer): integer return f(42) end return apply(function(name: integer): integer return name end)")]
    [InlineData("local function f(): integer return 42 as integer end return f()", "local function f(): integer return 42 as integer end return f()")]
    [InlineData("local function f(...: integer): integer return ... end return f(42)", "local function f(...: integer): integer return ... end return f(42)")]
    [InlineData("local function pair(): integer, string return 42,'issue' end local function f(): integer, string return pair() end return f()", "local function pair(): integer, string return 42,'issue' end local function f(): integer, string return pair() end return f()")]
    [InlineData("local f = (left: integer, right: integer) => left + right return f(19,23)", "local function f(left: integer,right: integer): integer return left + right end return f(19,23)")]
    [InlineData("local macro id!(x: Expression) return `$x` end local function f(): integer return 42 end return id!(f())", "local macro id!(x: Expression) return `$x` end local function f(): integer return 42 end return id!(f())")]
    [InlineData("local function f(): integer return 42 end local record R where self.n > 0 n: integer end return f()", "local function f(): integer return 42 end local record R where self.n > 0 n: integer end return f()")]
    public void Repaired_forms_execute_in_their_supported_modes(string cameoSource, string tealSource)
    {
        using var cameo = new CameoEngine();
        using var teal = new ScriptEngine();
        Assert.Equal(42, cameo.RunString(cameoSource)[0].I);
        Assert.Equal(42, teal.RunString(tealSource)[0].I);
    }

    [Fact]
    public void Parameter_renaming_preserves_actionable_missing_context_diagnostic()
    {
        using var cameo = new CameoEngine();
        // Safe generated identifiers vary source positions and names without inventing types.
        Gen.Int[0, 10000].Sample(index =>
        {
            string name = $"parameter{index}";
            var errors = cameo.Check($"local f = ({name}) => {name}", "names.cameo");
            var error = Assert.Single(errors, d => d.Message.Contains("CAMEO001", StringComparison.Ordinal));
            Assert.Contains($"{name}: <concrete type>", error.Message, StringComparison.Ordinal);
        }, iter: 60, threads: 1, seed: Properties.Prop.Seeds.CsCheckSeed);
    }
}
