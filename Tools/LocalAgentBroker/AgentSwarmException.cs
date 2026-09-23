using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

internal sealed class AgentSwarmException(AgentFailureCategory category, string message, string diagnosticDetail = "") : Exception(message)
{
    public AgentFailureCategory Category { get; } = category;
    public string DiagnosticDetail { get; } = diagnosticDetail;
}
