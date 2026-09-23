namespace XREngine.AgentOrchestration;

/// <summary>Aggregate outcome of a fully settled read-only swarm run.</summary>
public sealed record AgentSwarmRunResult
{
    public AgentRunResult Aggregate { get; init; } = new();
    public AgentSwarmSnapshot Snapshot { get; init; } = new();
    public IReadOnlyList<AgentSwarmCodeChange> Changes { get; init; } = [];
}
