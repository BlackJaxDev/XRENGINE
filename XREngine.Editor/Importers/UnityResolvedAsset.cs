namespace XREngine.Scene.Importers;

/// <summary>
/// Resolution metadata for an external Unity asset reference.
/// </summary>
public sealed record UnityResolvedAsset
{
    public required UnityAssetReference Reference { get; init; }
    public string? AssetPath { get; init; }
    public string? MetaPath { get; init; }
    public bool Exists => !string.IsNullOrWhiteSpace(AssetPath) && File.Exists(AssetPath);
}
