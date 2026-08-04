namespace XREngine.AgentOrchestration;

/// <summary>
/// Terminal or incremental result returned by an orchestration host.
/// </summary>
public sealed record AgentRunResult
{
    public string RunId { get; init; } = string.Empty;

    public AgentRunStatus Status { get; init; }

    public string RequestedModel { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    public string FinalText { get; init; } = string.Empty;

    public IReadOnlyList<AgentOutputItem> OutputItems { get; init; } = [];

    public IReadOnlyList<AgentToolEvidence> ToolEvidence { get; init; } = [];

    public AgentTokenUsage Usage { get; init; } = new();

    public int ToolCallCount { get; init; }

    public int TurnCount { get; init; }

    public int RetryCount { get; init; }

    public IReadOnlyList<AgentProviderAttemptDiagnostic> ProviderAttempts { get; init; } = [];

    public long ElapsedMilliseconds { get; init; }

    public AgentFailure? Failure { get; init; }
}
