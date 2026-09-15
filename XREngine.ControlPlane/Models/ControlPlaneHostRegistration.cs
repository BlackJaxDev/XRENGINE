using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>
/// Represents the registration information for a control plane host.
/// </summary>
public sealed class ControlPlaneHostRegistration
{
    /// <summary>
    /// Gets or sets the unique identifier of the host.
    /// </summary>
    public string HostId { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the display name of the host.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the endpoint descriptor for the host's realtime communication.
    /// </summary>
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new()
    {
        Host = "127.0.0.1",
        Port = 5000,
        ProtocolVersion = "dev",
    };

    /// <summary>
    /// Gets or sets the maximum number of instances the host can run.
    /// </summary>
    public int MaxInstances { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum number of players the host can support.
    /// </summary>
    public int MaxPlayers { get; set; } = 16;

    /// <summary>
    /// Gets or sets the metadata associated with the host.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}
