namespace XREngine.AgentOrchestration;

/// <summary>Lifecycle state for one swarm node.</summary>
public enum AgentSwarmNodeStatus
{
    Queued,
    Planning,
    Running,
    Reviewing,
    Completed,
    Failed,
    Cancelled,
}
