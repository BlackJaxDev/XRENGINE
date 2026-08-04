namespace XREngine.AgentOrchestration;

/// <summary>
/// Safe provider metadata for one model-request attempt.
/// </summary>
public sealed record AgentProviderAttemptDiagnostic
{
    public int TurnNumber { get; init; }

    public int AttemptNumber { get; init; }

    public bool UsedBackgroundMode { get; init; }

    public string Outcome { get; init; } = string.Empty;

    public string ResponseId { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    public int ProviderEventCount { get; init; }

    public int MalformedEventCount { get; init; }

    public string LastProviderEventType { get; init; } = string.Empty;

    public long? LastSequenceNumber { get; init; }

    public string TerminalStatus { get; init; } = string.Empty;

    public string IncompleteReason { get; init; } = string.Empty;

    public long ElapsedMilliseconds { get; init; }

    public AgentFailureCategory? FailureCategory { get; init; }

    public int? ProviderStatus { get; init; }

    public bool Retryable { get; init; }

    public bool Retried { get; init; }

    public bool ProviderCancellationAccepted { get; init; }
}
