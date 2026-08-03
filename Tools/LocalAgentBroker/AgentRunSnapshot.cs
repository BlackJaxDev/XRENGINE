using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Immutable broker view of an active or retained run.
/// </summary>
public sealed record AgentRunSnapshot
{
    public string RunId { get; init; } = string.Empty;

    public AgentRunStatus Status { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public string RequestedModel { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    public string EditorSession { get; init; } = string.Empty;

    public bool UseBackgroundMode { get; init; }

    public string IncrementalText { get; init; } = string.Empty;

    public AgentTokenUsage Usage { get; init; } = new();

    public IReadOnlyList<AgentToolEvidence> ToolEvidence { get; init; } = [];

    public int RetryCount { get; init; }

    public IReadOnlyList<AgentProviderAttemptDiagnostic> ProviderAttempts { get; init; } = [];

    public AgentRunResult? Result { get; init; }
}
