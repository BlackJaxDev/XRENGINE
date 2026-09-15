namespace XREngine.ControlPlane;

/// <summary>
/// Represents the plan for launching a server, including instance information and environment variables.
/// </summary>
public sealed class ServerLaunchPlan
{
    /// <summary>
    /// Gets or sets the information about the multiplayer instance to be launched.
    /// </summary>
    public MultiplayerInstanceInfo Instance { get; set; } = new();

    /// <summary>
    /// Gets or sets the environment variables for the server launch.
    /// </summary>
    public Dictionary<string, string> Environment { get; set; } = [];
}
