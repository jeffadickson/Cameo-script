using Cameo;
using Xunit;

namespace Cameo.Tests;

/// <summary>Exercises the complete cancellation example through the real Cameo engine.</summary>
public class CameoCancellationExampleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deadline_example_matches_its_declared_output(bool typeCheck)
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Cameo.slnx")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string directory = Path.Combine(root, "examples");
        string path = Path.Combine(directory, "10-cancellation-deadlines.cameo");
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        var output = new StringWriter { NewLine = "\n" };
        engine.State.Stdout = output;
        engine.RunFile(path);
        Assert.Equal(File.ReadAllText(Path.Combine(directory, "10-cancellation-deadlines.expected"))
            .Replace("\r\n", "\n"), output.ToString());
    }
}
