using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class JoinMultiplayerInstanceRequest
{
    public string InstanceId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public WorldAssetIdentity? LocalWorldAsset { get; set; }
    public string? BuildVersion { get; set; }
    public int? ClientReceivePort { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}
