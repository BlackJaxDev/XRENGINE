namespace XREngine.ControlPlane;

public sealed class ServerLaunchPlan
{
    public MultiplayerInstanceInfo Instance { get; set; } = new();
    public Dictionary<string, string> Environment { get; set; } = [];
}
