using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Compact run metadata used by list_agent_runs.
/// </summary>
public sealed record AgentRunListItem
{
    public string RunId { get; init; } = string.Empty;

    public AgentRunStatus Status { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>Time at which this list item was produced by the broker.</summary>
    public DateTimeOffset ObservedUtc { get; init; }

    /// <summary>Non-negative wall-clock duration from run creation to observation.</summary>
    public long ElapsedMilliseconds { get; init; }

    /// <summary>Latest informational broker or provider progress stage.</summary>
    public string ProgressMessage { get; init; } = string.Empty;

    public string RequestedModel { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    public string RequestedReasoningEffort { get; init; } = string.Empty;

    public string RequestedTextVerbosity { get; init; } = string.Empty;

    public int MaxOutputTokens { get; init; }

    public string? EditorSession { get; init; }

    public bool UseBackgroundMode { get; init; }

    public int AttemptCount { get; init; }

    public int RetryCount { get; init; }
}
