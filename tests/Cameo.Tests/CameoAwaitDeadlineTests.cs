using Cameo;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Exercises the named deadline options through the Cameo entry point.</summary>
public class CameoAwaitDeadlineTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Deadline_can_cancel_the_owned_source(bool checking, bool cancel)
    {
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = checking });
        string option = cancel ? ", cancelOnTimeout: true" : "";
        var result = engine.RunString($$"""
            let source: Future<integer> = Future.new()
            let _, issue = source.await(timeout: 0{{option}})
            return issue.kind, source.isDone(), source.isCancelled(), source.complete(42)
            """);
        Assert.Equal("timeout", result[0].AsString);
        Assert.Equal(cancel, result[1].IsTruthy);
        Assert.Equal(cancel, result[2].IsTruthy);
        Assert.Equal(!cancel, result[3].IsTruthy);
    }

    [Fact]
    public void A_suspended_task_resumes_with_timeout_and_source_cancellation()
    {
        using var engine = new CameoEngine();
        var result = engine.RunString("""
            let source: Future<integer> = Future.new()
            fn waitForSource() -> string {
                let _, issue = source.await(timeout: 0, cancelOnTimeout: true)
                return issue.kind
            }
            return Future.supplyAsync(waitForSource).get(), source.isCancelled()
            """);
        Assert.Equal("timeout", result[0].AsString);
        Assert.True(result[1].IsTruthy);
    }

    [Fact]
    public void Teal_can_use_the_same_wait_with_positional_arguments()
    {
        using var engine = new ScriptEngine();
        var result = engine.RunString("""
            local source: Future<integer> = Future.new()
            local _, issue = source:await(0, true)
            return issue.kind, source:isCancelled()
            """);
        Assert.Equal("timeout", result[0].AsString);
        Assert.True(result[1].IsTruthy);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0 / 0")]
    [InlineData("1 / 0")]
    [InlineData("2147484")]
    public void Invalid_duration_fails_without_cancelling_the_source(string duration)
    {
        using var engine = new CameoEngine();
        var result = engine.RunString($$"""
            let source: Future<integer> = Future.new()
            fn invalidWait() {
                source.await(timeout: {{duration}}, cancelOnTimeout: true)
            }
            let ok, issue = pcall(invalidWait)
            return ok, tostring(issue), source.isDone()
            """);
        Assert.False(result[0].IsTruthy);
        Assert.Contains("timeout must be finite", result[1].AsString);
        Assert.False(result[2].IsTruthy);
    }

    [Fact]
    public void Unknown_second_label_gets_a_source_diagnostic()
    {
        using var engine = new CameoEngine();
        var issue = Assert.Throws<CameoCompileException>(() => engine.RunString("""
            let source: Future<integer> = Future.new()
            source.await(timeout: 0, cancel: true)
            """));
        Assert.Contains("expected 'cancelOnTimeout:'", issue.Message);
    }

    [Fact]
    public void A_settled_source_wins_even_at_zero_timeout()
    {
        using var engine = new CameoEngine();
        var result = engine.RunString("""
            let source = Future.completedFuture(42)
            let value = source.await(timeout: 0, cancelOnTimeout: true)
            return value, source.isCancelled()
            """);
        Assert.Equal(42, result[0].I);
        Assert.False(result[1].IsTruthy);
    }
}
