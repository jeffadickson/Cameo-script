using Cameo.Runtime.Vm;

namespace Cameo;

/// <summary>A supported author operation; internal parser and VM helpers are ordinary functions.</summary>
public abstract record CameoCommand;
/// <summary>Check independent files without executing script; Json selects machine-readable diagnostics.</summary>
/// <param name="Files">Nonempty source file paths.</param><param name="Json">Write structured diagnostics.</param>
public sealed record CameoCheckCommand(IReadOnlyList<string> Files, bool Json = false) : CameoCommand;
/// <summary>Run one file with explicit host capabilities and optional execution limits.</summary>
/// <param name="File">Source path.</param><param name="Arguments">Script arguments, not CLI options.</param>
/// <param name="AllowNetwork">Enable network library operations.</param><param name="AllowProcesses">Enable child programs.</param>
/// <param name="AllowDatabase">Enable database operations.</param><param name="Timeout">Positive, at most one day.</param>
/// <param name="MaxMemoryBytes">Optional process-wide managed-heap threshold sampled by the script watchdog;
/// not an isolated script budget, native-memory limit or development-job cap.</param>
/// <param name="Sandboxed">Run with pure-compute <c>CameoLibraries.Sandboxed</c> profile (no io/fs/aio/db/debug).</param>
/// <param name="MaxSteps">Optional deterministic bytecode step budget.</param>
/// <param name="Jit">Enable automatic hot-function IL tiering.</param>
/// <param name="Hermetic">Run with strictly deterministic <c>CameoLibraries.Hermetic</c> profile (no wall-clock, RNG, I/O, or debug).</param>
public sealed record CameoRunCommand(string File, IReadOnlyList<string> Arguments,
    bool AllowNetwork = false, bool AllowProcesses = false, bool AllowDatabase = false,
    TimeSpan? Timeout = null, long? MaxMemoryBytes = null, bool Sandboxed = false,
    long? MaxSteps = null, bool Jit = false, bool Hermetic = false) : CameoCommand;
/// <summary>Evaluate an inline snippet or Perl-style line-processing one-liner (-e, -n, -p, -a, -F, -i).</summary>
public sealed record CameoEvalCommand(string Code, IReadOnlyList<string> Arguments,
    bool LineMode = false, bool PrintLines = false, bool AutoSplit = false, string? FieldSeparator = null,
    bool InPlace = false, string? BackupSuffix = null, string? BeginCode = null, string? EndCode = null,
    bool AllowNetwork = false, bool AllowProcesses = false, bool AllowDatabase = false, bool Sandboxed = false,
    bool TypeCheck = true, TimeSpan? Timeout = null, long? MaxMemoryBytes = null,
    long? MaxSteps = null, bool Jit = false, bool Hermetic = false) : CameoCommand;
/// <summary>Compile a Cameo file and emit source-annotated VM bytecode.</summary>
/// <param name="File">Source path.</param>
/// <param name="TypeCheck">Whether to type-check before compiling.</param>
public sealed record CameoDumpCommand(string File, bool TypeCheck = true) : CameoCommand;
/// <summary>Run an interactive or piped Cameo REPL session.</summary>
/// <param name="AllowNetwork">Enable network library operations.</param>
/// <param name="AllowProcesses">Enable child programs.</param>
/// <param name="AllowDatabase">Enable database operations.</param>
/// <param name="Sandboxed">Run under <c>CameoLibraries.Sandboxed</c>.</param>
/// <param name="Hermetic">Run under strictly deterministic <c>CameoLibraries.Hermetic</c>.</param>
public sealed record CameoReplCommand(bool AllowNetwork = true, bool AllowProcesses = true,
    bool AllowDatabase = true, bool Sandboxed = false, bool Hermetic = false) : CameoCommand;
/// <summary>Run explicitly named test scripts in fresh engines, with no interactive stdin.</summary>
/// <param name="Files">Nonempty paths, executed in ordinal order.</param>
/// <param name="Json">Emit machine-readable case outcomes.</param>
/// <param name="Timeout">Per-case script execution timeout. Defaults to 30 seconds.</param>
public sealed record CameoTestCommand(IReadOnlyList<string> Files, bool Json = false,
    TimeSpan? Timeout = null) : CameoCommand;
/// <summary>Format Cameo source files into canonical style, or verify formatting with CheckOnly.</summary>
/// <param name="Files">Nonempty source file paths.</param>
/// <param name="CheckOnly">When true, do not modify files and exit non-zero if any file needs formatting.</param>
public sealed record CameoFmtCommand(IReadOnlyList<string> Files, bool CheckOnly = false) : CameoCommand;
/// <summary>Validate and compile a Cameo source file into a static CEMB v1 image, C++20 header, and/or UART hex payload.</summary>
public sealed record CameoEmbedCommand(
    string File,
    string? OutBin = null,
    string? OutHeader = null,
    string? OutHex = null,
    string SymbolPrefix = "kCameoProgram",
    string CppNamespace = "cameo::programs",
    bool TypeCheck = true,
    bool AllowFloats = false,
    bool AllowRecordsAndArrays = true,
    bool AllowMath = true,
    bool BootOnlyClosures = false,
    bool AllowMemWrites = true,
    bool Json = false,
    IReadOnlyList<string>? HostFunctions = null,
    string? OutMap = null,
    string? GenRegMap = null,
    string? OutRegMap = null,
    string? TraceToFst = null,
    string? OutFst = null,
    string? UploadEndpoint = null,
    string? RunEntry = null,
    int? SaveSlot = null) : CameoCommand;
/// <summary>Describe the supported command-line interface.</summary>
public sealed record CameoHelpCommand : CameoCommand;
/// <summary>Discover stable command identifiers and typed request models.</summary>
public sealed record CameoDescribeCommand : CameoCommand;
/// <summary>Expected command/compile/runtime failure, or a checker warning, in host Unicode text.</summary>
/// <param name="Code">Stable identifier.</param><param name="Message">Actionable host text.</param>
/// <param name="File">Original host path, without byte-string re-decoding.</param><param name="Line">One-based line.</param>
/// <param name="Column">One-based byte-oriented source column.</param><param name="Warning">Nonfatal checker warning.</param>
public sealed record CameoDiagnostic(string Code, string Message, string? File = null,
    int? Line = null, int? Column = null, bool Warning = false);
/// <summary>Argument parsing returns either one typed command or an expected diagnostic.</summary>
/// <param name="Command">Validated request shape, or null.</param><param name="Error">Usage failure, or null.</param>
public sealed record CameoCommandParse(CameoCommand? Command, CameoDiagnostic? Error);
/// <summary>Explicit command outcome. Script output travels through the supplied output sink.</summary>
/// <param name="ExitCode">Process-style outcome.</param><param name="Diagnostics">Structured expected failures/warnings.</param>
/// <param name="Text">Help/discovery text when requested.</param><param name="Json">Diagnostics should be serialized.</param>
public sealed record CameoCommandResult(int ExitCode, IReadOnlyList<CameoDiagnostic> Diagnostics,
    string? Text = null, bool Json = false)
{
    /// <summary>Captured case outcomes for a valid test run, or null for other commands.</summary>
    public IReadOnlyList<CameoTestCaseResult>? TestCases { get; init; }
}
/// <summary>Why an individual test script stopped.</summary>
public enum CameoTestOutcome
{
    /// <summary>The script returned normally.</summary>
    Completed,
    /// <summary>The script could not be parsed, checked or compiled.</summary>
    CompileFailed,
    /// <summary>The script raised an unhandled error.</summary>
    RuntimeFailed,
    /// <summary>The script explicitly exited. A zero exit code passes.</summary>
    Exited,
    /// <summary>The engine stopped execution because of an abort.</summary>
    Aborted,
    /// <summary>The test source could not be found.</summary>
    MissingSource,
    /// <summary>Reading or executing the source hit a host I/O failure.</summary>
    IoFailed
}
/// <summary>Outcome of one script, including its captured Unicode stdout and stderr.</summary>
/// <param name="File">Original host path.</param>
/// <param name="Outcome">How execution finished.</param>
/// <param name="StandardOutput">Text captured from the VM's standard output.</param>
/// <param name="StandardError">Text captured from the VM's standard error.</param>
/// <param name="Diagnostics">Structured expected failures.</param>
/// <param name="ScriptExitCode">The script's explicit exit code, if any.</param>
/// <param name="AbortReason">The engine's reason for aborting the case, if any.</param>
public sealed record CameoTestCaseResult(string File, CameoTestOutcome Outcome, string StandardOutput,
    string StandardError, IReadOnlyList<CameoDiagnostic> Diagnostics, int? ScriptExitCode = null,
    AbortReason? AbortReason = null)
{
    /// <summary>Normal completion or an explicit zero exit code passes.</summary>
    public bool Passed => Outcome == CameoTestOutcome.Completed ||
        (Outcome == CameoTestOutcome.Exited && ScriptExitCode == 0);
}
/// <summary>Undo semantics of a user operation.</summary>
public enum CameoUndoPolicy
{
    /// <summary>Read-only inspection does not change application state.</summary>
    NotApplicable,
    /// <summary>Script effects require script/application-specific recovery; no automatic rollback.</summary>
    ScriptOwned
}
/// <summary>Stable command discovery backed by the same typed handlers as the CLI.</summary>
/// <param name="Id">Stable operation identifier.</param><param name="Usage">Supported CLI shape.</param>
/// <param name="InputModel">Managed typed request name.</param><param name="Undo">Explicit recovery policy.</param>
public sealed record CameoCommandDescriptor(string Id, string Usage, string InputModel, CameoUndoPolicy Undo);
