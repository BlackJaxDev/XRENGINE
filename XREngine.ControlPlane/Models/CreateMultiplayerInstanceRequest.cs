using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>
/// Represents a request to create a multiplayer instance in the control plane.
/// </summary>
public sealed class CreateMultiplayerInstanceRequest
{
    /// <summary>
    /// Gets or sets the unique identifier of the multiplayer instance.
    /// </summary>
    public string? InstanceId { get; set; }
    
    /// <summary>
    /// Gets or sets the display name of the multiplayer instance.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the user ID of the owner of the multiplayer instance.
    /// </summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the tenant ID associated with the multiplayer instance.
    /// </summary>
    public string TenantId { get; set; } = "local";

    /// <summary>
    /// Gets or sets the visibility of the multiplayer instance.
    /// </summary>
    public MultiplayerInstanceVisibility Visibility { get; set; }

    /// <summary>
    /// Gets or sets the operation ID associated with the multiplayer instance.
    /// </summary>
    public string? OperationId { get; set; }
    
    /// <summary>
    /// Gets or sets the host ID of the multiplayer instance.
    /// </summary>
    public string? HostId { get; set; }

    /// <summary>
    /// Gets or sets the endpoint descriptor of the multiplayer instance.
    /// </summary>
    public RealtimeEndpointDescriptor? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the world asset associated with the multiplayer instance.
    /// </summary>
    public WorldAssetIdentity? WorldAsset { get; set; }

    /// <summary>
    /// Gets or sets the world package manifest associated with the multiplayer instance.
    /// </summary>
    public WorldPackageManifest? WorldPackage { get; set; }

    /// <summary>
    /// Gets or sets the session ID of the multiplayer instance.
    /// </summary>
    public Guid? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the session token of the multiplayer instance.
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of players allowed in the multiplayer instance.
    /// </summary>
    public int? MaxPlayers { get; set; }

    /// <summary>
    /// Permits the legacy direct endpoint flow. Managed creation is pending worker readiness.
    /// </summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>
    /// Gets or sets the metadata associated with the multiplayer instance.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}
