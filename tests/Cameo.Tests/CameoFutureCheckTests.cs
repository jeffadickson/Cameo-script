using Cameo;
using Cameo.Runtime.Syntax;
using Xunit;
namespace Cameo.Tests;
/// <summary>Only built-in Future instances receive implicit self at direct Cameo dot calls.</summary>
public class CameoFutureCheckTests
{
    [Theory]
    [InlineData("local result: integer = Future.completedFuture(21).thenApply((n) => n * 2).get()")]
    [InlineData("local job = Future.completedFuture(21) local result: integer = job.thenApply((n) => n * 2).get()")]
    [InlineData("local F = Future local job = F.completedFuture(42) local result: integer = F.get(job)")]
    [InlineData("local job = Future.completedFuture(42) local f = job.get local result: integer = f(job)")]
    [InlineData("local job = Future.completedFuture(42) local result: integer = (job.get)(job)")]
    [InlineData("local a = Future.completedFuture(20) local b = Future.completedFuture(22) local result: integer = a.thenCombine(b, (x, y) => x + y).get()")]
    public void Instance_methods_and_explicit_static_aliases_check(string source)
    {
        using var e = new CameoEngine(); Assert.Empty(e.Check(source));
    }
    [Fact]
    public void Counterfeit_future_does_not_gain_implicit_self()
    {
        using var e = new CameoEngine();
        Assert.NotEmpty(e.Check("local record Future get: function(self): integer end local fake: Future = {} local result = fake.get()"));
    }
    [Theory]
    [InlineData("return job.get()", true)] [InlineData("return job.get 'x'", true)] [InlineData("return job.get {}", true)]
    [InlineData("return (job.get)()", false)] [InlineData("return job['get']()", false)] [InlineData("return method()", false)]
    public void Only_source_direct_dot_calls_are_marked(string source, bool marked)
    {
        var chunk = Parser.ParseCameo(source, "test.cameo");
        var statement = Assert.IsType<ReturnStat>(Assert.Single(chunk.Main.Body.Stats));
        var call = Assert.IsType<CallExpr>(Assert.Single(statement.Values));
        Assert.Equal(marked, call.CameoDotCall);
        var teal = Parser.Parse(source, "test.tl");
        Assert.False(Assert.IsType<CallExpr>(Assert.Single(Assert.IsType<ReturnStat>(Assert.Single(teal.Main.Body.Stats)).Values)).CameoDotCall);
    }
}
