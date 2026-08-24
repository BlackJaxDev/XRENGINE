using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Projects orchestration events into the synchronized run registry.
/// </summary>
internal sealed class BrokerRunObserver(
    BrokerRunRecord record,
    BrokerHistoryPublisher historyPublisher) : IAgentRunObserver
{
    public ValueTask OnEventAsync(AgentRunEvent runEvent, CancellationToken cancellationToken)
    {
        if (runEvent.Kind == AgentRunEventKind.Status)
            record.UpdateStatus(runEvent.Message);
        else if (runEvent.Kind == AgentRunEventKind.TextDelta)
            record.AppendText(runEvent.Message);
        else if (runEvent.Kind == AgentRunEventKind.Usage && runEvent.Usage is not null)
            record.AddUsage(runEvent.Usage);
        else if (runEvent.Kind == AgentRunEventKind.ToolCompleted
            && runEvent.ToolEvidence is not null)
        {
            record.AddToolEvidence(runEvent.ToolEvidence);
        }
        else if (runEvent.Kind == AgentRunEventKind.Diagnostic
            && runEvent.ProviderAttempt is not null)
        {
            record.AddProviderAttempt(runEvent.ProviderAttempt);
        }
        else if (runEvent.Kind == AgentRunEventKind.Retry)
        {
            record.RecordRetry();
        }
        historyPublisher.QueueUpdate(record);
        return ValueTask.CompletedTask;
    }
}
