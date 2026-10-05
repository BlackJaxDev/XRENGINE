namespace XREngine.ControlPlane;

public sealed class WorldPackageVerificationResult
{
    public bool Success => !InvalidManifest && !ManifestHashMismatch && !AssetContentHashMismatch && MissingFiles.Count == 0 && HashMismatches.Count == 0 && LengthMismatches.Count == 0 && UnsafePaths.Count == 0 && ExtraFiles.Count == 0 && ExecutablePayloads.Count == 0;
    public bool InvalidManifest { get; set; }
    public bool ManifestHashMismatch { get; set; }
    public bool AssetContentHashMismatch { get; set; }
    public List<string> MissingFiles { get; } = [];
    public List<string> HashMismatches { get; } = [];
    public List<string> LengthMismatches { get; } = [];
    public List<string> UnsafePaths { get; } = [];
    public List<string> ExtraFiles { get; } = [];
    /// <summary>
    /// Files rejected by the data-only content policy. Each entry is the diagnostic text from
    /// <see cref="WorldPackageExecutablePayloadPolicy.DescribeRejection"/>, naming the file, its
    /// detected kind, and the policy document.
    /// </summary>
    public List<string> ExecutablePayloads { get; } = [];
}
