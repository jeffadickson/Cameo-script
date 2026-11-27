using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Cameo.Tests;

/// <summary>Real process entry points exercise shared typed commands, diagnostics and byte output.</summary>
public class CameoCliTests
{
    [Fact]
    public async Task Help_names_the_new_language()
    {
        var result=await Invoke("--help");
        Assert.Equal(0,result.Code);
        Assert.Contains("Cameo",Encoding.UTF8.GetString(result.Out));
    }

    [Fact]
    public async Task Malformed_import_never_certifies_a_successful_check()
    {
        using var file=new SourceFile("return require('bad')");
        File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file.Path)!,"bad.cameo"),"let f = () =>");
        var result=await Invoke("check","--json",file.Path);
        Assert.Equal(1,result.Code);
        using var json=JsonDocument.Parse(result.Out);
        Assert.Contains(json.RootElement.EnumerateArray(),d=>d.GetProperty("file").GetString()!.EndsWith("bad.cameo")&&!d.GetProperty("warning").GetBoolean());
    }

    [Fact]
    public async Task Run_and_check_use_cameo_syntax()
    {
        using var file=new SourceFile("let f = (n: integer) => n * 2 print(f(21))");
        var check=await Invoke("check","--json",file.Path);
        Assert.Equal(0,check.Code);
        using var json=JsonDocument.Parse(check.Out);
        Assert.Equal(0,json.RootElement.GetArrayLength());
        var run=await Invoke("run",file.Path);
        Assert.Equal(0,run.Code);
        Assert.Equal("42\n",Encoding.UTF8.GetString(run.Out).Replace("\r\n","\n"));
    }

    [Fact]
    public async Task Compile_diagnostics_are_structured_and_preserve_unicode_paths()
    {
        using var file=new SourceFile("let f = (x) => x", "héllo.cameo");
        var check=await Invoke("check","--json",file.Path);
        Assert.Equal(1,check.Code);
        using var json=JsonDocument.Parse(check.Out);
        var error=json.RootElement.EnumerateArray().Single(d=>d.GetProperty("code").GetString()=="CAMEO001");
        Assert.Equal(file.Path,error.GetProperty("file").GetString());
        Assert.Equal(1,error.GetProperty("line").GetInt32());
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("86401")]
    public async Task Invalid_timeout_is_a_usage_error(string value)
    {
        var result=await Invoke("run","--timeout",value,"missing.cameo");
        Assert.Equal(2,result.Code);
        Assert.Contains("CAMEOCLI004",result.Err);
    }

    [Fact]
    public async Task Timeout_terminates_a_running_script()
    {
        using var file=new SourceFile("while true { }");
        var result=await Invoke("run","--timeout","0.02",file.Path);
        Assert.Equal(124,result.Code);
        Assert.Contains("Timeout",result.Err);
    }

    [Fact]
    public async Task Raw_vm_bytes_are_not_converted_to_text_on_stdout()
    {
        using var file=new SourceFile("io.write(string.char(0,255,195,169))");
        var result=await Invoke("run",file.Path);
        Assert.Equal(0,result.Code);
        Assert.Equal(new byte[]{0,255,195,169},result.Out);
    }

    [Fact]
    public async Task Runtime_error_text_is_not_utf8_decoded_twice()
    {
        using var file=new SourceFile("error('héllo')");
        var result=await Invoke("run",file.Path);
        Assert.Equal(1,result.Code);
        Assert.Contains("héllo",result.Err);
    }

    [Fact]
    public async Task Missing_file_is_an_expected_command_failure()
    {
        var result=await Invoke("run",System.IO.Path.Combine(System.IO.Path.GetTempPath(),Guid.NewGuid()+".cameo"));
        Assert.Equal(2,result.Code);
        Assert.Contains("CAMEOCLI003",result.Err);
    }

    private static async Task<(int Code,byte[] Out,string Err)> Invoke(params string[] args)
    {
        string? root=AppContext.BaseDirectory;
        while(root!=null&&!File.Exists(System.IO.Path.Combine(root,"Cameo.slnx")))root=System.IO.Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string configuration=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string dll=System.IO.Path.Combine(root,"src","Cameo.Cli","bin",configuration,"net11.0","cameo.dll");
        Assert.True(File.Exists(dll),"The Cameo CLI executable does not exist yet");
        using var process=new Process{StartInfo=new("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,StandardErrorEncoding=Encoding.UTF8,CreateNoWindow=true}};
        process.StartInfo.ArgumentList.Add(dll);
        foreach(string arg in args)process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var output=new MemoryStream();
        Task read=process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> errors=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}
        catch{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();throw;}
        await read;
        return(process.ExitCode,output.ToArray(),await errors);
    }

    [Fact]
    public async Task Embedded_example_runs_with_an_independent_host_loop()
    {
        string? root=AppContext.BaseDirectory;
        while(root!=null&&!File.Exists(System.IO.Path.Combine(root,"Cameo.slnx")))root=System.IO.Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string configuration=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string dll=System.IO.Path.Combine(root,"examples","host","bin",configuration,"net11.0","CameoHost.dll");
        Assert.True(File.Exists(dll),"The Cameo embedded host has not been implemented");
        using var process=new Process{StartInfo=new("dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,CreateNoWindow=true}};
        process.StartInfo.ArgumentList.Add(dll);
        process.StartInfo.ArgumentList.Add(System.IO.Path.Combine(root,"examples","05-embedded-events.cameo"));
        process.Start();
        Task<string> output=process.StandardOutput.ReadToEndAsync();
        Task<string> errors=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}
        catch{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();throw;}
        Assert.Equal(0,process.ExitCode);
        Assert.Equal("",await errors);
        Assert.Equal(File.ReadAllText(System.IO.Path.Combine(root,"examples","05-embedded-events.expected")).Replace("\r\n","\n"),(await output).Replace("\r\n","\n"));
    }

    private sealed class SourceFile : IDisposable
    {
        private readonly string _directory;
        internal string Path {get;}
        internal SourceFile(string source,string name="script.cameo")
        {
            string parent=System.IO.Path.GetTempPath();
            if(!Directory.Exists(parent))throw new DirectoryNotFoundException(parent);
            _directory=System.IO.Path.Combine(parent,"cameo-cli-"+Guid.NewGuid().ToString("N"));
            if(Directory.Exists(_directory))throw new IOException("Temporary collision");
            Directory.CreateDirectory(_directory);
            Path=System.IO.Path.Combine(_directory,name);
            File.WriteAllText(Path,source);
        }
        public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
    }
}
