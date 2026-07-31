using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Projects orchestration events into the synchronized run registry.
/// </summary>
internal sealed class BrokerRunObserver(BrokerRunRecord record) : IAgentRunObserver
{
    public ValueTask OnEventAsync(AgentRunEvent runEvent, CancellationToken cancellationToken)
    {
        if (runEvent.Kind == AgentRunEventKind.TextDelta)
            record.AppendText(runEvent.Message);
        else if (runEvent.Kind == AgentRunEventKind.Usage && runEvent.Usage is not null)
            record.AddUsage(runEvent.Usage);
        else if (runEvent.Kind == AgentRunEventKind.ToolCompleted
            && runEvent.ToolEvidence is not null)
        {
            record.AddToolEvidence(runEvent.ToolEvidence);
        }
        return ValueTask.CompletedTask;
    }
}
