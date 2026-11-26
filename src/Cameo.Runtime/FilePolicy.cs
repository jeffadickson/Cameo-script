namespace Cameo.Runtime;

/// <summary>An immutable directory rule for managed-host filesystem confinement.</summary>
/// <remarks>Paths are canonicalized via <see cref="Path.GetFullPath(string)"/> and existing symlink
/// targets are resolved where possible so parent traversal (<c>..</c>) and symlink escapes cannot
/// leave the configured root.</remarks>
public sealed class FileRule
{
    /// <summary>The canonicalized, fully-qualified root directory.</summary>
    public string RootDirectory { get; }

    /// <summary>Whether write, create, rename and delete operations are allowed inside <see cref="RootDirectory"/>.</summary>
    public bool AllowWrite { get; }

    /// <summary>Creates a filesystem rule for one directory tree.</summary>
    /// <param name="rootDirectory">A fully-qualified directory path without NUL characters.</param>
    /// <param name="allowWrite">False for read-only access; true for read and write access.</param>
    /// <exception cref="ArgumentException">The path is empty, relative, or contains NUL characters.</exception>
    public FileRule(string rootDirectory, bool allowWrite = false)
    {
        if (!TryCanonicalize(rootDirectory, out string canonical))
            throw new ArgumentException("A fully-qualified directory path without NUL characters is required", nameof(rootDirectory));
        RootDirectory = TrimSeparator(canonical);
        AllowWrite = allowWrite;
    }

    internal bool Allows(string canonicalPath, bool write)
    {
        if (write && !AllowWrite) return false;
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(canonicalPath, RootDirectory, cmp)) return true;
        if (!canonicalPath.StartsWith(RootDirectory, cmp)) return false;
        // Root "/" or "C:\" already ends with a directory separator; other roots must be followed by one
        // so "/tmp/app" never matches "/tmp/app2/secret.txt".
        if (RootDirectory.Length > 0 && IsSeparator(RootDirectory[^1])) return true;
        return canonicalPath.Length > RootDirectory.Length && IsSeparator(canonicalPath[RootDirectory.Length]);
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    internal static string TrimSeparator(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (root != null && string.Equals(path, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return path;
        return Path.TrimEndingDirectorySeparator(path);
    }

    internal static bool TryCanonicalize(string? path, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return false;
        try
        {
            string full = Path.GetFullPath(path);
            canonical = TrimSeparator(ResolveLinks(full));
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Resolve symlinks on the target itself or its nearest existing ancestor so creating a new
    // file beneath a symlinked directory is checked against the real destination tree.
    private static string ResolveLinks(string fullPath)
    {
        string current = fullPath;
        var suffix = new Stack<string>();
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (File.Exists(current))
                {
                    FileSystemInfo? target = new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                    string resolved = target != null ? Path.GetFullPath(target.FullName) : current;
                    return AppendSuffix(resolved, suffix);
                }
                if (Directory.Exists(current))
                {
                    FileSystemInfo? target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                    string resolved = target != null ? Path.GetFullPath(target.FullName) : current;
                    return AppendSuffix(resolved, suffix);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return fullPath;
            }
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;
            string leaf = Path.GetFileName(current);
            if (!string.IsNullOrEmpty(leaf)) suffix.Push(leaf);
            current = parent;
        }
        return fullPath;
    }

    private static string AppendSuffix(string basePath, Stack<string> suffix)
    {
        string result = basePath;
        while (suffix.Count > 0) result = Path.Combine(result, suffix.Pop());
        return Path.GetFullPath(result);
    }
}

/// <summary>An immutable allowlist that confines host filesystem access to configured directory trees.</summary>
/// <remarks>Inherited automatically by parallel workers and child sandboxes. An empty policy denies
/// every filesystem access.</remarks>
public sealed class FilePolicy
{
    private readonly FileRule[] _rules;

    /// <summary>Copies the supplied immutable rules. An empty collection denies all file operations.</summary>
    public FilePolicy(IEnumerable<FileRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
        if (_rules.Any(rule => rule is null)) throw new ArgumentException("Rules must be non-null", nameof(rules));
    }

    /// <summary>Convenience factory for a policy that denies all filesystem access.</summary>
    public static FilePolicy DenyAll { get; } = new([]);

    /// <summary>Tests whether reading <paramref name="path"/> is permitted.</summary>
    public bool AllowsRead(string? path) => TryAuthorize(path, write: false, out _);

    /// <summary>Tests whether writing, creating, or deleting <paramref name="path"/> is permitted.</summary>
    public bool AllowsWrite(string? path) => TryAuthorize(path, write: true, out _);

    /// <summary>Authorizes <paramref name="path"/> and returns the canonicalized path that matched.</summary>
    public bool TryAuthorize(string? path, bool write, out string canonical)
    {
        canonical = "";
        if (!FileRule.TryCanonicalize(path, out string candidate)) return false;
        foreach (FileRule rule in _rules)
        {
            if (!rule.Allows(candidate, write)) continue;
            canonical = candidate;
            return true;
        }
        return false;
    }
}
