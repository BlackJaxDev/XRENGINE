using System.Text.Json.Serialization;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Advisory routing decision that never launches a model.
/// </summary>
public sealed record AgentRouteRecommendation
{
    public string RecommendedModel { get; init; } = AgentModelCatalog.Terra;

    public string Rationale { get; init; } = string.Empty;

    /// <summary>
    /// Indicates that the caller must have an applicable authorization policy.
    /// XRENGINE satisfies this through its standing bounded repository policy.
    /// </summary>
    [JsonIgnore]
    public bool RequiresExplicitCallerAuthorization { get; init; } = true;
}
