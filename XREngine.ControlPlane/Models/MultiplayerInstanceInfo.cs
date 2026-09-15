using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>
/// Represents information about a multiplayer instance in the control plane.
/// </summary>
public sealed class MultiplayerInstanceInfo
{
    /// <summary>
    /// Gets or sets the unique identifier of the multiplayer instance.
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the multiplayer instance.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the unique identifier of the host of the multiplayer instance.
    /// </summary>
    public string HostId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unique identifier of the owner user of the multiplayer instance.
    /// </summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tenant identifier of the multiplayer instance.
    /// </summary>
    public string TenantId { get; set; } = "local";

    /// <summary>
    /// Gets or sets the visibility of the multiplayer instance.
    /// </summary>
    public MultiplayerInstanceVisibility Visibility { get; set; }

    /// <summary>
    /// Gets or sets the operation identifier associated with the multiplayer instance.
    /// </summary>
    public string OperationId { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the worker generation identifier associated with the multiplayer instance.
    /// </summary>
    public Guid WorkerGeneration { get; set; }

    /// <summary>
    /// Gets or sets the realtime endpoint descriptor for the multiplayer instance.
    /// </summary>
    public RealtimeEndpointDescriptor Endpoint { get; set; } = new();

    /// <summary>
    /// Gets or sets the session identifier for the multiplayer instance.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// Gets or sets the world asset identity associated with the multiplayer instance.
    /// </summary>
    public WorldAssetIdentity WorldAsset { get; set; } = new();

    /// <summary>
    /// Gets or sets the world package manifest associated with the multiplayer instance.
    /// </summary>
    public WorldPackageManifest? WorldPackage { get; set; }

    /// <summary>
    /// Gets or sets the current state of the multiplayer instance.
    /// </summary>
    public MultiplayerInstanceState State { get; set; } = MultiplayerInstanceState.Running;

    /// <summary>
    /// Gets or sets the maximum number of players allowed in the multiplayer instance.
    /// </summary>
    public int MaxPlayers { get; set; }

    /// <summary>
    /// Gets or sets the current number of players in the multiplayer instance.
    /// </summary>
    public int CurrentPlayers { get; set; }

    /// <summary>
    /// Gets or sets the number of reserved player slots in the multiplayer instance.
    /// </summary>
    public int ReservedPlayers { get; set; }

    /// <summary>
    /// Gets or sets the number of connected players in the multiplayer instance.
    /// </summary>
    public int ConnectedPlayers { get; set; }

    /// <summary>
    /// Gets or sets the number of synchronized players in the multiplayer instance.
    /// </summary>
    public int SynchronizedPlayers { get; set; }

    /// <summary>
    /// Gets or sets the number of players whose sessions are held for resumption in the multiplayer instance.
    /// </summary>
    public int ResumeHeldPlayers { get; set; }

    /// <summary>
    /// Gets or sets the creation timestamp of the multiplayer instance in UTC.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the metadata associated with the multiplayer instance.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>
    /// Local-dev token that should be passed to the realtime worker as XRE_SESSION_TOKEN.
    /// Public services should keep the same shape but avoid returning this in list APIs.
    /// </summary>
    public string SessionToken { get; set; } = string.Empty;
}
