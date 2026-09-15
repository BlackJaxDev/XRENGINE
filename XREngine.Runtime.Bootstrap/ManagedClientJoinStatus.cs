namespace XREngine.Runtime.Bootstrap;

/// <summary>Non-secret status projection for a managed join flow.</summary>
public sealed class ManagedClientJoinStatus
{
    public ManagedClientJoinState State { get; init; }
    public ManagedClientJoinFailureKind FailureKind { get; init; }
    public string Message { get; init; } = string.Empty;
    public Exception? Failure { get; init; }
}
