namespace XREngine.AgentOrchestration;

/// <summary>Immutable externally visible state of one swarm node.</summary>
public sealed record AgentSwarmNodeSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public int Depth { get; init; }
    public AgentSwarmRole Role { get; init; }
    public string Objective { get; init; } = string.Empty;
    public AgentSwarmNodeStatus Status { get; init; }
    public bool? Approved { get; init; }
    public string ReviewSummary { get; init; } = string.Empty;
    public IReadOnlyList<string> Failures { get; init; } = [];
    public string RequestedModel { get; init; } = string.Empty;
    public string ActualModel { get; init; } = string.Empty;
    public AgentTokenUsage Usage { get; init; } = new();
    public IReadOnlyList<AgentProviderAttemptDiagnostic> ProviderAttempts { get; init; } = [];
    public IReadOnlyList<AgentSwarmCodeChange> Artifacts { get; init; } = [];
}
