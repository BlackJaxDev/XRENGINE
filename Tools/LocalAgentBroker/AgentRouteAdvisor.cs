namespace XREngine.LocalAgentBroker;

/// <summary>
/// Applies the repository's Luna/Terra/Sol policy as a conservative recommendation.
/// </summary>
public static class AgentRouteAdvisor
{
    private static readonly string[] s_solSignals =
    [
        "architecture", "cross-subsystem", "unclear root cause", "data loss", "security",
        "concurrency", "deadlock", "race", "gpu", "renderer", "vulkan", "shader",
        "performance regression", "unsafe",
    ];

    private static readonly string[] s_lunaSignals =
    [
        "inventory", "search", "find files", "mechanical", "boilerplate", "documentation",
        "run tests", "run build", "log classification", "rename",
    ];

    public static AgentRouteRecommendation Recommend(string objective, IReadOnlyList<string>? constraints = null)
    {
        string combined = objective + "\n" + string.Join('\n', constraints ?? []);
        if (s_solSignals.Any(signal => combined.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            return new AgentRouteRecommendation
            {
                RecommendedModel = AgentModelCatalog.Sol,
                Rationale = "The task contains a difficult or high-risk architecture, concurrency, GPU, security, or root-cause signal.",
            };
        }

        if (s_lunaSignals.Any(signal => combined.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            return new AgentRouteRecommendation
            {
                RecommendedModel = AgentModelCatalog.Luna,
                Rationale = "The task appears bounded, reversible, and deterministically verifiable.",
            };
        }

        return new AgentRouteRecommendation
        {
            RecommendedModel = AgentModelCatalog.Terra,
            Rationale = "Terra is the repository default for ordinary implementation, debugging, review, and integration.",
        };
    }
}
