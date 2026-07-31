namespace XREngine.AgentOrchestration;

/// <summary>
/// Observer used when a host does not need incremental progress.
/// </summary>
public sealed class NullAgentRunObserver : IAgentRunObserver
{
    public static NullAgentRunObserver Instance { get; } = new();

    private NullAgentRunObserver()
    {
    }

    public ValueTask OnEventAsync(AgentRunEvent runEvent, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
