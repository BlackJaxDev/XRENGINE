using System.Text.Json.Serialization;
using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class WorldPackageManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string PackageId { get; set; } = string.Empty;
    public WorldAssetIdentity Asset { get; set; } = new();
    public string WorldEntryPoint { get; set; } = string.Empty;
    public string GameBootstrapId { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
    /// <summary>Local staging source. It is intentionally excluded from transport/public package DTOs.</summary>
    [JsonIgnore]
    public string RootPath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public string ManifestHash { get; set; } = string.Empty;
    public List<WorldPackageFile> Files { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];
}
