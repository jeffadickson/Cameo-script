using System.Text;
using System.Text.Json;
using Cameo;

namespace Cameo.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        CameoCommandParse parsed=CameoCommands.Parse(args);
        if(parsed.Error is { } error){WriteDiagnostic(error);return 2;}
        int exit=1;
        // A dedicated owner matches the qualified Windows stack configuration; the CLI
        // is not a UI thread. Actual GUI/TUI hosts use their own owner/context contract.
        var thread=new Thread(() =>
        {
            try
            {
                using Stream stdout=Console.OpenStandardOutput(),stderr=Console.OpenStandardError();
                CameoCommandResult result=CameoCommands.Execute(parsed.Command,Console.Out,Console.Error,stdout,stderr);
                if(result.Text is { } text)Console.Out.Write(text);
                if(result.Json && result.TestCases is { } tests)
                    Console.Out.WriteLine(JsonSerializer.Serialize(tests,CameoCommands.JsonOptions));
                else if(result.Json)Console.Out.WriteLine(JsonSerializer.Serialize(result.Diagnostics,CameoCommands.JsonOptions));
                else foreach(var diagnostic in result.Diagnostics)WriteDiagnostic(diagnostic);
                exit=result.ExitCode;
            }
            catch(Exception unexpected)
            {
                Console.Error.WriteLine("CAMEOINTERNAL: "+unexpected);
            }
        },8*1024*1024);
        thread.Start();thread.Join();return exit;
    }
    private static void WriteDiagnostic(CameoDiagnostic diagnostic)
    {
        string position=diagnostic.File??"cameo";
        if(diagnostic.Line is { } line)position+=$":{line}:{diagnostic.Column??1}";
        Console.Error.WriteLine($"{position}: {diagnostic.Code}: {diagnostic.Message}");
    }
}
