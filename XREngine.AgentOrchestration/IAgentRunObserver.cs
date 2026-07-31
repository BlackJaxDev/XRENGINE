namespace XREngine.AgentOrchestration;

/// <summary>
/// Receives streaming progress without coupling orchestration to a UI or host.
/// </summary>
public interface IAgentRunObserver
{
    ValueTask OnEventAsync(AgentRunEvent runEvent, CancellationToken cancellationToken);
}
