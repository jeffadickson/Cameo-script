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
        Assert.Equal(42,engine.RunString("local f: function(integer): function(integer): integer = (x) => (y) => x+y return f(20)(22)")[0].I);
    }

    [Fact]
    public void Class_lowering_invalidates_expression_function_lookahead()
    {
        using var engine=new CameoEngine();
        Assert.Equal(42,engine.RunString("local a = (1) local class Empty end local f = () => 42 return f()")[0].I);
    }

    [Fact]
    public void Literal_return_inference_satisfies_an_enum_callback()
    {
        using var engine=new CameoEngine();
        var result=engine.RunString("local enum Verdict 'ok' 'bad' end local function judge(f: function(integer): Verdict): Verdict return f(1) end return judge((n) => 'ok')");
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
