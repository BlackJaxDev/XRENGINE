using XREngine.Networking;

namespace XREngine.ControlPlane;

public sealed class JoinMultiplayerInstanceResult
{
    public MultiplayerInstanceInfo Instance { get; set; } = new();
    public MultiplayerPlayerInfo Player { get; set; } = new();
    public RealtimeJoinHandoffPayload HandoffPayload { get; set; } = new();
    public string HandoffJson { get; set; } = string.Empty;
    public Dictionary<string, string> ClientEnvironment { get; set; } = [];
}
