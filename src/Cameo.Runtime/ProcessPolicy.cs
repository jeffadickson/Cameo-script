namespace Cameo.Runtime;

/// <summary>An immutable rule for one fully-qualified executable and one exact argument vector.</summary>
/// <remarks>Paths are normalized lexically, then compared ordinally on every platform.
/// This is not executable file-identity validation or operating-system isolation.</remarks>
public sealed class ProcessRule
{
    /// <summary>The lexically normalized, fully-qualified executable path.</summary>
    public string Executable { get; }

    /// <summary>The exact arguments, in order. A private copy is exposed read-only.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Whether this rule also allows pseudo-terminal launches. Ordinary launches remain allowed.</summary>
    public bool AllowPseudoTerminal { get; }

    /// <summary>Creates a rule without looking up or opening an executable.</summary>
    /// <param name="executable">A fully-qualified path; simple PATH-search names are rejected.
    /// On Windows the filename must include an explicit extension and cannot end in a dot or whitespace.</param>
    /// <param name="arguments">The complete argument vector. Empty strings are meaningful; nulls/NUL characters are invalid.</param>
    /// <param name="allowPseudoTerminal">Whether a matching request may also use a pseudo-terminal.</param>
    /// <exception cref="ArgumentException">The path is not fully qualified or an argument is invalid.</exception>
    public ProcessRule(string executable, IEnumerable<string> arguments, bool allowPseudoTerminal = false)
    {
        if (!TryNormalize(executable, out string normalized))
            throw new ArgumentException("A fully-qualified executable path without NUL characters is required", nameof(executable));
        ArgumentNullException.ThrowIfNull(arguments);
        string[] owned = arguments.ToArray();
        if (owned.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Process arguments must be non-null strings without NUL characters", nameof(arguments));
        Executable = normalized;
        Arguments = Array.AsReadOnly(owned);
        AllowPseudoTerminal = allowPseudoTerminal;
    }

    internal bool Matches(string executable, IReadOnlyList<string> arguments, bool pseudoTerminal)
    {
        if (!string.Equals(Executable, executable, StringComparison.Ordinal) ||
            arguments.Count != Arguments.Count || (pseudoTerminal && !AllowPseudoTerminal)) return false;
        for (int i = 0; i < Arguments.Count; i++)
            if (!string.Equals(Arguments[i], arguments[i], StringComparison.Ordinal)) return false;
        return true;
    }

    internal static bool TryNormalize(string? executable, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0') || !Path.IsPathFullyQualified(executable)) return false;
        try
        {
            // CreateProcess can append .exe to an extensionless Windows request, and
            // Win32 path handling can trim a terminal dot/space. Neither behavior may
            // silently authorize a different executable spelling than the host listed.
            if (OperatingSystem.IsWindows() &&
                (executable[^1] == '.' || char.IsWhiteSpace(executable[^1]) || !Path.HasExtension(executable))) return false;
            normalized = Path.GetFullPath(executable);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>An immutable allowlist that further restricts managed-host process permission.</summary>
/// <remarks>Use with <see cref="CameoRuntimeOptions.AllowProcesses"/>; a policy never grants that permission.
/// Rules can be shared by workers. They do not constrain the environment, working directory,
/// filesystem access, executable contents/identity, or arbitrary registered host functions.</remarks>
public sealed class ProcessPolicy
{
    private readonly ProcessRule[] _rules;

    /// <summary>Copies the supplied immutable rules. An empty collection denies every process request.</summary>
    /// <param name="rules">Rules to copy; every entry must be non-null.</param>
    /// <exception cref="ArgumentException">The collection or a rule is null.</exception>
    public ProcessPolicy(IEnumerable<ProcessRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
        if (_rules.Any(rule => rule is null)) throw new ArgumentException("Rules must be non-null", nameof(rules));
    }

    /// <summary>Tests whether the exact request matches a rule, independently of the coarse permission.</summary>
    /// <param name="executable">Fully-qualified path, normalized lexically before an ordinal comparison.</param>
    /// <param name="arguments">Complete argument vector, compared ordinally in order.</param>
    /// <param name="pseudoTerminal">Whether the request uses a pseudo-terminal.</param>
    /// <returns>False for malformed paths, null/NUL arguments or unmatched requests; otherwise true.</returns>
    public bool Allows(string? executable, IReadOnlyList<string>? arguments, bool pseudoTerminal = false)
        => TryAuthorize(executable, arguments, pseudoTerminal, out _);

    // Return the very spelling we compared. Launching the original path containing '..'
    // could resolve differently through a symlink even after a lexical match succeeded.
    internal bool TryAuthorize(string? executable, IReadOnlyList<string>? arguments, bool pseudoTerminal, out string normalized)
    {
        normalized = "";
        if (!ProcessRule.TryNormalize(executable, out string candidate) || arguments is null) return false;
        for (int i = 0; i < arguments.Count; i++)
            if (arguments[i] is null || arguments[i].Contains('\0')) return false;
        foreach (ProcessRule rule in _rules)
        {
            if (!rule.Matches(candidate, arguments, pseudoTerminal)) continue;
            normalized = candidate;
            return true;
        }
        return false;
    }
}
