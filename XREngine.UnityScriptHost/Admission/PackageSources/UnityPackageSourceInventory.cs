namespace XREngine.UnityScriptHost.Admission.PackageSources;

/// <summary>Records the files and identity of one local Unity package source directory.</summary>
public sealed record UnityPackageSourceInventory
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
    public long TotalFileBytes { get; init; }
    public UnitySourceEntry[] Entries { get; init; } = [];
}
