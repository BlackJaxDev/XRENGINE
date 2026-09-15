using XREngine.Networking;

namespace XREngine.ControlPlane;

/// <summary>
/// Represents the result of a request to join a multiplayer instance in the control plane.
/// </summary>
public sealed class JoinMultiplayerInstanceResult
{
    /// <summary>
    /// Gets or sets the information of the multiplayer instance that was joined.
    /// </summary>
    public MultiplayerInstanceInfo Instance { get; set; } = new();

    /// <summary>
    /// Gets or sets the information of the player who joined the multiplayer instance.
    /// </summary>
    public MultiplayerPlayerInfo Player { get; set; } = new();

    /// <summary>
    /// Gets or sets the handoff payload for the real-time connection.
    /// </summary>
    public RealtimeJoinHandoffPayload HandoffPayload { get; set; } = new();

    /// <summary>
    /// Gets or sets the handoff payload as a JSON string.
    /// </summary>
    public string HandoffJson { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client environment information.
    /// </summary>
    public Dictionary<string, string> ClientEnvironment { get; set; } = [];
}
