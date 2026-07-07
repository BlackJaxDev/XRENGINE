using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class WorldPackageManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string PackageId { get; set; } = string.Empty;
    public WorldAssetIdentity Asset { get; set; } = new();
    public string RootPath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public string ManifestHash { get; set; } = string.Empty;
    public List<WorldPackageFile> Files { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];
}
