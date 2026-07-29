namespace XREngine.Scene.Prefabs;

/// <summary>
/// Fingerprinted source dependency and its native conversion result.
/// </summary>
[Serializable]
public sealed class UnityImportDependencyManifestEntry
{
    public string? SourceGuid { get; set; }
    public long? LocalFileId { get; set; }
    public string NormalizedPath { get; set; } = string.Empty;
    public UnityImportDependencyKind Kind { get; set; }
    public string? ReferringProperty { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string? OutputAssetPath { get; set; }
    public UnityImportConversionOutcome Outcome { get; set; }
}
