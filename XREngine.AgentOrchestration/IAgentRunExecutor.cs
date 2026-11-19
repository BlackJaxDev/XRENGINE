namespace XREngine.AgentOrchestration;

/// <summary>
/// Executes a complete agent run and reports host tool evidence and terminal state.
/// API transports and local agent runtimes implement this boundary separately.
/// </summary>
public interface IAgentRunExecutor
{
    Task<AgentRunResult> RunAsync(
        string runId,
        AgentRunRequest request,
        IAgentToolProvider toolProvider,
        IAgentRunObserver? observer = null,
        CancellationToken cancellationToken = default);
}
