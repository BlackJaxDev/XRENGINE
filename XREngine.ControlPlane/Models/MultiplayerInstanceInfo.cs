using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class MultiplayerInstanceInfo
{
    public string InstanceId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string HostId { get; set; } = string.Empty;
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new();
    public Guid SessionId { get; set; }
    public WorldAssetIdentity WorldAsset { get; set; } = new();
    public WorldPackageManifest? WorldPackage { get; set; }
    public MultiplayerInstanceState State { get; set; } = MultiplayerInstanceState.Running;
    public int MaxPlayers { get; set; }
    public int CurrentPlayers { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>
    /// Local-dev token that should be passed to the realtime worker as XRE_SESSION_TOKEN.
    /// Public services should keep the same shape but avoid returning this in list APIs.
    /// </summary>
    public string SessionToken { get; set; } = string.Empty;
}
