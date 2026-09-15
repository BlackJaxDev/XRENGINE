namespace XREngine.Runtime.Bootstrap;

/// <summary>Observable state of one managed-client join or instance switch.</summary>
public enum ManagedClientJoinState
{
    Idle = 0,
    Loading = 1,
    Connecting = 2,
    Synchronizing = 3,
    Ready = 4,
    Reconnecting = 5,
    Leaving = 6,
    Failed = 7,
}
