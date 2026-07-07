using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class ControlPlaneHostSnapshot
{
    public string HostId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new();
    public int MaxInstances { get; set; }
    public int MaxPlayers { get; set; }
    public int ActiveInstances { get; set; }
    public int ActivePlayers { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];

    public bool HasCapacityFor(int requestedMaxPlayers)
        => ActiveInstances < MaxInstances
            && ActivePlayers + Math.Max(1, requestedMaxPlayers) <= MaxPlayers;
}
