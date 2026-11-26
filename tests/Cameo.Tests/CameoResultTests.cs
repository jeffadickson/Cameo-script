using Cameo;
using Xunit;

namespace Cameo.Tests;

/// <summary>Tagged outcomes preserve payloads independently of their truthiness.</summary>
public sealed class CameoResultTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructors_preserve_nil_success_and_false_error(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            fn success() -> Result<nil, boolean> { return ok(nil) }
            fn failure() -> Result<nil, boolean> { return err(false) }
            let a = success()
            let b = failure()
            return a:isOk(), a:value(), b:isOk(), b:issue()
            """);
        Assert.True(values[0].IsTruthy);
        Assert.True(values[1].IsNil);
        Assert.False(values[2].IsTruthy);
        Assert.True(values[3].IsBoolean);
        Assert.False(values[3].IsTruthy);
    }

    [Fact]
    public void Checker_rejects_payload_of_the_wrong_type()
    {
        using var engine = new CameoEngine();
        var error = Assert.Throws<CameoCompileException>(() => engine.RunString("""
            fn wrong() -> Result<integer, string> { return err(42) }
            return wrong()
            """));
        Assert.Contains("result payload", error.Message);
    }

    [Fact]
    public void Wrong_branch_raises_and_payload_identity_is_preserved()
    {
        using var engine = new CameoEngine();
        var values = engine.RunString("""
            let original: {integer} = {42}
            let result: Result<integer, {integer}> = err(original)
            let same = rawequal(original, result:issue())
            fn readSuccess() -> integer { return result:value() }
            let succeeded, _ = pcall(readSuccess)
            return same, succeeded
            """);
        Assert.True(values[0].IsTruthy);
        Assert.False(values[1].IsTruthy);
    }

    [Fact]
    public void Teal_keeps_its_own_global_namespace()
    {
        using var engine = new ScriptEngine();
        var values = engine.RunString("""
            global ok: integer = 42
            return ok
            """);
        Assert.Equal(42, values[0].I);
        Assert.True(engine.State.GetGlobal("Result").IsNil);
        Assert.True(engine.State.GetGlobal("err").IsNil);
    }

    [Fact]
    public void Constructor_requires_a_result_context()
    {
        using var engine = new CameoEngine();
        var error = Assert.Throws<CameoCompileException>(() => engine.RunString("let pending = ok(42)"));
        Assert.Contains("requires an expected Result", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructor_takes_one_slot_from_a_result_pack(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            fn empty() {}
            fn pair() -> integer, integer { return 42, 99 }
            let missing: Result<nil, string> = ok(empty())
            let first: Result<integer, string> = ok(pair())
            return missing:isOk(), missing:value(), first:value()
            """);
        Assert.True(values[0].IsTruthy);
        Assert.True(values[1].IsNil);
        Assert.Equal(42, values[2].I);
    }
}
