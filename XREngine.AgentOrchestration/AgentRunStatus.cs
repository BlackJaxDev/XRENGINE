namespace XREngine.AgentOrchestration;

/// <summary>
/// Describes the externally visible lifecycle of an agent run.
/// </summary>
public enum AgentRunStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}
