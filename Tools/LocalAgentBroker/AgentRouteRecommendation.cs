namespace XREngine.LocalAgentBroker;

/// <summary>
/// Advisory routing decision that never launches a model.
/// </summary>
public sealed record AgentRouteRecommendation
{
    public string RecommendedModel { get; init; } = AgentModelCatalog.Terra;

    public string Rationale { get; init; } = string.Empty;

    public bool RequiresExplicitCallerAuthorization { get; init; } = true;
}
