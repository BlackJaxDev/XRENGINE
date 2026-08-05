namespace XREngine.LocalAgentBroker;

/// <summary>
/// Applies the repository's Luna/Terra/Sol policy as a conservative recommendation.
/// </summary>
public static class AgentRouteAdvisor
{
    private static readonly string[] s_hardSolSignals =
    [
        "architecture", "cross-subsystem", "unclear root cause", "data loss", "security",
        "concurrency", "deadlock", "race", "performance regression", "unsafe",
        "resource lifetime", "sophisticated algorithm",
    ];

    private static readonly string[] s_lunaSignals =
    [
        "inventory", "search", "find files", "mechanical", "boilerplate", "documentation",
        "run tests", "run build", "log classification", "rename", "read one", "single read",
        "retrieve file", "file retrieval", "write comments", "comment writing", "xml documentation",
        "snapshot", "extract", "classification", "classify", "deterministic", "read-only",
        "one tool call", "smoke test", "smoke check",
    ];

    private static readonly string[] s_rendererDomainSignals =
    [
        "gpu", "renderer", "rendering", "vulkan", "shader",
    ];

    private static readonly string[] s_complexRendererActionSignals =
    [
        "debug", "diagnose", "root cause", "design", "optimize", "regression", "artifact",
        "failure", "correctness", "race", "lifetime",
    ];

    public static AgentRouteRecommendation Recommend(string objective, IReadOnlyList<string>? constraints = null)
    {
        string combined = objective + "\n" + string.Join('\n', constraints ?? []);
        if (ContainsAny(combined, s_hardSolSignals))
        {
            return new AgentRouteRecommendation
            {
                RecommendedModel = AgentModelCatalog.Sol,
                Rationale = "The task contains a difficult or high-risk architecture, concurrency, GPU, security, or root-cause signal.",
            };
        }

        if (ContainsAny(combined, s_lunaSignals))
        {
            return new AgentRouteRecommendation
            {
                RecommendedModel = AgentModelCatalog.Luna,
                Rationale = "The task appears bounded, reversible, and deterministically verifiable.",
            };
        }


        if (ContainsAny(combined, s_rendererDomainSignals)
            && ContainsAny(combined, s_complexRendererActionSignals))
        {
            return new AgentRouteRecommendation
            {
                RecommendedModel = AgentModelCatalog.Sol,
                Rationale = "The task combines renderer/GPU scope with unresolved debugging, design, correctness, or lifetime reasoning.",
            };
        }

        return new AgentRouteRecommendation
        {
            RecommendedModel = AgentModelCatalog.Terra,
            Rationale = "Terra is the repository default for ordinary implementation, debugging, review, and integration.",
        };
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> signals)
        => signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
