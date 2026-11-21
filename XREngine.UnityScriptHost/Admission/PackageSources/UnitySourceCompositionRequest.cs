namespace XREngine.UnityScriptHost.Admission.PackageSources;

/// <summary>Pins a source inventory and local package sources for a new composed project.</summary>
public sealed record UnitySourceCompositionRequest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SourceContentSha256 { get; init; } = string.Empty;
    public string EntryPath { get; init; } = string.Empty;
    public string RootName { get; init; } = string.Empty;
    public UnityPinnedPackageSource[] Packages { get; init; } = [];
}
