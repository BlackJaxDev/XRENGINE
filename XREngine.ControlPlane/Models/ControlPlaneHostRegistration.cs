using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class ControlPlaneHostRegistration
{
    public string HostId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new()
    {
        Host = "127.0.0.1",
        Port = 5000,
        ProtocolVersion = "dev",
    };
    public int MaxInstances { get; set; } = 1;
    public int MaxPlayers { get; set; } = 16;
    public Dictionary<string, string> Metadata { get; set; } = [];
}
