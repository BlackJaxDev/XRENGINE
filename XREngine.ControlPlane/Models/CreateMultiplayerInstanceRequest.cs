using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class CreateMultiplayerInstanceRequest
{
    public string? InstanceId { get; set; }
    public string? DisplayName { get; set; }
    public string? HostId { get; set; }
    public RealtimeEndpointDescriptor? Endpoint { get; set; }
    public WorldAssetIdentity? WorldAsset { get; set; }
    public WorldPackageManifest? WorldPackage { get; set; }
    public Guid? SessionId { get; set; }
    public string? SessionToken { get; set; }
    public int? MaxPlayers { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}
