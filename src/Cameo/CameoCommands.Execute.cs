using System.Text.Json;
using System.Text.Json.Serialization;
using Cameo.Runtime;
using Cameo.Runtime.Vm;
using Cameo.Runtime.Syntax;

namespace Cameo;

public static partial class CameoCommands
{
    /// <summary>Serializer policy shared by command discovery and CLI diagnostic output.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        Converters={new JsonStringEnumConverter()}
    };

    private static CameoCommandResult Fail(string code,string message,string? file=null,int exit=2) =>
        new(exit,[new(code,message,file)]);

    private static CameoDiagnostic ConvertDiagnostic(Diagnostic diagnostic)
    {
        string message=LuaText.Decode(diagnostic.Message);
        string code=diagnostic.IsWarning ? "CAMEOWARNING" : "CAMEOTYPE";
        int colon=message.IndexOf(':');
        if(message.StartsWith("CAMEO",StringComparison.Ordinal) && colon>0)
        {code=message[..colon];message=message[(colon+1)..].TrimStart();}
        return new(code,message,diagnostic.File,diagnostic.Line,diagnostic.Col,diagnostic.IsWarning);
    }

    /// <summary>Executes a validated request with structured expected outcomes. No dialogs.
    /// Byte sinks preserve raw VM output; text-only hosts can supply writers instead.
    /// Run effects are script-owned and have no general undo/redo guarantee.</summary>
    public static CameoCommandResult Execute(CameoCommand? command,TextWriter output,TextWriter error,
        Stream? outputBytes=null,Stream? errorBytes=null,TextReader? input=null)
    {
        ArgumentNullException.ThrowIfNull(output);ArgumentNullException.ThrowIfNull(error);
        try
        {
            switch(command)
            {
                case CameoHelpCommand:
                    return new(0,[],"Cameo0.1 - experimental language on the shared Cameo.Runtime engine\n\n"+
                        string.Join("\n",Descriptions.Select(d=>"  cameo "+d.Usage))+
                        "\n\nRun options: --allow-net --allow-run --allow-db --sandboxed --hermetic --jit --timeout <seconds> --max-memory <MiB> --max-steps <steps>\n"+
                        "Test execution timeout defaults to 30 seconds per file.\n");
                case CameoDescribeCommand:
                    return new(0,[],JsonSerializer.Serialize(Descriptions,JsonOptions)+"\n");
                case CameoTestCommand test:
                    return ExecuteTests(test);
                case CameoDumpCommand dump:
                    return ExecuteDump(dump);
                case CameoReplCommand repl:
                    return ExecuteRepl(repl, output, error, outputBytes, errorBytes, input);
                case CameoFmtCommand fmt:
                    return ExecuteFmt(fmt);
                case CameoEmbedCommand embed:
                    return ExecuteEmbed(embed);
                case CameoCheckCommand check:
                {
                    if(check.Files==null || check.Files.Count==0)return Fail("CAMEOCLI002","check requires source files");
                    var diagnostics=new List<CameoDiagnostic>();
                    foreach(string path in check.Files.ToArray())
                    {
                        if(!File.Exists(path)){diagnostics.Add(new("CAMEOCLI003","Source file not found",path));continue;}
                        using var engine=new CameoEngine();
                        diagnostics.AddRange(engine.CheckFile(path).Select(ConvertDiagnostic));
                        diagnostics.AddRange(engine.Runtime.Warnings.Select(d =>
                            d.Message.StartsWith("internal type checker error:",StringComparison.Ordinal)
                                ? ConvertDiagnostic(d) with {Code="CAMEOINTERNAL",Warning=false}
                                : ConvertDiagnostic(d)));
                    }
                    int status=diagnostics.Any(d=>!d.Warning) ? 1 : 0;
                    return new(status,diagnostics,Json:check.Json);
                }
                case CameoRunCommand run:
                {
                    if(!File.Exists(run.File))return Fail("CAMEOCLI003","Source file not found",run.File);
                    if(run.Arguments==null)return Fail("CAMEOCLI002","Script argument list is required",run.File);
                    if(run.Timeout is { } timeout && (timeout<=TimeSpan.Zero || timeout>TimeSpan.FromDays(1)))
                        return Fail("CAMEOCLI004","Timeout must be positive and at most one day",run.File);
                    if(run.MaxMemoryBytes is <=0)return Fail("CAMEOCLI004","Requested managed-memory threshold must be positive",run.File);
                    if(run.MaxSteps is <=0)return Fail("CAMEOCLI004","Step budget must be positive",run.File);
                    bool restricted = run.Hermetic || run.Sandboxed;
                    using var engine=new CameoEngine(new CameoRuntimeOptions
                    {
                        Libraries=run.Hermetic ? Cameo.Runtime.Stdlib.CameoLibraries.Hermetic
                            : run.Sandboxed ? Cameo.Runtime.Stdlib.CameoLibraries.Sandboxed
                            : Cameo.Runtime.Stdlib.CameoLibraries.All,
                        AllowNetwork=!restricted && run.AllowNetwork,
                        AllowProcesses=!restricted && run.AllowProcesses,
                        AllowDatabase=!restricted && run.AllowDatabase,
                        AllowDebug=!restricted,
                        FilePolicy=restricted ? FilePolicy.DenyAll : null,
                        Timeout=run.Timeout,MaxMemoryBytes=run.MaxMemoryBytes,
                        MaxSteps=run.MaxSteps,JitTierThreshold=run.Jit ? 50 : null
                    });
                    engine.State.Stdout=output;engine.State.Stderr=error;
                    engine.State.StdoutBytes=outputBytes;engine.State.StderrBytes=errorBytes;
                    if (input != null) engine.State.Stdin = input;
                    RunResult result=engine.TryRunFile(run.File,run.Arguments.ToArray());
                    engine.State.FlushOut();engine.State.FlushErr();
                    return MapRunResult(result, run.File);
                }
                case CameoEvalCommand eval:
                    return ExecuteEval(eval, output, error, outputBytes, errorBytes, input);
                default:return Fail("CAMEOCLI002","A supported typed command is required");
            }
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
            return Fail("CAMEOCLI003",exception.Message);
        }
    }

    private static CameoCommandResult ExecuteFmt(CameoFmtCommand fmt)
    {
        string[]? files = fmt.Files?.ToArray();
        if (files == null || files.Length == 0 || files.Any(string.IsNullOrWhiteSpace))
            return Fail("CAMEOCLI002", "fmt requires at least one source file");

        var diagnostics = new List<CameoDiagnostic>();
        var unformatted = new List<string>();
        foreach (string path in files)
        {
            if (!File.Exists(path))
            {
                diagnostics.Add(new("CAMEOCLI003", "Source file not found", path));
                continue;
            }
            string original = File.ReadAllText(path);
            CameoFormatOutcome outcome = fmt.Migrate
                ? CameoFormatter.TryMigrate(original, path)
                : CameoFormatter.TryFormat(original, path);
            if (outcome.Diagnostic != null)
            {
                diagnostics.Add(outcome.Diagnostic);
                continue;
            }
            if (outcome.Changed)
            {
                if (fmt.CheckOnly)
                {
                    unformatted.Add(path);
                }
                else
                {
                    File.WriteAllText(path, outcome.Formatted!, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }
        }

        if (diagnostics.Count > 0)
            return new(1, diagnostics);
        if (fmt.CheckOnly && unformatted.Count > 0)
        {
            string text = string.Join("\n", unformatted.Select(p => $"needs formatting: {p}")) + "\n";
            return new(1, [], text);
        }
        return new(0, []);
    }

    private static CameoCommandResult ExecuteDump(CameoDumpCommand dump)
    {
        if (!File.Exists(dump.File)) return Fail("CAMEOCLI003", "Source file not found", dump.File);
        using var engine = new CameoEngine(new CameoRuntimeOptions { TypeCheck = dump.TypeCheck });
        try
        {
            string rawSource = File.ReadAllText(dump.File);
            Proto proto = engine.Runtime.CompileFile(dump.File);
            var sb = new System.Text.StringBuilder();
            FormatProto(proto, rawSource.Split('\n'), sb, 0);
            return new(0, [], sb.ToString());
        }
        catch (CameoCompileException ex)
        {
            return new(1, ex.Diagnostics.Select(ConvertDiagnostic).ToArray());
        }
    }

    private static void FormatProto(Proto p, string[]? source, System.Text.StringBuilder sb, int depth)
    {
        string indent = new(' ', depth * 2);
        sb.AppendLine($"{indent}function {p.Name} ({p.NumParams} params{(p.IsVarArg ? ", ..." : "")}, {p.MaxStack} regs, {p.Code.Length} ops)");
        int lastLine = -1;
        for (int pc = 0; pc < p.Code.Length; pc++)
        {
            int ln = p.LineOf(pc);
            if (source != null && ln != lastLine && ln >= 1 && ln <= source.Length)
            {
                sb.AppendLine($"{indent}  -- {ln}: {source[ln - 1].TrimEnd('\r')}");
                lastLine = ln;
            }
            Instruction ins = p.At(pc);
            sb.AppendLine($"{indent}  {pc,4}: {ins}");
        }
        foreach (Proto child in p.Protos) FormatProto(child, source, sb, depth + 1);
    }

    private static CameoCommandResult ExecuteRepl(CameoReplCommand repl, TextWriter output, TextWriter error,
        Stream? outputBytes, Stream? errorBytes, TextReader? input)
    {
        TextReader reader = input ?? Console.In;
        bool interactive = input == null && !Console.IsInputRedirected;
        bool restricted = repl.Hermetic || repl.Sandboxed;
        using var engine = new CameoEngine(new CameoRuntimeOptions
        {
            TypeCheck = false,
            Libraries = repl.Hermetic ? Cameo.Runtime.Stdlib.CameoLibraries.Hermetic
                : repl.Sandboxed ? Cameo.Runtime.Stdlib.CameoLibraries.Sandboxed
                : Cameo.Runtime.Stdlib.CameoLibraries.All,
            AllowNetwork = !restricted && repl.AllowNetwork,
            AllowProcesses = !restricted && repl.AllowProcesses,
            AllowDatabase = !restricted && repl.AllowDatabase,
            AllowDebug = !restricted,
            FilePolicy = restricted ? FilePolicy.DenyAll : null,
        });
        engine.State.Stdout = output; engine.State.Stderr = error;
        engine.State.StdoutBytes = outputBytes; engine.State.StderrBytes = errorBytes;
        engine.State.Stdin = reader;
        if (interactive) output.WriteLine("Cameo REPL (type .exit to quit, .help for help)");
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            if (interactive) { output.Write(buffer.Length == 0 ? "cameo> " : "  ...> "); output.Flush(); }
            string? line = reader.ReadLine();
            if (line == null) break;
            if (buffer.Length == 0)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed is ".exit" or ".quit") break;
                if (trimmed == ".help")
                {
                    output.WriteLine("Commands: .exit / .quit to leave, .help for help. Expressions print automatically; globals persist.");
                    continue;
                }
            }
            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(line);
            string snippet = buffer.ToString();

            // Try as an expression first (`return <snippet>`); if that fails, try as statements.
            RunResult result = engine.TryRunString("return " + snippet, "=repl");
            if (result.Value is CompileFailed)
            {
                if (engine.Check(snippet, "=repl") is { Count: > 0 } diags && IsIncompleteReplInput(diags))
                    continue;
                string stmtSnippet = ExportTopLevelReplFn(snippet);
                RunResult stmtResult = engine.TryRunString(stmtSnippet, "=repl");
                buffer.Clear();
                ReportReplOutcome(stmtResult, engine, output, error, printValues: false);
            }
            else
            {
                buffer.Clear();
                ReportReplOutcome(result, engine, output, error, printValues: true);
            }
            engine.State.FlushOut(); engine.State.FlushErr();
        }
        return new(0, []);
    }

    private static string ExportTopLevelReplFn(string snippet)
    {
        ReadOnlySpan<char> trimmed = snippet.AsSpan().TrimStart();
        if (!trimmed.StartsWith("fn ", StringComparison.Ordinal)) return snippet;
        ReadOnlySpan<char> rest = trimmed[3..].TrimStart();
        int len = 0;
        while (len < rest.Length && (char.IsAsciiLetterOrDigit(rest[len]) || rest[len] == '_')) len++;
        if (len == 0 || len >= rest.Length || rest[len] != '(') return snippet;
        string name = rest[..len].ToString();
        return $"{snippet}\n_G[\"{name}\"] = {name}";
    }

    private static bool IsIncompleteReplInput(IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) return false;
        string msg = diagnostics[0].Message;
        return msg.Contains("<eof>", StringComparison.Ordinal) || msg.Contains("unfinished", StringComparison.Ordinal);
    }

    private static void ReportReplOutcome(RunResult result, CameoEngine engine, TextWriter output, TextWriter error, bool printValues)
    {
        switch (result.Value)
        {
            case RunCompleted done when printValues && done.Values.Length > 0:
                output.WriteLine(string.Join("\t", done.Values.Select(v => LuaText.Decode(engine.State.ToStringMeta(v)))));
                break;
            case CompileFailed cf:
                foreach (Diagnostic d in cf.Diagnostics) error.WriteLine(LuaText.Decode(d.ToString()));
                break;
            case RuntimeFailed rf:
                error.WriteLine(rf.Message);
                break;
            case RunAborted ab:
                error.WriteLine("Stopped: " + ab.Reason);
                break;
        }
    }

    private static CameoCommandResult MapRunResult(RunResult result, string sourceName) => result switch
    {
        RunCompleted => new(0,[]),
        CompileFailed failed => new(1,failed.Diagnostics.Select(ConvertDiagnostic).ToArray()),
        RuntimeFailed failed => Fail("CAMEORUNTIME",failed.Message,sourceName,1),
        RunAborted aborted => Fail("CAMEOABORT","Stopped: "+aborted.Reason,sourceName,
            aborted.Reason==AbortReason.Timeout ? 124 : aborted.Reason==AbortReason.Cancelled ? 130 : 125),
        RunExited exited => new(exited.Code,[])
    };

    private static CameoCommandResult ExecuteEval(CameoEvalCommand eval, TextWriter output, TextWriter error,
        Stream? outputBytes, Stream? errorBytes, TextReader? input)
    {
        if (eval.Code == null) return Fail("CAMEOCLI002", "eval requires code");
        if (eval.Arguments == null) return Fail("CAMEOCLI002", "Script argument list is required");
        if (eval.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)))
            return Fail("CAMEOCLI004", "Timeout must be positive and at most one day");
        if (eval.MaxMemoryBytes is <= 0)
            return Fail("CAMEOCLI004", "Requested managed-memory threshold must be positive");
        if (eval.MaxSteps is <= 0)
            return Fail("CAMEOCLI004", "Step budget must be positive");
        if (eval.InPlace && (!eval.LineMode || eval.Arguments.Count == 0))
            return Fail("CAMEOCLI002", "-i requires -n/-p and at least one file");

        bool restricted = eval.Hermetic || eval.Sandboxed;
        using var engine = new CameoEngine(new CameoRuntimeOptions
        {
            TypeCheck = eval.TypeCheck,
            Libraries = eval.Hermetic ? Cameo.Runtime.Stdlib.CameoLibraries.Hermetic
                : eval.Sandboxed ? Cameo.Runtime.Stdlib.CameoLibraries.Sandboxed
                : Cameo.Runtime.Stdlib.CameoLibraries.All,
            AllowNetwork = !restricted && eval.AllowNetwork,
            AllowProcesses = !restricted && eval.AllowProcesses,
            AllowDatabase = !restricted && eval.AllowDatabase,
            AllowDebug = !restricted,
            FilePolicy = restricted ? FilePolicy.DenyAll : null,
            Timeout = eval.Timeout,
            MaxMemoryBytes = eval.MaxMemoryBytes,
            MaxSteps = eval.MaxSteps,
            JitTierThreshold = eval.Jit ? 50 : null,
        });
        engine.State.Stdout = output; engine.State.Stderr = error;
        engine.State.StdoutBytes = outputBytes; engine.State.StderrBytes = errorBytes;
        if (input != null) engine.State.Stdin = input;

        string[] scriptArgs = eval.Arguments.ToArray();
        var argt = new Table();
        argt.SetInt(0, Value.FromText("(command line)"));
        var argv = new Value[scriptArgs.Length];
        for (int k = 0; k < scriptArgs.Length; k++) { argv[k] = Value.FromText(scriptArgs[k]); argt.SetInt(k + 1, argv[k]); }
        engine.State.Globals.SetStr("arg", argt);
        engine.State.ScriptArgs = scriptArgs;

        string source = eval.LineMode ? BuildOneLinerSource(eval) : eval.Code;
        string chunkName = eval.LineMode ? "(one-liner)" : "(command line)";
        RunResult result = eval.LineMode
            ? RunStringWithArgs(engine, source, chunkName, argv)
            : RunStringWithArgs(engine, source, chunkName, argv);
        engine.State.FlushOut(); engine.State.FlushErr();
        return MapRunResult(result, chunkName);
    }

    private static RunResult RunStringWithArgs(CameoEngine engine, string source, string chunkName, Value[] argv)
    {
        try
        {
            Value[] values = engine.Runtime.RunString(source, chunkName, argv);
            return new RunCompleted(values);
        }
        catch (CameoCompileException e) { return new CompileFailed(e.Diagnostics); }
        catch (LuaException e) { return new RuntimeFailed(e.Value, e.Message, e.Traceback); }
        catch (LuaAbortException e) { return new RunAborted(e.Reason); }
        catch (LuaExitException e) { return new RunExited(e.Code); }
    }

    private static string BuildOneLinerSource(CameoEvalCommand eval)
    {
        static string Lit(string s) => "\"" + string.Concat(LuaText.Encode(s).Select(ch => "\\" + ((int)ch).ToString("D3"))) + "\"";
        var src = new System.Text.StringBuilder();
        src.Append("var __files = {...}\n");
        if (eval.BeginCode != null) src.Append(eval.BeginCode).Append('\n');
        src.Append("var nr = 0\n");
        src.Append("if #__files == 0 { __files = {false} }\n");
        src.Append("for _, __file in ipairs(__files) {\n");
        src.Append("let filename = __file || '-'\n");
        if (eval.InPlace)
        {
            src.Append("let __out = {}\n");
            src.Append("fn print(...) { let t = table.pack(...) for i = 1, t.n { t[i] = tostring(t[i]) } __out[#__out + 1] = table.concat(t, '\\t') }\n");
        }
        src.Append("for line in (__file && io.lines(__file) || io.lines()) {\n");
        src.Append("nr += 1\n");
        if (eval.AutoSplit)
            src.Append("let fields = line.split(").Append(eval.FieldSeparator == null ? "" : Lit(eval.FieldSeparator)).Append(")\n");
        src.Append("for __once = 1, 1 {\n").Append(eval.Code).Append("\n}\n");
        if (eval.PrintLines) src.Append("if line != nil { print(line) }\n");
        src.Append("}\n");
        if (eval.InPlace)
        {
            if (eval.BackupSuffix != null)
                src.Append("assert(fs.copy(filename, filename .. ").Append(Lit(eval.BackupSuffix)).Append(", true))\n");
            src.Append("assert(fs.write(filename, table.concat(__out, '\\n') .. (#__out > 0 && '\\n' || '')))\n");
        }
        src.Append("}\n");
        if (eval.EndCode != null) src.Append(eval.EndCode).Append('\n');
        return src.ToString();
    }

    private static CameoCommandResult ExecuteTests(CameoTestCommand command)
    {
        // Snapshot and validate the whole request before executing any script.
        string[]? files = command.Files?.ToArray();
        if (files is null || files.Length == 0 || files.Any(string.IsNullOrWhiteSpace))
            return Fail("CAMEOCLI002", "test requires at least one nonempty source path");
        TimeSpan timeout = command.Timeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
            return Fail("CAMEOCLI004", "Timeout must be positive and at most one day");

        Array.Sort(files, StringComparer.Ordinal);
        CameoTestCaseResult[] cases = files.Select(path => ExecuteTestFile(path, timeout)).ToArray();
        IReadOnlyList<CameoDiagnostic> diagnostics = cases.SelectMany(result => result.Diagnostics).ToArray();
        string? summary = null;
        if (!command.Json)
        {
            int passed = cases.Count(result => result.Passed);
            summary = string.Join("\n", cases.Select(result =>
                $"{(result.Passed ? "PASS" : "FAIL")} {result.File} ({result.Outcome})"))
                + $"\n{passed}/{cases.Length} passed\n";
        }
        return new(cases.All(result => result.Passed) ? 0 : 1, diagnostics, summary, command.Json)
        {
            TestCases = cases
        };
    }

    private static CameoTestCaseResult ExecuteTestFile(string path, TimeSpan timeout)
    {
        using var output = new StringWriter { NewLine = "\n" };
        using var error = new StringWriter { NewLine = "\n" };
        CameoTestCaseResult Outcome(CameoTestOutcome kind, IReadOnlyList<CameoDiagnostic>? diagnostics = null,
            int? scriptExitCode = null, AbortReason? abort = null) =>
            new(path, kind, output.ToString(), error.ToString(), diagnostics ?? [], scriptExitCode, abort);

        if (!File.Exists(path))
            return Outcome(CameoTestOutcome.MissingSource, [new("CAMEOCLI003", "Source file not found", path)]);
        try
        {
            RunResult result;
            // Engine disposal runs before we take the output snapshot, so cleanup output
            // belongs to this case. Capabilities use the restricted host defaults.
            using (var engine = new CameoEngine(new CameoRuntimeOptions { Timeout = timeout }))
            {
                engine.State.Stdin = TextReader.Null;
                engine.State.Stdout = output;
                engine.State.Stderr = error;
                result = engine.TryRunFile(path);
            }

            return result switch
            {
                RunCompleted => Outcome(CameoTestOutcome.Completed),
                CompileFailed failed => Outcome(CameoTestOutcome.CompileFailed,
                    failed.Diagnostics.Select(ConvertDiagnostic).ToArray()),
                RuntimeFailed failed => Outcome(CameoTestOutcome.RuntimeFailed,
                    [new("CAMEORUNTIME", failed.Message, path)]),
                RunExited exited => Outcome(CameoTestOutcome.Exited, exited.Code == 0 ? [] :
                    [new("CAMEOTESTEXIT", $"Script exited with code {exited.Code}", path)], exited.Code),
                RunAborted aborted => Outcome(CameoTestOutcome.Aborted,
                    [new("CAMEOABORT", $"Stopped: {aborted.Reason}", path)], abort: aborted.Reason)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Outcome(CameoTestOutcome.IoFailed, [new("CAMEOCLI003", exception.Message, path)]);
        }
    }

    private static CameoCommandResult ExecuteEmbed(CameoEmbedCommand command)
    {
        var utf8NoBom = new System.Text.UTF8Encoding(false);
        static void EnsureParentDir(string path)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        // Standalone tool mode 1: SVD / board register-map codegen
        if (!string.IsNullOrWhiteSpace(command.GenRegMap))
        {
            try
            {
                string src = command.GenRegMap;
                string generated = File.Exists(src)
                    ? Cameo.Embed.SvdRegMapGenerator.GenerateFromSvd(File.ReadAllText(src), Path.GetFileName(src))
                    : Cameo.Embed.SvdRegMapGenerator.GenerateBuiltIn(src);
                if (!string.IsNullOrWhiteSpace(command.OutRegMap))
                {
                    EnsureParentDir(command.OutRegMap);
                    File.WriteAllText(command.OutRegMap, generated, utf8NoBom);
                    return new(0, [], $"Generated Cameo.Embed register map -> {command.OutRegMap}\n");
                }
                return new(0, [], generated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
            {
                return Fail("CAMEOEMB020", ex.Message, command.GenRegMap);
            }
        }

        // Standalone tool mode 2: BusTrace / NexRT NXTR binary trace -> GTKWave FST waveform
        if (!string.IsNullOrWhiteSpace(command.TraceToFst))
        {
            if (!File.Exists(command.TraceToFst))
                return Fail("CAMEOCLI003", "Trace input file not found", command.TraceToFst);
            string outFst = !string.IsNullOrWhiteSpace(command.OutFst)
                ? command.OutFst
                : Path.ChangeExtension(command.TraceToFst, ".fst");
            try
            {
                EnsureParentDir(outFst);
                int events = Cameo.Embed.BusTraceFstExporter.ExportFileToFst(command.TraceToFst, outFst);
                return new(0, [], $"Exported {events} trace events -> {outFst}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Fail("CAMEOEMB021", ex.Message, command.TraceToFst);
            }
        }

        if (!File.Exists(command.File))
            return Fail("CAMEOCLI003", "Source file not found", command.File);

        var hosts = command.HostFunctions?.Select(name => new Cameo.Embed.EmbedHostFunctionSpec(name)).ToArray() ?? [];
        var options = new Cameo.Embed.EmbedCompileOptions
        {
            TypeCheck = command.TypeCheck,
            AllowFloats = command.AllowFloats,
            AllowRecordsAndArrays = command.AllowRecordsAndArrays,
            AllowMath = command.AllowMath,
            BootOnlyClosures = command.BootOnlyClosures,
            AllowMemWrites = command.AllowMemWrites,
            HostFunctions = hosts,
        };

        Cameo.Embed.EmbedCompileResult compiled = Cameo.Embed.EmbedCompiler.CompileFile(command.File, options);
        if (!compiled.Success)
        {
            var diags = compiled.Diagnostics
                .Select(d => new CameoDiagnostic(d.Code, d.Message, d.File ?? command.File, d.Line, d.Column))
                .ToArray();
            return new(1, diags, Json: command.Json);
        }

        Cameo.Embed.EmbedCompiledImage image = compiled.Image!;
        try
        {
            if (!string.IsNullOrWhiteSpace(command.OutBin))
            {
                EnsureParentDir(command.OutBin);
                File.WriteAllBytes(command.OutBin, image.Bytes);
            }
            if (!string.IsNullOrWhiteSpace(command.OutHeader))
            {
                EnsureParentDir(command.OutHeader);
                File.WriteAllText(command.OutHeader, image.EmitCppHeader(command.SymbolPrefix, command.CppNamespace), utf8NoBom);
            }
            if (!string.IsNullOrWhiteSpace(command.OutHex))
            {
                EnsureParentDir(command.OutHex);
                File.WriteAllText(command.OutHex, image.ToHexString() + "\n", utf8NoBom);
            }
            if (!string.IsNullOrWhiteSpace(command.OutMap))
            {
                EnsureParentDir(command.OutMap);
                File.WriteAllText(command.OutMap, image.DebugMap.ToJson() + "\n", utf8NoBom);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("CAMEOCLI003", ex.Message, command.File);
        }

        string uploadSummary = "";
        if (!string.IsNullOrWhiteSpace(command.UploadEndpoint))
        {
            try
            {
                using var transport = Cameo.Embed.EmbedUploader.OpenEndpoint(command.UploadEndpoint);
                var up = Cameo.Embed.EmbedUploader.Upload(
                    transport, image, saveSlot: command.SaveSlot, runExport: command.RunEntry);
                if (!up.Success)
                    return Fail("CAMEOEMB022", up.Error ?? "Target upload failed", command.File);
                uploadSummary = $"Uploaded {up.BytesUploaded} bytes (target CRC 0x{up.TargetCrc32C ?? image.Report.Crc32C:X8})" +
                                (up.RunOutput is not null ? $": {up.RunOutput}\n" : "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                return Fail("CAMEOEMB022", ex.Message, command.File);
            }
        }

        Cameo.Embed.EmbedProgramReport r = image.Report;
        string summary = command.Json
            ? JsonSerializer.Serialize(r, JsonOptions) + "\n"
            : $"CEMB v{Cameo.Embed.EmbedBinaryFormat.Version}: {r.ImageBytes} bytes, CRC-32C 0x{r.Crc32C:X8}, flags 0x{(ushort)r.Flags:X4}, " +
              $"{r.ProtoCount} protos, {r.InstructionCount} instructions, " +
              $"req(stack={r.RequiredStackSlots}, frames={r.RequiredFrames}, globals={r.GlobalCount}, " +
              $"boxes={r.RequiredBoxes}, closures={r.RequiredClosures}, records={r.RequiredRecords}, slots={r.RequiredRecordSlots})\n" +
              uploadSummary;
        return new(0, [], summary);
    }
}
