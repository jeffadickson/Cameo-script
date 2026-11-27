using Cameo;
using Cameo.Jit;
using Xunit;

namespace Cameo.Tests;

public class CameoIntegrationTests
{
    [Fact]
    public void Nested_expression_functions_keep_receiving_return_context()
    {
        using var engine=new CameoEngine();
        Assert.Equal(42,engine.RunString("let f: fn(integer) -> fn(integer) -> integer = (x) => (y) => x+y return f(20)(22)")[0].I);
    }

    [Fact]
    public void Record_declarations_do_not_break_expression_function_lookahead()
    {
        using var engine=new CameoEngine();
        Assert.Equal(42,engine.RunString("let a = (1) record Empty { } let f = () => 42 return f()")[0].I);
    }

    [Fact]
    public void Literal_return_inference_satisfies_an_enum_callback()
    {
        using var engine=new CameoEngine();
        var result=engine.RunString("enum Verdict { 'ok', 'bad' } fn judge(f: fn(integer) -> Verdict) -> Verdict { return f(1) } return judge((n) => 'ok')");
        Assert.Equal("ok",result[0].ToText());
    }

    [Fact]
    public void Expression_function_uses_the_existing_jit()
    {
        using var engine=new CameoEngine();
        JitLibrary.Enable(engine.Runtime);
        var function=engine.RunString("return (x: integer) => x * 2")[0];
        Assert.True(JitLibrary.TryCompile(function,out var compiled,out var reason),reason?.ToString());
        Assert.Equal(42,engine.State.Call(compiled,Cameo.Runtime.Vm.Value.FromInt(21))[0].I);
    }
}
