namespace XREngine.AgentOrchestration;

/// <summary>
/// Adapts a host callback to the reusable observer boundary.
/// </summary>
public sealed class DelegateAgentRunObserver(
    Func<AgentRunEvent, CancellationToken, ValueTask> onEvent) : IAgentRunObserver
{
    public ValueTask OnEventAsync(AgentRunEvent runEvent, CancellationToken cancellationToken)
        => onEvent(runEvent, cancellationToken);
}
