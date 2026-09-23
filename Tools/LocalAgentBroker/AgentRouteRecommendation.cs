using System.Text.Json.Serialization;

namespace XREngine.LocalAgentBroker;

/// <summary>
/// Advisory routing decision that never launches a model.
/// </summary>
public sealed record AgentRouteRecommendation
{
    public string RecommendedModel { get; init; } = AgentModelCatalog.Sol6;

    /// <summary>
    /// Model family used to make this recommendation.
    /// </summary>
    public string ModelFamily { get; init; } = AgentModelCatalog.Gpt6Family;

    /// <summary>
    /// Indicates that the recommendation is an explicitly requested GPT-5.6 compatibility route.
    /// </summary>
    public bool DeprecatedModel { get; init; }

    public string Rationale { get; init; } = string.Empty;

    /// <summary>
    /// Indicates that the caller must have an applicable authorization policy.
    /// XRENGINE satisfies this through its standing bounded repository policy.
    /// </summary>
    [JsonIgnore]
    public bool RequiresExplicitCallerAuthorization { get; init; } = true;
}
