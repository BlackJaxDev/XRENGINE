using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker.Shared;

/// <summary>
/// Durable, user-visible prompt and response state for one broker run.
/// </summary>
public sealed record BrokerHistoryRecord
{
    public int SchemaVersion { get; init; } = 1;

    public string RunId { get; init; } = string.Empty;

    public AgentRunStatus Status { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public string Objective { get; init; } = string.Empty;

    /// <summary>
    /// Broker-generated first-turn prompt text. Separate image and repository
    /// context input blocks are intentionally excluded.
    /// </summary>
    public string PromptText { get; init; } = string.Empty;

    /// <summary>Optional Responses API instructions submitted beside the prompt.</summary>
    public string SystemInstructions { get; init; } = string.Empty;

    public string RequestedModel { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    public string? EditorSession { get; init; }

    public string ProgressMessage { get; init; } = string.Empty;

    /// <summary>Incremental response text while running and final text when terminal.</summary>
    public string ResponseText { get; init; } = string.Empty;

    public string FailureSummary { get; init; } = string.Empty;

    public string FailureDetail { get; init; } = string.Empty;

    public AgentTokenUsage Usage { get; init; } = new();

    public int TurnCount { get; init; }

    public int ToolCallCount { get; init; }

    public int RetryCount { get; init; }

    public bool IsActive
        => Status is AgentRunStatus.Queued or AgentRunStatus.Running;
}
