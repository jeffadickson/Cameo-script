using Cameo;
using Cameo.Jit;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Complete surface examples retain their declared output across checking and execution modes.</summary>
public class CameoSurfaceExampleTests
{
    private static string Root
    {
        get
        {
            string? directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "Cameo.slnx")))
                directory = Path.GetDirectoryName(directory);
            return directory ?? throw new DirectoryNotFoundException("Repository root");
        }
    }

    /// <summary>Each reference program is tested checked/unchecked with interpreter execution and a representative named IL function.</summary>
    public static TheoryData<string, bool, bool> Examples
    {
        get
        {
            var cases = new TheoryData<string, bool, bool>();
            foreach (string name in new[] { "06-stock-report", "07-recursion-and-captures", "08-file-futures", "09-explicit-recovery" })
                foreach (bool typeCheck in new[] { true, false })
                    foreach (bool compiled in new[] { false, true })
                        cases.Add(name, typeCheck, compiled);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Complete_example_matches_declared_output(string name, bool typeCheck, bool compiled)
    {
        string directory = Path.Combine(Root, "examples");
        string path = Path.Combine(directory, name + ".cameo");
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        var output = new StringWriter { NewLine = "\n" };
        engine.State.Stdout = output;
        if (compiled)
        {
            // The IL target rejects chunks that create closures. Execute the full script, then
            // compile a representative named function from it; do not claim whole-script IL.
            var arguments = new Table();
            arguments.Set(Value.FromInt(1), Value.FromText(Path.Combine(directory, "fixtures")));
            engine.State.Globals.SetStr("arg", Value.FromTable(arguments));
            string functionName = name switch
            {
                "06-stock-report" => "classify",
                "07-recursion-and-captures" => "factorial",
                "08-file-futures" => "normalize",
                _ => "recover"
            };
            Value function = engine.RunString(File.ReadAllText(path) + "\nreturn " + functionName, path)[0];
            Assert.True(JitLibrary.TryCompile(function, out Value lowered, out var issue), issue?.ToString());
            if (name == "06-stock-report")
                Assert.Equal("low", engine.State.Call(lowered, Value.FromInt(2))[0].AsString);
            else if (name == "07-recursion-and-captures")
                Assert.Equal(120, engine.State.Call(lowered, Value.FromInt(5))[0].I);
            else if (name == "08-file-futures")
                Assert.Equal("cameo", engine.State.Call(lowered, Value.FromText("  cameo \n"))[0].AsString);
            else
            {
                var failure = new Table();
                failure.SetStr("kind", Value.FromText("failed"));
                Assert.Equal(42, engine.State.Call(lowered, Value.FromTable(failure))[0].I);
            }
        }
        else
        {
            engine.RunFile(path, Path.Combine(directory, "fixtures"));
        }
        Assert.Equal(File.ReadAllText(Path.Combine(directory, name + ".expected")).Replace("\r\n", "\n"), output.ToString());
    }
}
