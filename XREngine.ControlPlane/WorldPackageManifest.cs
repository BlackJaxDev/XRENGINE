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

public sealed class WorldPackageFile
{
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class WorldPackageVerificationResult
{
    public bool Success => MissingFiles.Count == 0 && HashMismatches.Count == 0 && LengthMismatches.Count == 0;
    public List<string> MissingFiles { get; } = [];
    public List<string> HashMismatches { get; } = [];
    public List<string> LengthMismatches { get; } = [];
}
