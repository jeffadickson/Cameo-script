using System.Reflection;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Distinct Cameo entry point without changing the maintained Teal language.</summary>
public class CameoTests
{
    [Fact]
    public void Cameo_has_a_distinct_managed_entry_point_and_executes_common_source()
    {
        Type? type = Type.GetType("Cameo.CameoEngine, Cameo.Language", throwOnError: false);
        Assert.NotNull(type);
        using var engine = Assert.IsAssignableFrom<IDisposable>(Activator.CreateInstance(type));
        MethodInfo? run = type.GetMethod("RunString", [typeof(string), typeof(string)]);
        Assert.NotNull(run);
        var values = Assert.IsType<Value[]>(run.Invoke(engine, ["return 6 * 7, 'héllo'", "first.cameo"]));
        Assert.Equal(42, values[0].I);
        Assert.Equal("héllo", values[1].ToText());
    }

    [Fact]
    public void Legacy_Teal_constructor_remains_available()
    {
        Assert.NotNull(typeof(ScriptEngine).GetConstructor([typeof(CameoRuntimeOptions)]));
        using var engine = new ScriptEngine();
        Assert.Equal(42, engine.RunString("return 6 * 7")[0].I);
    }
}
