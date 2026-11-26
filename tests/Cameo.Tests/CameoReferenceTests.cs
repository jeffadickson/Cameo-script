using Cameo;
using Xunit;

namespace Cameo.Tests;

/// <summary>Reference programs run with deterministic data through the public managed entry.</summary>
public class CameoReferenceTests
{
    private static string Root
    {
        get
        {
            string? root=AppContext.BaseDirectory;
            while(root!=null&&!File.Exists(Path.Combine(root,"Cameo.slnx")))root=Path.GetDirectoryName(root);
            return root??throw new DirectoryNotFoundException("Repository root");
        }
    }

    [Theory]
    [InlineData("01-file-organizer", true)] [InlineData("01-file-organizer", false)]
    [InlineData("02-csv-report", true)] [InlineData("02-csv-report", false)]
    [InlineData("03-json-client", true)] [InlineData("03-json-client", false)]
    [InlineData("04-async-workflow", true)] [InlineData("04-async-workflow", false)]
    public void Reference_script_matches_its_declared_output(string name, bool typeCheck)
    {
        string directory=Path.Combine(Root,"examples");
        string script=Path.Combine(directory,name+".cameo");
        Assert.True(File.Exists(script),"The Cameo reference program has not been created");
        using var engine=new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        var output=new StringWriter{NewLine="\n"};
        engine.State.Stdout=output;
        engine.RunFile(script,Path.Combine(directory,"fixtures"));
        Assert.Equal(File.ReadAllText(Path.Combine(directory,name+".expected")).Replace("\r\n","\n"),output.ToString());
    }

    [Theory]
    [InlineData("02-csv-report","tasks-bad.csv","row 2.hours", true)]
    [InlineData("02-csv-report","tasks-bad.csv","row 2.hours", false)]
    [InlineData("03-json-client","profile-null.json","$.name: null is not allowed", true)]
    [InlineData("03-json-client","profile-null.json","$.name: null is not allowed", false)]
    [InlineData("03-json-client","profile-missing.json","$.name: missing", true)]
    [InlineData("03-json-client","profile-missing.json","$.name: missing", false)]
    [InlineData("03-json-client","https://example.invalid/","network access is not enabled", true)]
    [InlineData("03-json-client","https://example.invalid/","network access is not enabled", false)]
    [InlineData("03-json-client","profile-wrong-name.json","$.name: expected string", true)]
    [InlineData("03-json-client","profile-wrong-name.json","$.name: expected string", false)]
    [InlineData("03-json-client","profile-fractional-visits.json","$.visits: expected integer", true)]
    [InlineData("03-json-client","profile-fractional-visits.json","$.visits: expected integer", false)]
    [InlineData("03-json-client","missing-profile.json","missing-profile.json", true)]
    [InlineData("03-json-client","missing-profile.json","missing-profile.json", false)]
    [InlineData("02-csv-report","tasks-negative.csv","row 2.hours", true)]
    [InlineData("02-csv-report","tasks-negative.csv","row 2.hours", false)]
    [InlineData("02-csv-report","tasks-invalid-done.csv","expected true or false", true)]
    [InlineData("02-csv-report","tasks-invalid-done.csv","expected true or false", false)]
    public void Input_failure_never_becomes_a_successful_report(string name,string input,string expected, bool typeCheck)
    {
        string directory=Path.Combine(Root,"examples");
        string script=Path.Combine(directory,name+".cameo");
        Assert.True(File.Exists(script),"Reference program missing");
        using var engine=new CameoEngine(new CameoRuntimeOptions { TypeCheck = typeCheck });
        var output=new StringWriter{NewLine="\n"};engine.State.Stdout=output;
        var errors=new StringWriter{NewLine="\n"};engine.State.Stderr=errors;
        var result=engine.TryRunFile(script,Path.Combine(directory,"fixtures"),input);
        Assert.Equal(2,Assert.IsType<Cameo.Runtime.RunExited>(result.Value).Code);
        Assert.Contains(expected,errors.ToString());
        Assert.DoesNotContain("9007199254740993 visits",output.ToString());
        Assert.Equal(7,engine.RunString("return 7")[0].I);
    }
}
