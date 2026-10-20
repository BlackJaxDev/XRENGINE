namespace XREngine.Core.Files;

/// <summary>Describes a cooked asset and the references that must be ready before deserialization.</summary>
public sealed record RuntimeAssetCatalogEntry(
    string Path,
    string TypeName,
    RuntimeAssetEncoding Encoding,
    IReadOnlyList<string> Dependencies);
