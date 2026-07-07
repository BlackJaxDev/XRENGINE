namespace XREngine.ControlPlane;

public sealed class WorldPackageVerificationResult
{
    public bool Success => MissingFiles.Count == 0 && HashMismatches.Count == 0 && LengthMismatches.Count == 0;
    public List<string> MissingFiles { get; } = [];
    public List<string> HashMismatches { get; } = [];
    public List<string> LengthMismatches { get; } = [];
}
