using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>
/// Represents a snapshot of the current state and capabilities of a control plane host.
/// </summary>
public sealed class ControlPlaneHostSnapshot
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
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new();

    /// <summary>
    /// Gets or sets the maximum number of instances the host can run.
    /// </summary>
    public int MaxInstances { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of players the host can support.
    /// </summary>
    public int MaxPlayers { get; set; }

    /// <summary>
    /// Gets or sets the number of active instances currently running on the host.
    /// </summary>
    public int ActiveInstances { get; set; }

    /// <summary>
    /// Gets or sets the number of active players currently connected to the host.
    /// </summary>
    public int ActivePlayers { get; set; }

    /// <summary>
    /// Gets or sets the number of player slots reserved on the host.
    /// </summary>
    public int ReservedPlayerSlots { get; set; }

    /// <summary>
    /// Gets or sets the number of players currently connected to the host.
    /// </summary>
    public int ConnectedPlayers { get; set; }

    /// <summary>
    /// Gets or sets the number of players whose state is synchronized with the host.
    /// </summary>
    public int SynchronizedPlayers { get; set; }

    /// <summary>
    /// Gets or sets the number of players whose sessions are held for resumption.
    /// </summary>
    public int ResumeHeldPlayers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the host is healthy.
    /// </summary>
    public bool IsHealthy { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp when the host's lease expires.
    /// </summary>
    public DateTimeOffset LeaseExpiresUtc { get; set; }
    
    /// <summary>
    /// Gets or sets the metadata associated with the host.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>
    /// Determines whether the host has capacity for the specified number of requested players.
    /// </summary>
    /// <param name="requestedMaxPlayers">The number of players to check for capacity.</param>
    /// <returns><c>true</c> if the host has capacity for the specified number of requested players; otherwise, <c>false</c>.</returns>
    public bool HasCapacityFor(int requestedMaxPlayers)
        => ActiveInstances < MaxInstances
            && IsHealthy
            && ReservedPlayerSlots + Math.Max(1, requestedMaxPlayers) <= MaxPlayers;
}
