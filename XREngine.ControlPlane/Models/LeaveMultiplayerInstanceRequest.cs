namespace XREngine.ControlPlane;

/// <summary>
/// Represents a request to leave a multiplayer instance in the control plane.
/// </summary>
public sealed class LeaveMultiplayerInstanceRequest
{
    /// <summary>
    /// Gets or sets the unique identifier of the multiplayer instance to leave.
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unique identifier of the client leaving the multiplayer instance.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;
}
