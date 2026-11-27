using Cameo;
using CsCheck;
using Xunit;

namespace Cameo.Tests;

/// <summary>Typed automation uses the same validation and operation set as the CLI.</summary>
public class CameoCommandTests
{
    [Fact]
    public void Parser_discovery_help_check_aliases_and_defaults_are_explicit()
    {
        foreach(string[] args in new[]{Array.Empty<string>(),new[]{"help"},new[]{"-h"},new[]{"--help"}})
            Assert.IsType<CameoHelpCommand>(CameoCommands.Parse(args).Command);
        Assert.IsType<CameoDescribeCommand>(CameoCommands.Parse(["commands"]).Command);
        foreach(bool json in new[]{false,true})
        {
            var check=Assert.IsType<CameoCheckCommand>(CameoCommands.Parse(json?["check","--json","a.cameo","b.cameo"]:["check","a.cameo","b.cameo"]).Command);
            Assert.Equal(json,check.Json);
            Assert.Equal(new[]{"a.cameo","b.cameo"},check.Files);
        }
        foreach(string[] args in new[]{new[]{"a.cameo","--flag"},new[]{"A.CAMEO","--flag"},new[]{"run","a.cameo","--flag"}})
        {
            var run=Assert.IsType<CameoRunCommand>(CameoCommands.Parse(args).Command);
            Assert.False(run.AllowNetwork||run.AllowProcesses||run.AllowDatabase);
            Assert.Null(run.Timeout);Assert.Null(run.MaxMemoryBytes);
            Assert.Equal(new[]{"--flag"},run.Arguments);
        }
        Assert.Equal("-file.cameo",Assert.IsType<CameoRunCommand>(CameoCommands.Parse(["run","--","-file.cameo"]).Command).File);
        Assert.NotNull(CameoCommands.Parse(new[]{"run",null!,"a.cameo"}).Error);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("86401")]
    [InlineData("1e-20")]
    public void Parser_rejects_timeout_bounds_and_sub_tick_values(string seconds)
    {
        Assert.Equal("CAMEOCLI004",CameoCommands.Parse(["run","--timeout",seconds,"a.cameo"]).Error?.Code);
    }

    [Fact]
    public void Parser_accepts_inclusive_upper_timeout_and_memory_bounds()
    {
        var upper=Assert.IsType<CameoRunCommand>(CameoCommands.Parse(["run","--timeout","86400","--max-memory","2147483647","a.cameo"]).Command);
        Assert.Equal(TimeSpan.FromDays(1),upper.Timeout);
        Assert.Equal((long)int.MaxValue*1024*1024,upper.MaxMemoryBytes);
        Assert.Equal(1024*1024,Assert.IsType<CameoRunCommand>(CameoCommands.Parse(["run","--max-memory","1","a.cameo"]).Command).MaxMemoryBytes);
    }

    [Fact]
    public void Managed_checks_distinguish_errors_warnings_and_missing_files()
    {
        WithSource("let unused = 1",path=>
        {
            var warnings=CameoCommands.Execute(new CameoCheckCommand([path]),TextWriter.Null,TextWriter.Null);
            Assert.Equal(0,warnings.ExitCode);
            Assert.Contains(warnings.Diagnostics,d=>d.Warning&&d.Code=="CAMEOWARNING");
            var mixed=CameoCommands.Execute(new CameoCheckCommand([path+".missing",path]),TextWriter.Null,TextWriter.Null);
            Assert.Equal(1,mixed.ExitCode);
            Assert.Contains(mixed.Diagnostics,d=>!d.Warning&&d.Code=="CAMEOCLI003");
            Assert.Contains(mixed.Diagnostics,d=>d.Warning);
        });
        foreach(string source in new[]{"let =","let f: function(integer): integer = 'bad'"})
            WithSource(source,path=>
            {
                var errors=CameoCommands.Execute(new CameoCheckCommand([path]),TextWriter.Null,TextWriter.Null);
                Assert.Equal(1,errors.ExitCode);
                Assert.Contains(errors.Diagnostics,d=>!d.Warning&&d.Code=="CAMEOTYPE");
            });
    }

    [Fact]
    public void Managed_request_validation_does_not_execute_invalid_settings()
    {
        WithSource("error('must not execute')",path=>
        {
            foreach(var request in new[]{new CameoRunCommand(path,[],Timeout:TimeSpan.Zero),new CameoRunCommand(path,[],Timeout:TimeSpan.FromDays(1)+TimeSpan.FromTicks(1)),new CameoRunCommand(path,[],MaxMemoryBytes:0),new CameoRunCommand(path,[],MaxMemoryBytes:-1)})
            {
                var result=CameoCommands.Execute(request,TextWriter.Null,TextWriter.Null);
                Assert.Equal(2,result.ExitCode);
                Assert.Contains(result.Diagnostics,d=>d.Code=="CAMEOCLI004");
            }
            Assert.Equal(2,CameoCommands.Execute(new CameoRunCommand(path,null!),TextWriter.Null,TextWriter.Null).ExitCode);
            Assert.Equal(2,CameoCommands.Execute(new CameoCheckCommand(null!),TextWriter.Null,TextWriter.Null).ExitCode);
            Assert.Throws<ArgumentNullException>(()=>CameoCommands.Execute(new CameoHelpCommand(),null!,TextWriter.Null));
            Assert.Throws<ArgumentNullException>(()=>CameoCommands.Execute(new CameoHelpCommand(),TextWriter.Null,null!));
        });
    }

    [Fact]
    public void Managed_memory_abort_is_distinct_from_timeout()
    {
        WithSource("while true { }",path=>
        {
            // The inherited watchdog samples process-wide managed memory every10ms.
            // Keep the script active for observation; the independent timeout bounds
            // a broken memory watchdog. This never kills or caps the test process.
            var result=CameoCommands.Execute(new CameoRunCommand(path,[],Timeout:TimeSpan.FromSeconds(10),MaxMemoryBytes:1),TextWriter.Null,TextWriter.Null);
            Assert.Equal(125,result.ExitCode);
            Assert.Contains(result.Diagnostics,d=>d.Code=="CAMEOABORT");
        });
    }

    [Fact]
    public void Managed_inclusive_limits_allow_a_small_script()
    {
        WithSource("return 42",path=>
            Assert.Equal(0,CameoCommands.Execute(new CameoRunCommand(path,[],Timeout:TimeSpan.FromDays(1),MaxMemoryBytes:long.MaxValue),TextWriter.Null,TextWriter.Null).ExitCode));
    }

    [Fact]
    public void Windows_read_sharing_failure_is_a_structured_io_error()
    {
        // Windows sharing semantics are the oracle here; this case is not claimed on Unix.
        if(!OperatingSystem.IsWindows())return;
        WithSource("return 42",path=>
        {
            using var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None);
            var result=CameoCommands.Execute(new CameoCheckCommand([path]),TextWriter.Null,TextWriter.Null);
            Assert.Equal(2,result.ExitCode);
            Assert.Contains(result.Diagnostics,d=>d.Code=="CAMEOCLI003");
        });
    }

    [Fact]
    public void Managed_output_is_visible_in_buffered_sinks_before_return()
    {
        WithSource("io.write('out') io.stderr:write('err')",path=>
        {
            using var stdout=new MemoryStream();using var stderr=new MemoryStream();
            using var output=new StreamWriter(stdout,leaveOpen:true);using var error=new StreamWriter(stderr,leaveOpen:true);
            Assert.Equal(0,CameoCommands.Execute(new CameoRunCommand(path,[]),output,error).ExitCode);
            Assert.Equal("out",System.Text.Encoding.UTF8.GetString(stdout.ToArray()));
            Assert.Equal("err",System.Text.Encoding.UTF8.GetString(stderr.ToArray()));
        });
    }

    [Theory]
    [InlineData("print(arg[1])",0,"é\n",null)]
    [InlineData("let f = (x) => x",1,"","CAMEO001")]
    [InlineData("error('héllo')",1,"","CAMEORUNTIME")]
    [InlineData("os.exit(7)",7,"",null)]
    [InlineData("return ((n: integer) => n * 2)(21)",0,"",null)]
    public void Managed_run_and_check_share_expected_outcomes(string source,int code,string stdout,string? diagnostic)
    {
        WithSource(source,path=>
        {
            var output=new StringWriter{NewLine="\n"};
            var result=CameoCommands.Execute(new CameoRunCommand(path,["é"]),output,TextWriter.Null);
            Assert.Equal(code,result.ExitCode);
            Assert.Equal(stdout,output.ToString());
            if(diagnostic!=null)Assert.Contains(result.Diagnostics,d=>d.Code==diagnostic);
            if(diagnostic=="CAMEO001")
                Assert.StartsWith("expression parameter needs a concrete annotation",Assert.Single(result.Diagnostics,d=>d.Code==diagnostic).Message);
            if(diagnostic=="CAMEORUNTIME")Assert.Contains(result.Diagnostics,d=>d.Message.Contains("héllo"));
            var check=CameoCommands.Execute(new CameoCheckCommand([path],true),TextWriter.Null,TextWriter.Null);
            Assert.Equal(diagnostic=="CAMEO001"?1:0,check.ExitCode);
            Assert.True(check.Json);
            if(diagnostic=="CAMEO001")Assert.Contains(check.Diagnostics,d=>d.Code==diagnostic);
        });
    }

    [Fact]
    public void Managed_byte_sink_preserves_non_utf8_output()
    {
        WithSource("io.write(string.char(0,255,195,169))",path=>
        {
            using var bytes=new MemoryStream();
            var result=CameoCommands.Execute(new CameoRunCommand(path,[]),TextWriter.Null,TextWriter.Null,bytes);
            Assert.Equal(0,result.ExitCode);
            Assert.Equal(new byte[]{0,255,195,169},bytes.ToArray());
        });
    }

    [Fact]
    public void Managed_timeout_is_an_explicit_abort_outcome()
    {
        WithSource("while true { }",path=>
        {
            var result=CameoCommands.Execute(new CameoRunCommand(path,[],Timeout:TimeSpan.FromMilliseconds(20)),TextWriter.Null,TextWriter.Null);
            Assert.Equal(124,result.ExitCode);
            Assert.Contains(result.Diagnostics,d=>d.Code=="CAMEOABORT"&&d.Message.Contains("Timeout"));
        });
    }

    private static void WithSource(string source,Action<string> action)
    {
        string parent=Path.GetTempPath();
        if(!Directory.Exists(parent))throw new DirectoryNotFoundException(parent);
        string path=Path.Combine(parent,"cameo-managed-"+Guid.NewGuid().ToString("N")+".cameo");
        if(File.Exists(path))throw new IOException("Temporary collision");
        try{File.WriteAllText(path,source);action(path);}
        finally{if(File.Exists(path))File.Delete(path);}
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unknown", "a.cameo")]
    [InlineData("check")]
    [InlineData("run")]
    [InlineData("check", "--wrong")]
    [InlineData("run", "--wrong", "a.cameo")]
    [InlineData("run", "--timeout")]
    [InlineData("run", "--max-memory", "0", "a.cameo")]
    [InlineData("run", "--max-memory", "2147483648", "a.cameo")]
    [InlineData("commands", "extra")]
    public void Invalid_input_has_a_structured_error(params string[] args)
    {
        var parsed=CameoCommands.Parse(args);
        Assert.Null(parsed.Command);
        Assert.StartsWith("CAMEOCLI",Assert.IsType<CameoDiagnostic>(parsed.Error).Code);
    }

    [Fact]
    public void Run_options_end_at_the_script_and_arguments_remain_exact()
    {
        var parsed=CameoCommands.Parse(["run","--allow-net","--allow-run","--allow-db","--timeout","1.5","--max-memory","2","a.cameo","--timeout","é"]);
        var run=Assert.IsType<CameoRunCommand>(parsed.Command);
        Assert.Null(parsed.Error);
        Assert.True(run.AllowNetwork&&run.AllowProcesses&&run.AllowDatabase);
        Assert.Equal(TimeSpan.FromSeconds(1.5),run.Timeout);
        Assert.Equal(2*1024*1024,run.MaxMemoryBytes);
        Assert.Equal(new[]{"--timeout","é"},run.Arguments);
    }

    [Fact]
    public void Every_declared_operation_has_a_typed_handler()
    {
        Assert.Equal(new[]{"cameo.check","cameo.run","cameo.eval","cameo.dump","cameo.repl","cameo.fmt","cameo.embed","cameo.test","cameo.help","cameo.describe"},CameoCommands.Descriptions.Select(d=>d.Id));
        foreach(CameoCommand command in new CameoCommand[]{new CameoHelpCommand(),new CameoDescribeCommand(),new CameoCheckCommand([]),new CameoRunCommand("missing.cameo",[]),new CameoEvalCommand("return 1",[]),new CameoTestCommand([])})
        {
            var result=CameoCommands.Execute(command,TextWriter.Null,TextWriter.Null);
            Assert.DoesNotContain(result.Diagnostics,d=>d.Message.Contains("supported typed command"));
        }
        Assert.Equal(2,CameoCommands.Execute(null,TextWriter.Null,TextWriter.Null).ExitCode);
        Assert.NotNull(CameoCommands.Parse(null).Error);
        Assert.NotNull(CameoCommands.Parse(new string[]{null!}).Error);
    }

    [Fact]
    public void Arbitrary_arguments_never_escape_as_parser_exceptions()
    {
        // CsCheck reports a replay seed on failure. The suite pins TEALVM_TEST_SEED externally.
        Gen.String[Gen.Char[(char)0,(char)255],0,100].Select(s=>new[]{"run","--timeout",s,"a.cameo"}).Sample(args=>
        {
            var result=CameoCommands.Parse(args);
            Assert.True((result.Command is null)!=(result.Error is null));
        },iter:200,threads:1,seed:Properties.Prop.Seeds.CsCheckSeed);
    }
}
