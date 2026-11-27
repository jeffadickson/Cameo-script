using Cameo.Runtime;

namespace Cameo;

/// <summary>Shared typed command registry, validation and handlers; no interactive prompts.</summary>
public static partial class CameoCommands
{
    /// <summary>The supported operation set. MCP transport is a separately tracked capability.</summary>
    public static IReadOnlyList<CameoCommandDescriptor> Descriptions { get; } = Array.AsReadOnly<CameoCommandDescriptor>([
        new("cameo.check", "check [--json] <file.cameo>...", nameof(CameoCheckCommand), CameoUndoPolicy.NotApplicable),
        new("cameo.run", "run [options] <file.cameo> [args...]", nameof(CameoRunCommand), CameoUndoPolicy.ScriptOwned),
        new("cameo.eval", "[-n|-p] [-a] [-F sep] [-i[.bak]] [--begin code] [--end code] -e <code> [args|files...]", nameof(CameoEvalCommand), CameoUndoPolicy.ScriptOwned),
        new("cameo.dump", "dump [--no-check] <file.cameo>", nameof(CameoDumpCommand), CameoUndoPolicy.NotApplicable),
        new("cameo.repl", "repl [--sandboxed|--hermetic]", nameof(CameoReplCommand), CameoUndoPolicy.ScriptOwned),
        new("cameo.fmt", "fmt [--check] [--migrate] <file.cameo>...", nameof(CameoFmtCommand), CameoUndoPolicy.ScriptOwned),
        new("cameo.embed", "embed [--out-bin <file.cemb>] [--out-header <file.hpp>] [--out-hex <file.hex>] [--symbol <name>] [--namespace <ns>] [--host-fn <name>]... [--floats|--no-floats] [--no-records] [--no-math] [--read-only] [--boot-only-closures] [--no-check] [--json] <file.cameo>", nameof(CameoEmbedCommand), CameoUndoPolicy.ScriptOwned),
        new("cameo.test", "test [--json] [--timeout <seconds>] <file.cameo>...", nameof(CameoTestCommand), CameoUndoPolicy.ScriptOwned),
        new("cameo.help", "--help", nameof(CameoHelpCommand), CameoUndoPolicy.NotApplicable),
        new("cameo.describe", "commands", nameof(CameoDescribeCommand), CameoUndoPolicy.NotApplicable)
    ]);

    private static CameoCommandParse Invalid(string message, string code = "CAMEOCLI002") => new(null,new(code,message));

    /// <summary>Parses CLI arguments into one typed request. Missing/invalid input is a result.</summary>
    public static CameoCommandParse Parse(IReadOnlyList<string>? args)
    {
        if(args == null || args.Any(a=>a==null)) return Invalid("An argument list with non-null strings is required");
        if(args.Count==0 || args[0] is "--help" or "-h" or "help") return new(new CameoHelpCommand(),null);
        if(args[0]=="commands") return args.Count==1 ? new(new CameoDescribeCommand(),null) : Invalid("commands takes no arguments");
        if(args[0]=="test") return ParseTest(args);
        if(args[0]=="dump") return ParseDump(args);
        if(args[0]=="repl") return ParseRepl(args);
        if(args[0]=="fmt") return ParseFmt(args);
        if(args[0]=="embed") return ParseEmbed(args);
        if(args[0]=="check")
        {
            bool json=false; var files=new List<string>();
            foreach(string item in args.Skip(1))
            {
                if(item=="--json")json=true;
                else if(item.StartsWith('-'))return Invalid($"Unknown check option: {item}");
                else files.Add(item);
            }
            return files.Count==0 ? Invalid("check requires at least one file") : new(new CameoCheckCommand(files.ToArray(),json),null);
        }
        if(IsEvalInvocation(args)) return ParseEval(args);
        int i=args[0]=="run" ? 1 : args[0].EndsWith(".cameo",StringComparison.OrdinalIgnoreCase) ? 0 : -1;
        if(i<0)return Invalid($"Unknown command: {args[0]}");
        bool network=false,processes=false,database=false,sandboxed=false,hermetic=false,jit=false;TimeSpan? timeout=null;long? memory=null,maxSteps=null;
        while(i<args.Count && args[i].StartsWith('-'))
        {
            string option=args[i++];
            if(option=="--")break;
            if(option=="--allow-net"){network=true;continue;}
            if(option=="--allow-run"){processes=true;continue;}
            if(option=="--allow-db"){database=true;continue;}
            if(option=="--sandboxed"){sandboxed=true;continue;}
            if(option=="--hermetic"){hermetic=true;continue;}
            if(option=="--jit"){jit=true;continue;}
            if(option is not ("--timeout" or "--max-memory" or "--max-steps"))return Invalid($"Unknown run option: {option}");
            if(i>=args.Count)return Invalid($"{option} requires a value");
            string value=args[i++];
            if(option=="--timeout")
            {
                if(!RunLimitParser.TryTimeout(value,out TimeSpan parsedTimeout))
                    return Invalid("--timeout must be finite, greater than zero, at most86400 seconds and at least one runtime tick","CAMEOCLI004");
                timeout=parsedTimeout;
            }
            else if(option=="--max-steps")
            {
                if(!long.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out long steps) || steps<=0)
                    return Invalid("--max-steps requires a positive decimal integer","CAMEOCLI004");
                maxSteps=steps;
            }
            else
            {
                if(!RunLimitParser.TryMemoryMiB(value,out long bytes))
                    return Invalid("--max-memory requires a positive Int32 MiB value","CAMEOCLI004");
                memory=bytes;
            }
        }
        if(i>=args.Count || string.IsNullOrWhiteSpace(args[i]))return Invalid("run requires a source file");
        return new(new CameoRunCommand(args[i],args.Skip(i+1).ToArray(),network,processes,database,timeout,memory,sandboxed,maxSteps,jit,hermetic),null);
    }

    private static CameoCommandParse ParseDump(IReadOnlyList<string> args)
    {
        bool typeCheck = true;
        string? file = null;
        foreach (string item in args.Skip(1))
        {
            if (item == "--no-check") typeCheck = false;
            else if (item.StartsWith('-')) return Invalid($"Unknown dump option: {item}");
            else if (file != null) return Invalid("dump takes exactly one file");
            else file = item;
        }
        return string.IsNullOrWhiteSpace(file) ? Invalid("dump requires a source file") : new(new CameoDumpCommand(file, typeCheck), null);
    }

    private static CameoCommandParse ParseRepl(IReadOnlyList<string> args)
    {
        bool sandboxed = false, hermetic = false;
        foreach (string item in args.Skip(1))
        {
            if (item == "--sandboxed") sandboxed = true;
            else if (item == "--hermetic") hermetic = true;
            else return Invalid($"Unknown repl option: {item}");
        }
        bool restricted = sandboxed || hermetic;
        return new(new CameoReplCommand(!restricted, !restricted, !restricted, sandboxed, hermetic), null);
    }

    private static CameoCommandParse ParseFmt(IReadOnlyList<string> args)
    {
        bool checkOnly = false;
        bool migrate = false;
        var files = new List<string>();
        int i = 1;
        for (; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--")
            {
                files.AddRange(args.Skip(i + 1));
                break;
            }
            if (arg == "--check") checkOnly = true;
            else if (arg == "--migrate") migrate = true;
            else if (arg.StartsWith('-')) return Invalid($"Unknown fmt option: {arg}");
            else files.Add(arg);
        }
        if (files.Count == 0 || files.Any(string.IsNullOrWhiteSpace))
            return Invalid("fmt requires at least one source file");
        return new(new CameoFmtCommand(files.ToArray(), checkOnly, migrate), null);
    }

    private static bool IsEvalInvocation(IReadOnlyList<string> args)
    {
        if (args[0] == "eval") return true;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "-e" or "-n" or "-p" or "-a" or "-F" or "--begin" or "--end") return true;
            if (a.StartsWith("-i", StringComparison.Ordinal)) return true;
            if (a is "--timeout" or "--max-memory" or "--max-steps") { i++; continue; }
            if (!a.StartsWith('-')) return false;
        }
        return false;
    }

    private static CameoCommandParse ParseEval(IReadOnlyList<string> args)
    {
        int i = args[0] == "eval" ? 1 : 0;
        bool lineMode = false, printLines = false, autoSplit = false, inPlace = false;
        bool network = false, processes = false, database = false, sandboxed = false, hermetic = false, jit = false;
        bool? explicitCheck = null;
        string? separator = null, backup = null, code = null, begin = null, end = null;
        TimeSpan? timeout = null;
        long? memory = null, maxSteps = null;
        var rest = new List<string>();
        while (i < args.Count)
        {
            string a = args[i++];
            if (code != null) { rest.Add(a); continue; }
            if (a == "--") { rest.AddRange(args.Skip(i)); break; }
            if (a == "-n") { lineMode = true; continue; }
            if (a == "-p") { lineMode = true; printLines = true; continue; }
            if (a == "-a") { autoSplit = true; continue; }
            if (a == "--allow-net") { network = true; continue; }
            if (a == "--allow-run") { processes = true; continue; }
            if (a == "--allow-db") { database = true; continue; }
            if (a == "--sandboxed") { sandboxed = true; continue; }
            if (a == "--hermetic") { hermetic = true; continue; }
            if (a == "--jit") { jit = true; continue; }
            if (a == "--no-check") { explicitCheck = false; continue; }
            if (a == "--check") { explicitCheck = true; continue; }
            if (a.StartsWith("-i", StringComparison.Ordinal))
            {
                inPlace = true;
                backup = a.Length > 2 ? a[2..] : null;
                continue;
            }
            if (a is "-e" or "-F" or "--begin" or "--end" or "--timeout" or "--max-memory" or "--max-steps")
            {
                if (i >= args.Count) return Invalid($"{a} requires a value");
                string val = args[i++];
                switch (a)
                {
                    case "-e": code = val; break;
                    case "-F": autoSplit = true; separator = val; break;
                    case "--begin": begin = val; break;
                    case "--end": end = val; break;
                    case "--timeout":
                        if (!RunLimitParser.TryTimeout(val, out TimeSpan pt))
                            return Invalid("--timeout must be finite, greater than zero, at most 86400 seconds and at least one runtime tick", "CAMEOCLI004");
                        timeout = pt;
                        break;
                    case "--max-memory":
                        if (!RunLimitParser.TryMemoryMiB(val, out long mb))
                            return Invalid("--max-memory requires a positive Int32 MiB value", "CAMEOCLI004");
                        memory = mb;
                        break;
                    case "--max-steps":
                        if (!long.TryParse(val, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long steps) || steps <= 0)
                            return Invalid("--max-steps requires a positive decimal integer", "CAMEOCLI004");
                        maxSteps = steps;
                        break;
                }
                continue;
            }
            if (a.StartsWith('-')) return Invalid($"Unknown eval option: {a}");
            return Invalid($"Unexpected argument before -e: {a}");
        }
        if (code == null) return Invalid("eval requires -e '<code>'");
        if (inPlace && !lineMode) return Invalid("-i requires -n or -p");
        if (inPlace && rest.Count == 0) return Invalid("-i edits files in place: name the files");
        bool typeCheck = explicitCheck ?? !lineMode;
        return new(new CameoEvalCommand(code, rest.ToArray(), lineMode, printLines, autoSplit, separator,
            inPlace, backup, begin, end, network, processes, database, sandboxed, typeCheck, timeout, memory, maxSteps, jit, hermetic), null);
    }

    private static CameoCommandParse ParseTest(IReadOnlyList<string> args)
    {
        bool json = false;
        TimeSpan? timeout = null;
        var files = new List<string>();
        int i = 1;
        for (; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--")
            {
                files.AddRange(args.Skip(i + 1));
                break;
            }
            if (arg == "--json") json = true;
            else if (arg == "--timeout")
            {
                if (++i >= args.Count) return Invalid("--timeout requires a value");
                if (!RunLimitParser.TryTimeout(args[i], out TimeSpan limit))
                    return Invalid("--timeout must be finite, positive, at most 86400 seconds and at least one runtime tick",
                        "CAMEOCLI004");
                timeout = limit;
            }
            else if (arg.StartsWith('-')) return Invalid($"Unknown test option: {arg}");
            else files.Add(arg);
        }
        if (files.Count == 0 || files.Any(string.IsNullOrWhiteSpace))
            return Invalid("test requires at least one nonempty source path");
        return new(new CameoTestCommand(files.ToArray(), json, timeout), null);
    }

    private static CameoCommandParse ParseEmbed(IReadOnlyList<string> args)
    {
        string? file = null, outBin = null, outHeader = null, outHex = null, outMap = null;
        string? genRegMap = null, outRegMap = null, traceToFst = null, outFst = null;
        string? uploadEndpoint = null, runEntry = null;
        int? saveSlot = null;
        string symbol = "kCameoProgram", ns = "cameo::programs";
        bool typeCheck = true, allowFloats = false, allowRecords = true, allowMath = true,
            bootOnlyClosures = false, allowMemWrites = true, json = false;
        var hostFns = new List<string>();
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--no-check") typeCheck = false;
            else if (arg == "--floats") allowFloats = true;
            else if (arg == "--no-floats") allowFloats = false;
            else if (arg is "--no-records" or "--no-tables") allowRecords = false;
            else if (arg == "--no-math") allowMath = false;
            else if (arg == "--boot-only-closures") bootOnlyClosures = true;
            else if (arg == "--read-only") allowMemWrites = false;
            else if (arg == "--json") json = true;
            else if (arg is "--out-bin" or "--out-header" or "--out-hex" or "--out-map" or
                     "--symbol" or "--namespace" or "--host-fn" or
                     "--gen-regmap" or "--out-regmap" or "--trace-to-fst" or "--out-fst" or
                     "--upload" or "--run-entry" or "--save-slot")
            {
                if (++i >= args.Count || string.IsNullOrWhiteSpace(args[i]))
                    return Invalid($"{arg} requires a value");
                if (arg == "--out-bin") outBin = args[i];
                else if (arg == "--out-header") outHeader = args[i];
                else if (arg == "--out-hex") outHex = args[i];
                else if (arg == "--out-map") outMap = args[i];
                else if (arg == "--symbol") symbol = args[i];
                else if (arg == "--namespace") ns = args[i];
                else if (arg == "--gen-regmap") genRegMap = args[i];
                else if (arg == "--out-regmap") outRegMap = args[i];
                else if (arg == "--trace-to-fst") traceToFst = args[i];
                else if (arg == "--out-fst") outFst = args[i];
                else if (arg == "--upload") uploadEndpoint = args[i];
                else if (arg == "--run-entry") runEntry = args[i];
                else if (arg == "--save-slot")
                {
                    if (!int.TryParse(args[i], out int slot) || slot < 0)
                        return Invalid("--save-slot must be a non-negative integer");
                    saveSlot = slot;
                }
                else hostFns.Add(args[i]);
            }
            else if (arg.StartsWith('-')) return Invalid($"Unknown embed option: {arg}");
            else if (file != null) return Invalid("embed takes exactly one source file");
            else file = arg;
        }
        bool standaloneTool = !string.IsNullOrWhiteSpace(genRegMap) || !string.IsNullOrWhiteSpace(traceToFst);
        if (string.IsNullOrWhiteSpace(file) && !standaloneTool)
            return Invalid("embed requires a source file");
        return new(new CameoEmbedCommand(file ?? "", outBin, outHeader, outHex, symbol, ns,
            typeCheck, allowFloats, allowRecords, allowMath, bootOnlyClosures, allowMemWrites, json,
            hostFns.Count == 0 ? null : hostFns.ToArray(),
            outMap, genRegMap, outRegMap, traceToFst, outFst, uploadEndpoint, runEntry, saveSlot), null);
    }
}
