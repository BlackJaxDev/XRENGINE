namespace XREngine.AgentOrchestration;

/// <summary>
/// Provider request and continuation boundary used by the reusable tool loop.
/// </summary>
public interface IAgentModelClient
{
    Task<AgentModelTurnResult> CreateResponseAsync(
        AgentModelTurnRequest request,
        IAgentRunObserver observer,
        CancellationToken cancellationToken);
}
