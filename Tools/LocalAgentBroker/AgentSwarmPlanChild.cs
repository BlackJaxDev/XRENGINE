using System.Text.Json.Serialization;
using XREngine.AgentOrchestration;

namespace XREngine.LocalAgentBroker;

internal sealed record AgentSwarmPlanChild
{
    [JsonRequired]
    public string Objective { get; init; } = string.Empty;
    [JsonRequired]
    public List<string> Paths { get; init; } = [];
    [JsonRequired]
    public AgentSwarmRole Role { get; init; }
}
