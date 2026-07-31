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

    public string RequestedModel { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    public string EditorSession { get; init; } = string.Empty;
}
