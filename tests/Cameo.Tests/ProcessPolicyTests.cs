using CsCheck;
using Cameo.Runtime.Vm;
using Xunit;

namespace Cameo.Tests;

/// <summary>Exact managed process rules and real pre-launch/worker enforcement.</summary>
public class ProcessPolicyTests
{
    private static string ExampleExecutable => Path.Combine(Path.GetTempPath(),
        OperatingSystem.IsWindows() ? "teal-policy-example.exe" : "teal-policy-example");

    [Fact]
    public void Policies_copy_rules_and_argument_vectors_before_sharing_them()
    {
        string[] arguments = ["first", "two words"];
        var rule = new ProcessRule(ExampleExecutable, arguments);
        ProcessRule[] rules = [rule];
        var policy = new ProcessPolicy(rules);
        arguments[0] = "changed";
        rules[0] = new ProcessRule(ExampleExecutable, []);
        Assert.True(policy.Allows(ExampleExecutable, ["first", "two words"]));
        Assert.False(policy.Allows(ExampleExecutable, ["changed", "two words"]));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)rule.Arguments)[0] = "changed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-program")]
    [InlineData("./relative-program")]
    [InlineData("bad\0program")]
    public void Rule_construction_rejects_non_absolute_or_malformed_paths(string? path)
        => Assert.ThrowsAny<ArgumentException>(() => new ProcessRule(path!, []));

    [Fact]
    public void Invalid_host_rule_collections_are_argument_errors()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ProcessRule(ExampleExecutable, null!));
        Assert.ThrowsAny<ArgumentException>(() => new ProcessRule(ExampleExecutable, [null!]));
        Assert.ThrowsAny<ArgumentException>(() => new ProcessRule(ExampleExecutable, ["bad\0argument"]));
        Assert.ThrowsAny<ArgumentException>(() => new ProcessPolicy(null!));
        Assert.ThrowsAny<ArgumentException>(() => new ProcessPolicy([null!]));
    }

    [Fact]
    public void Runtime_malformed_requests_deny_without_throwing()
    {
        var policy = new ProcessPolicy([new ProcessRule(ExampleExecutable, [])]);
        Assert.False(policy.Allows(null, []));
        Assert.False(policy.Allows("", []));
        Assert.False(policy.Allows("relative-program", []));
        Assert.False(policy.Allows("bad\0program", []));
        Assert.False(policy.Allows(ExampleExecutable, null));
        Assert.False(policy.Allows(ExampleExecutable, [null!]));
        Assert.False(policy.Allows(ExampleExecutable, ["bad\0argument"]));
        Assert.False(new ProcessPolicy([]).Allows(ExampleExecutable, []));
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("owned.")]
    [InlineData("owned.exe.")]
    [InlineData("owned.exe ")]
    [InlineData("owned.exe\t")]
    public void Windows_rules_reject_implicit_extension_or_trimmed_filename_spellings(string spelling)
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.Combine(Path.GetTempPath(), spelling);
        Assert.ThrowsAny<ArgumentException>(() => new ProcessRule(path, []));
        var policy = new ProcessPolicy([new ProcessRule(ExampleExecutable, [])]);
        Assert.False(policy.Allows(path, []));
    }

    [Fact]
    public void Rules_match_full_argument_vectors_and_explicit_pty_allowance()
    {
        var plain = new ProcessPolicy([new ProcessRule(ExampleExecutable, ["one", "two"])]);
        Assert.True(plain.Allows(ExampleExecutable, ["one", "two"]));
        Assert.False(plain.Allows(ExampleExecutable, ["two", "one"]));
        Assert.False(plain.Allows(ExampleExecutable, ["one"]));
        Assert.False(plain.Allows(ExampleExecutable, ["one", "two", "three"]));
        Assert.False(plain.Allows(ExampleExecutable, ["One", "two"]));
        Assert.False(plain.Allows(ExampleExecutable, ["one", "two"], pseudoTerminal: true));
        Assert.False(plain.Allows(ExampleExecutable + "-different", ["one", "two"]));
        var pty = new ProcessPolicy([new ProcessRule(ExampleExecutable, ["one", "two"], allowPseudoTerminal: true)]);
        Assert.True(pty.Allows(ExampleExecutable, ["one", "two"]));
        Assert.True(pty.Allows(ExampleExecutable, ["one", "two"], pseudoTerminal: true));
    }

    [Fact]
    public void Executable_paths_normalize_lexically_and_require_ordinal_spelling()
    {
        string path = ExampleExecutable;
        var policy = new ProcessPolicy([new ProcessRule(path, [])]);
        string alternate = Path.Combine(Path.GetDirectoryName(path)!, "unused", "..", Path.GetFileName(path));
        Assert.True(policy.Allows(alternate, []));
        // Be conservative even in Windows directories configured for case sensitivity.
        Assert.False(policy.Allows(path.ToUpperInvariant(), []));
    }

    [Theory]
    [InlineData("\u00e9", "e\u0301")]
    [InlineData("\u00c5", "\u212b")]
    [InlineData("I", "\u0131")]
    [InlineData("\ufb00", "ff")]
    public void Unicode_argument_spellings_do_not_gain_culture_or_normalization_equivalence(string allowed, string different)
    {
        var policy = new ProcessPolicy([new ProcessRule(ExampleExecutable, [allowed])]);
        Assert.True(policy.Allows(ExampleExecutable, [allowed]));
        // Canonical/compatibility normalization and culture-sensitive comparisons can
        // equate some of these spellings. An exact host rule must preserve the supplied
        // character sequence rather than broaden permission through such equivalence.
        Assert.False(policy.Allows(ExampleExecutable, [different]));
    }

    [Fact]
    public void Argument_identity_is_an_exact_sequence_property()
    {
        Gen.Int[-10000, 10000].Sample(number =>
        {
            string text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var policy = new ProcessPolicy([new ProcessRule(ExampleExecutable, [text, "literal ; $HOME", ""])]);
            Assert.True(policy.Allows(ExampleExecutable, [text, "literal ; $HOME", ""]));
            Assert.False(policy.Allows(ExampleExecutable, [text + "x", "literal ; $HOME", ""]));
            Assert.False(policy.Allows(ExampleExecutable, [text, "literal ; $HOME"]));
            Assert.False(policy.Allows(ExampleExecutable, [text, "literal ; $HOME", "", "extra"]));
        }, iter: 200, threads: 1, seed: Properties.Prop.Seeds.CsCheckSeed);
    }

    private static (string Executable, string[] Arguments) Command()
    {
        if (OperatingSystem.IsWindows())
        {
            string? command = Environment.GetEnvironmentVariable("ComSpec");
            Assert.False(string.IsNullOrEmpty(command));
            return (Path.GetFullPath(command!), ["/d", "/c", "echo policy-ok"]);
        }
        return ("/bin/sh", ["-c", "printf policy-ok"]);
    }

    private static ScriptEngine Engine(ProcessPolicy? policy, bool allowProcesses = true) => new(new CameoRuntimeOptions
    {
        TypeCheck = false, AllowProcesses = allowProcesses, ProcessPolicy = policy,
        Timeout = TimeSpan.FromSeconds(10),
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Allowed_exec_and_spawn_use_the_actual_process_adapters(bool spawn)
    {
        var command = Command();
        using var engine = Engine(new ProcessPolicy([new ProcessRule(command.Executable, command.Arguments)]));
        string body = spawn
            ? "local p=aio.spawn(program,args):get() local text=p.stdout:readAll():get() local code=p:wait():get() return text,code"
            : "local p=aio.exec(program,args):get() return p.stdout,p.code";
        var values = Run(engine, body, command.Executable, command.Arguments);
        Assert.Equal("policy-ok", values[0].ToText().Trim());
        Assert.Equal(0, values[1].I);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_normalized_authorization_launches_the_normalized_path(bool spawn)
    {
        var command = Command();
        string unused = Path.Combine(Path.GetDirectoryName(command.Executable)!, "teal-policy-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(unused));
        string spelling = Path.Combine(unused, "..", Path.GetFileName(command.Executable));
        using var engine = Engine(new ProcessPolicy([new ProcessRule(command.Executable, command.Arguments)]));
        string body = spawn
            ? "local p=aio.spawn(program,args):get() p.stdout:readAll():get() return p:wait():get()"
            : "return aio.exec(program,args):get().code";
        // On POSIX, traversing a nonexistent component in the original spelling fails.
        // Authorization must launch its checked lexical result, not that original path.
        Assert.Equal(0, Run(engine, body, spelling, command.Arguments)[0].I);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("spawn")]
    [InlineData("pty")]
    public void Denied_requests_have_typed_errors_and_leave_the_engine_usable(string operation)
    {
        var command = Command();
        using var engine = Engine(new ProcessPolicy([new ProcessRule(command.Executable, command.Arguments)]));
        string call = operation == "exec" ? "aio.exec(program,args)" :
            operation == "spawn" ? "aio.spawn(program,args)" : "aio.spawn(program,args,{pty=true})";
        // A forbidden absent executable distinguishes denial from an OS start failure.
        foreach (var request in new[]
        {
            (command.Executable + "-absent", command.Arguments),
            (command.Executable, command.Arguments.Append("forbidden").ToArray()),
            (Path.GetFileName(command.Executable), command.Arguments),
        })
            Assert.Equal("permission_denied", Run(engine, $"local v,e={call}:await() return e.cause.kind", request.Item1, request.Item2)[0].ToText());
        if (operation == "pty")
            Assert.Equal("permission_denied", Run(engine, $"local v,e={call}:await() return e.cause.kind", command.Executable, command.Arguments)[0].ToText());
        Assert.Equal(42, engine.RunString("return 42")[0].I);
    }

    [Fact]
    public void A_matching_policy_does_not_grant_the_coarse_permission()
    {
        var command = Command();
        using var engine = Engine(new ProcessPolicy([new ProcessRule(command.Executable, command.Arguments)]), allowProcesses: false);
        Assert.Equal("permission_denied", Run(engine, "local v,e=aio.exec(program,args):await() return e.cause.kind", command.Executable, command.Arguments)[0].ToText());
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("spawn")]
    [InlineData("pty")]
    public void Windows_adapters_do_not_resolve_an_extensionless_request_to_an_exe_sibling(string operation)
    {
        if (!OperatingSystem.IsWindows()) return;
        string parent = Path.GetTempPath();
        Assert.True(Directory.Exists(parent));
        string directory = Directory.CreateTempSubdirectory("tealvm-process-extension-").FullName;
        string executable = Path.Combine(directory, "owned.exe");
        try
        {
            var command = Command();
            File.Copy(command.Executable, executable);
            var policy = new ProcessPolicy([new ProcessRule(executable, command.Arguments, allowPseudoTerminal: true)]);
            using var engine = Engine(policy);
            string call = operation == "exec" ? "aio.exec(program,args)" :
                operation == "spawn" ? "aio.spawn(program,args)" : "aio.spawn(program,args,{pty=true})";
            string noExtension = Path.Combine(directory, "owned");
            Assert.ThrowsAny<ArgumentException>(() => new ProcessRule(noExtension, command.Arguments));
            Assert.False(policy.Allows(noExtension, command.Arguments, pseudoTerminal: operation == "pty"));
            Assert.Equal("permission_denied", Run(engine, $"local v,e={call}:await() return e.cause.kind", noExtension, command.Arguments)[0].ToText());
            Assert.Equal(42, engine.RunString("return 42")[0].I);
        }
        finally
        {
            File.Delete(executable);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Null_policy_preserves_existing_coarse_permission_behavior()
    {
        var command = Command();
        using var engine = Engine(null);
        Assert.Equal(0, Run(engine, "return aio.exec(program,args):get().code", command.Executable, command.Arguments)[0].I);
    }

    [Fact]
    public void Workers_and_nested_sandboxes_inherit_the_same_policy()
    {
        var command = Command();
        using var engine = Engine(new ProcessPolicy([new ProcessRule(command.Executable, command.Arguments)]));
        const string body = "local v,e=aio.exec(program,args):await() return e and e.cause.kind or 'allowed'";
        string worker = "local program,args=... " + body;
        // The worker factory deliberately provides no policy: NewWorker must overwrite it.
        var factory = engine.State.WorkerFactory!;
        engine.State.WorkerFactory = () => { var workerState = factory(); workerState.ProcessPolicy = null; return workerState; };
        foreach (string code in new[]
        {
            "return Future.parallel(function(program,args) " + body + " end,program,args):get()",
            "return sandbox.run([=[" + worker + "]=],{allow={'run'},args={program,args}}):get()",
            "return sandbox.run([=[local program,args=... return sandbox.run([==[" + worker + "]==],{allow={'run'},args={program,args}}):get()]=],{allow={'run'},args={program,args}}):get()",
        })
        {
            Assert.Equal("permission_denied", Run(engine, code, command.Executable, command.Arguments.Append("forbidden").ToArray())[0].ToText());
            Assert.Equal("allowed", Run(engine, code, command.Executable, command.Arguments)[0].ToText());
        }
    }

    private static Value[] Run(ScriptEngine engine, string source, string executable, string[] arguments)
    {
        var table = new Table();
        foreach (string argument in arguments) table.Append(Value.FromText(argument));
        return engine.RunString("local program,args=... " + source, "process-policy", Value.FromText(executable), Value.FromTable(table));
    }
}
