using Cameo;
using Xunit;

namespace Cameo.Tests;

/// <summary>Cameo using declarations share lexical close semantics with Teal.</summary>
public sealed class CameoUsingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Using_closes_on_normal_scope_exit(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var values = engine.RunString("""
            let output = io.tmpfile()
            do {
                using resource = output
                assert(io.type(resource) == "file")
            }
            return io.type(output)
            """);
        Assert.Equal("closed file", values[0].AsString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Using_cannot_be_reassigned(bool check)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = check });
        var diagnostics = engine.Check("""
            using resource = io.tmpfile()
            resource = nil
            """);
        if (check)
            Assert.Contains(diagnostics, diagnostic => !diagnostic.IsWarning && diagnostic.Message.Contains("assign", StringComparison.Ordinal));
        else
            Assert.Throws<CameoCompileException>(() => engine.Compile("""
                using resource = io.tmpfile()
                resource = nil
                """));
    }

    [Fact]
    public void Using_closes_before_propagating_the_original_tagged_error()
    {
        using var engine = new CameoEngine();
        var values = engine.RunString("""
            fn fail() -> Result<integer, string> { return err("unavailable") }
            let output = io.tmpfile()
            fn work() -> Result<integer, string> {
                using resource = output
                assert(io.type(resource) == "file")
                let value = try fail()
                return ok(value)
            }
            let result = work()
            return result:issue(), io.type(output)
            """);
        Assert.Equal("unavailable", values[0].AsString);
        Assert.Equal("closed file", values[1].AsString);
    }
}
