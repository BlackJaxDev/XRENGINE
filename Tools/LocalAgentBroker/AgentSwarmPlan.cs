using System.Text.Json.Serialization;

namespace XREngine.LocalAgentBroker;

internal sealed record AgentSwarmPlan
{
    [JsonRequired]
    public List<AgentSwarmPlanChild> Children { get; init; } = [];
}
