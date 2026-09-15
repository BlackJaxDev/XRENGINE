namespace XREngine.Runtime.Bootstrap;

/// <summary>Actionable, credential-free failure categories exposed by managed client workflow UI.</summary>
public enum ManagedClientJoinFailureKind
{
    None = 0,
    WorldOrBuildMismatch = 1,
    RoomFull = 2,
    InvalidOrExpiredTicket = 3,
    ServerUnready = 4,
    EndpointUnreachable = 5,
    SynchronizationInterrupted = 6,
    Kicked = 7,
    ServerExited = 8,
    Cancelled = 9,
    Unknown = 10,
}
