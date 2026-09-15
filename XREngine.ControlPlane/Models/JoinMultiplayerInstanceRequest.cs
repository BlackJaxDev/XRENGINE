using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>
/// Represents a request to join a multiplayer instance in the control plane.
/// </summary>
public sealed class JoinMultiplayerInstanceRequest
{
    /// <summary>
    /// Gets or sets the unique identifier of the multiplayer instance to join.
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unique identifier of the client joining the multiplayer instance.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the client joining the multiplayer instance.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the local world asset of the client joining the multiplayer instance.
    /// </summary>
    public WorldAssetIdentity? LocalWorldAsset { get; set; }

    /// <summary>
    /// Gets or sets the build version of the client joining the multiplayer instance.
    /// </summary>
    public string? BuildVersion { get; set; }

    /// <summary>
    /// Gets or sets the client receive port for the multiplayer instance.
    /// </summary>
    public int? ClientReceivePort { get; set; }

    /// <summary>
    /// Gets or sets the metadata associated with the client joining the multiplayer instance.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}
