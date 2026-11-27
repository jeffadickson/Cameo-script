using Cameo;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Portable runner tests exercise the command registry and real script files.</summary>
public sealed class CameoTestCommandTests
{
    [Fact]
    public void Test_is_a_registered_command()
    {
        var parsed = CameoCommands.Parse(["test", "--json", "one.cameo"]);
        Assert.Null(parsed.Error);
        var command = Assert.IsType<CameoTestCommand>(parsed.Command);
        Assert.True(command.Json);
        Assert.Equal(["one.cameo"], command.Files);
        Assert.Contains(CameoCommands.Descriptions, descriptor => descriptor.Id == "cameo.test");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("86401")]
    public void Invalid_cli_timeout_returns_a_usage_diagnostic(string timeout)
    {
        var parsed = CameoCommands.Parse(["test", "--timeout", timeout, "test.cameo"]);
        Assert.Null(parsed.Command);
        Assert.Equal("CAMEOCLI004", parsed.Error?.Code);
    }

    [Fact]
    public void Dash_prefixed_path_can_follow_end_of_options()
    {
        var parsed = CameoCommands.Parse(["test", "--timeout", "2", "--", "-script.cameo"]);
        var test = Assert.IsType<CameoTestCommand>(parsed.Command);
        Assert.Equal(["-script.cameo"], test.Files);
        Assert.Equal(TimeSpan.FromSeconds(2), test.Timeout);
    }

    [Fact]
    public void Scripts_run_in_ordinal_order_with_fresh_globally_isolated_state()
    {
        using var files = new TemporarySources();
        string first = files.Add("A.cameo", """
            rawset(_G, "runner_probe", 123)
            print("first")
            io.stderr:write("note")
            """);
        string second = files.Add("b.cameo", """
            assert(rawget(_G, "runner_probe") == nil)
            assert(io.read() == nil)
            print("second")
            """);

        var result = CameoCommands.Execute(new CameoTestCommand([second, first], Json: true),
            TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json);
        Assert.Empty(result.Diagnostics);
        Assert.Collection(Assert.IsAssignableFrom<IReadOnlyList<CameoTestCaseResult>>(result.TestCases),
            test => {
                Assert.Equal(first, test.File);
                Assert.True(test.Passed);
                Assert.Equal("first\n", test.StandardOutput);
                Assert.Equal("note", test.StandardError);
            },
            test => {
                Assert.Equal(second, test.File);
                Assert.True(test.Passed);
                Assert.Equal("second\n", test.StandardOutput);
                Assert.Empty(test.StandardError);
            });
    }

    [Fact]
    public void Failures_are_typed_and_the_batch_runs_later_cases()
    {
        using var files = new TemporarySources();
        string bad = files.Add("01-syntax.cameo", "let =");
        string error = files.Add("02-runtime.cameo", """error("failed assertion")""");
        string exit = files.Add("03-exit.cameo", "os.exit(7)");
        string missing = files.Name("04-missing.cameo");
        string aborted = files.Add("05-loop.cameo", "while true { }");
        string successfulExit = files.Add("06-ok.cameo", "os.exit(0)");
        var result = CameoCommands.Execute(
            new CameoTestCommand([successfulExit, aborted, missing, exit, error, bad],
                Timeout: TimeSpan.FromSeconds(0.5)), TextWriter.Null, TextWriter.Null);

        Assert.Equal(1, result.ExitCode);
        string summary = Assert.IsType<string>(result.Text);
        Assert.Contains("1/6 passed", summary);
        Assert.Collection(Assert.IsAssignableFrom<IReadOnlyList<CameoTestCaseResult>>(result.TestCases),
            test => Assert.Equal(CameoTestOutcome.CompileFailed, test.Outcome),
            test => {
                Assert.Equal(CameoTestOutcome.RuntimeFailed, test.Outcome);
                Assert.Contains("failed assertion", Assert.Single(test.Diagnostics).Message);
            },
            test => {
                Assert.Equal(CameoTestOutcome.Exited, test.Outcome);
                Assert.Equal(7, test.ScriptExitCode);
                Assert.False(test.Passed);
            },
            test => Assert.Equal(CameoTestOutcome.MissingSource, test.Outcome),
            test => {
                Assert.Equal(CameoTestOutcome.Aborted, test.Outcome);
                Assert.Equal(AbortReason.Timeout, test.AbortReason);
            },
            test => {
                Assert.Equal(CameoTestOutcome.Exited, test.Outcome);
                Assert.Equal(0, test.ScriptExitCode);
                Assert.True(test.Passed);
            });
    }

    [Fact]
    public void Invalid_typed_path_rejects_the_whole_request_before_execution()
    {
        using var files = new TemporarySources();
        string unexecuted = files.Add("script.cameo", "error('would have run')");
        var result = CameoCommands.Execute(new CameoTestCommand([unexecuted, " "]),
            TextWriter.Null, TextWriter.Null);
        Assert.Equal(2, result.ExitCode);
        Assert.Null(result.TestCases);
        Assert.Equal("CAMEOCLI002", Assert.Single(result.Diagnostics).Code);
    }

    private sealed class TemporarySources : IDisposable
    {
        private readonly List<string> _files = [];
        private readonly string _directory;

        public TemporarySources()
        {
            string parent = Path.GetTempPath();
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
            _directory = Path.Combine(parent, "cameo-test-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(_directory)) throw new IOException("Temporary case collision");
            Directory.CreateDirectory(_directory);
        }

        public string Name(string name) => Path.Combine(_directory, name);

        public string Add(string name, string source)
        {
            string file = Name(name);
            File.WriteAllText(file, source);
            _files.Add(file);
            return file;
        }

        public void Dispose()
        {
            foreach (string file in _files) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}
