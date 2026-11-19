namespace XREngine.UnityScriptHost.Admission.PackageSources;

/// <summary>Names one local package source and the exact bytes requested for composition.</summary>
public sealed record UnityPinnedPackageSource
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
}
