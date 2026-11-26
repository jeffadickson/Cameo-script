using Cameo;
using CsCheck;
using Cameo.Runtime.Syntax;
using Xunit;
namespace Cameo.Tests;
/// <summary>Initialized Cameo bindings retain lexical semantics in checked and unchecked execution.</summary>
public class CameoLetTests
{
    [Fact]
    public void Bounded_generated_token_streams_have_only_defined_parse_outcomes()
    {
        string[] tokens = ["let", "value", "=", "(", ")", ",", "function", "end", "42", ".", "$", "{", "}", "\n"];
        var sources = Gen.String[Gen.Char['a', 'n'], 0, 40]
            .Select(text => string.Join(' ', text.Select(character => tokens[character - 'a'])));
        Cameo.Tests.Properties.Prop.Cs(sources, source =>
        {
            // This bounded token-input campaign uses maintained generation/shrinking.
            // Acceptance or a syntax diagnostic is valid; unrelated host faults are not.
            try { Parser.ParseCameo(source, "generated.cameo"); } catch (CameoSyntaxException) { }
            try { Parser.Parse(source, "generated.tl"); } catch (CameoSyntaxException) { }
        }, iter: 500);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Bindings_preserve_annotations_destructuring_capture_and_field_mutation(bool check)
    {
        using var e = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, e.RunString("let x, y: integer, integer = 20, 21 let box = {answer = 0} let f = function(): integer return x + y end box.answer = f() + 1 do let x = 1 end return box.answer")[0].I);
    }
    [Theory]
    [InlineData("let x = 1 x = 2")]
    [InlineData("let x = 1 x += 2")]
    [InlineData("let x = 1 local function f() x = 2 end")]
    [InlineData("let x = function() end function x() end")]
    public void Reassignment_is_rejected_even_without_checking(string source)
    {
        foreach (bool check in new[] { true, false }) { using var e = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check }); Assert.IsType<CompileFailed>(e.TryRunString(source).Value); }
    }
    [Theory]
    [InlineData("let x")] [InlineData("let x: integer")] [InlineData("let x =")] [InlineData("let x <const> = 1")] [InlineData("let x, = 1")]
    public void Initializer_is_required(string source) => Assert.Throws<CameoSyntaxException>(() => Parser.ParseCameo(source, "test.cameo"));
    [Fact]
    public void Let_identifiers_and_legacy_unchecked_constants_remain_usable()
    {
        const string source = "local let = function(x: integer): integer return x end local t = {let = let} return t.let(let(42))";
        using var c = new CameoEngine(); using var t = new ScriptEngine();
        Assert.Equal(42, c.RunString(source)[0].I); Assert.Equal(42, t.RunString(source)[0].I);
        using var uncheckedTeal = new ScriptEngine(new CameoRuntimeOptions { TypeCheck = false });
        Assert.Equal(2, uncheckedTeal.RunString("local x <const> = 1 x = 2 return x")[0].I);
        Assert.Throws<CameoSyntaxException>(() => Parser.Parse("let x = 1", "test.tl"));
    }
    [Fact]
    public void Randomized_captured_bindings_match_integer_addition()
    {
        Gen.Int[0, 1000].Sample(n => { using var e = new CameoEngine(); Assert.Equal(n + 1, e.RunString($"let n = {n} let f = function(): integer return n + 1 end return f()")[0].I); }, iter: 30, threads: 1);
    }
    [Theory]
    [InlineData(true, true)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public void Spliced_let_declarations_keep_immutability_and_do_not_modify_shared_identifiers(bool check, bool natural)
    {
        const string destructured = "local macro declare!(name: Expression, value: Expression) return ```let dummy, $name = 0, $value``` end ";
        const string simple = "local macro declare!(name: Expression, value: Expression) return ```let $name = $value``` end ";
        foreach (string declaration in new[] { natural ? simple : destructured })
        {
            using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
            Assert.Equal(1, engine.RunString(declaration + "declare!(x, 1) return x")[0].I);
            foreach (string mutation in new[] { "x = 2", "local function change() x = 2 end", "function x() end" })
                Assert.IsType<CompileFailed>(engine.TryRunString(declaration + "declare!(x, 1) " + mutation).Value);
        }
        // One spliced identifier serves a let declaration and a separate mutable local.
        // Declaration metadata must stay on the copied let name, not leak into the second splice.
        const string reused = "local macro twice!(name: Expression) return ```do let dummy, $name = 0, 1 end local $name = 1 $name = 42``` end twice!(x) return x";
        using var reusable = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        Assert.Equal(42, reusable.RunString(reused)[0].I);
    }}
