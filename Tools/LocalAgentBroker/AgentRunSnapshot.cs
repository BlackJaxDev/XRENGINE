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

    /// <summary>
    /// Time at which this snapshot was observed. Unlike <see cref="UpdatedUtc"/>,
    /// this advances on every poll even while the provider has emitted no text.
    /// </summary>
    public DateTimeOffset ObservedUtc { get; init; }

    /// <summary>
    /// Non-negative wall-clock duration from run creation through
    /// <see cref="ObservedUtc"/>.
    /// </summary>
    public long ElapsedMilliseconds { get; init; }

    /// <summary>
    /// Latest broker or provider progress signal. This is an informational stage,
    /// not a completion percentage or a terminal result.
    /// </summary>
    public string ProgressMessage { get; init; } = string.Empty;

    public string RequestedModel { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    /// <summary>Requested provider controls retained with the run for auditability.</summary>
    public string RequestedReasoningEffort { get; init; } = string.Empty;

    /// <summary>Requested Responses API visible-text verbosity.</summary>
    public string RequestedTextVerbosity { get; init; } = string.Empty;

    /// <summary>Hard run-wide Responses API output-token budget.</summary>
    public int MaxOutputTokens { get; init; }

    public string? EditorSession { get; init; }

    public bool UseBackgroundMode { get; init; }

    public string IncrementalText { get; init; } = string.Empty;

    public AgentTokenUsage Usage { get; init; } = new();

    public IReadOnlyList<AgentToolEvidence> ToolEvidence { get; init; } = [];

    public int RetryCount { get; init; }

    public IReadOnlyList<AgentProviderAttemptDiagnostic> ProviderAttempts { get; init; } = [];

    public AgentRunResult? Result { get; init; }
}
