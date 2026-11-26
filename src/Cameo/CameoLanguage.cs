using Cameo.Runtime;
using Cameo.Runtime.Syntax;

namespace Cameo;

/// <summary>Cameo's source-language entry point over shared syntax infrastructure.</summary>
public sealed class CameoLanguage : ISourceLanguage
{
    /// <summary>Immutable language selection shared by an engine and its workers.</summary>
    public static CameoLanguage Instance { get; } = new();
    private CameoLanguage() { }
    /// <inheritdoc />
    public string Name => "Cameo";
    /// <inheritdoc />
    public string SourceExtension => ".cameo";
    /// <inheritdoc />
    public bool UsesTaggedResults => true;
    /// <inheritdoc />
    public Chunk Parse(string source, string fileName) => Parser.ParseCameo(source, fileName);
}
