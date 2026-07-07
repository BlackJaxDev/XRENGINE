namespace XREngine.ControlPlane;

public sealed partial class InMemoryControlPlane
{
    private sealed class InstanceState
    {
        public MultiplayerInstanceInfo Info { get; init; } = new();
        public Dictionary<string, MultiplayerPlayerInfo> Players { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
