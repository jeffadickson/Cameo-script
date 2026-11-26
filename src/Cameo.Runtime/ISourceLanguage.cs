using Cameo.Runtime.Syntax;

namespace Cameo.Runtime;

/// <summary>A source-language boundary over the common AST and execution engine.
/// Implementations must be immutable and Parse must be safe for concurrent module parsing.</summary>
public interface ISourceLanguage
{
    /// <summary>The diagnostic/documentation name of this language.</summary>
    string Name { get; }
    /// <summary>Its primary source suffix, including a dot (for example .tl).</summary>
    string SourceExtension { get; }
    /// <summary>Whether this front end exposes the tagged Result prelude.</summary>
    bool UsesTaggedResults => false;
    /// <summary>Parses source in LuaText's byte-string representation, preserving source locations.
    /// Throws CameoSyntaxException for invalid source; never accesses live VM state.</summary>
    Chunk Parse(string source, string fileName);
}

/// <summary>The maintained Teal syntax; this remains the default for every existing entry point.</summary>
public sealed class CameoSourceLanguage : ISourceLanguage
{
    /// <summary>Stateless shared Teal front end.</summary>
    public static CameoSourceLanguage Instance { get; } = new();
    private CameoSourceLanguage() { }
    /// <inheritdoc />
    public string Name => "Teal";
    /// <inheritdoc />
    public string SourceExtension => ".tl";
    /// <inheritdoc />
    public Chunk Parse(string source, string fileName) => Parser.Parse(source, fileName);
}
