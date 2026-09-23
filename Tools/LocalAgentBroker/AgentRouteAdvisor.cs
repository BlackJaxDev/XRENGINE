namespace XREngine.LocalAgentBroker;

/// <summary>
/// Applies the repository's GPT-6 Astra/Luna/Sol policy as a conservative recommendation.
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

    /// <summary>
    /// Recommends an exact model in the selected supported model family.
    /// </summary>
    /// <param name="objective">The bounded delegated objective to classify.</param>
    /// <param name="constraints">Constraints that affect task difficulty or risk.</param>
    /// <param name="modelFamily">
    /// Exact model family to route within. Omitting it uses the preferred GPT-6 family.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="modelFamily"/> is unsupported.</exception>
    public static AgentRouteRecommendation Recommend(
        string objective,
        IReadOnlyList<string>? constraints = null,
        string modelFamily = AgentModelCatalog.Gpt6Family)
    {
        if (!AgentModelCatalog.IsApprovedModelFamily(modelFamily))
        {
            throw new ArgumentException(
                $"model_family must be exactly one of: {string.Join(", ", AgentModelCatalog.ModelFamilies)}",
                nameof(modelFamily));
        }

        string combined = objective + "\n" + string.Join('\n', constraints ?? []);
        if (ContainsAny(combined, s_hardSolSignals))
        {
            return new AgentRouteRecommendation
            {
                RecommendedModel = modelFamily == AgentModelCatalog.Gpt6Family
                    ? AgentModelCatalog.Astra6
                    : AgentModelCatalog.Sol,
                ModelFamily = modelFamily,
                DeprecatedModel = AgentModelCatalog.IsDeprecated(modelFamily == AgentModelCatalog.Gpt6Family ? AgentModelCatalog.Astra6 : AgentModelCatalog.Sol),
                Rationale = "The task contains a difficult or high-risk architecture, concurrency, GPU, security, or root-cause signal.",
            };
        }

        if (ContainsAny(combined, s_lunaSignals))
        {
            return new AgentRouteRecommendation
            {
                RecommendedModel = modelFamily == AgentModelCatalog.Gpt6Family
                    ? AgentModelCatalog.Luna6
                    : AgentModelCatalog.Luna,
                ModelFamily = modelFamily,
                DeprecatedModel = AgentModelCatalog.IsDeprecated(modelFamily == AgentModelCatalog.Gpt6Family ? AgentModelCatalog.Luna6 : AgentModelCatalog.Luna),
                Rationale = "The task appears bounded, reversible, and deterministically verifiable.",
            };
        }


        if (ContainsAny(combined, s_rendererDomainSignals)
            && ContainsAny(combined, s_complexRendererActionSignals))
        {
            return new AgentRouteRecommendation
            {
                RecommendedModel = modelFamily == AgentModelCatalog.Gpt6Family
                    ? AgentModelCatalog.Astra6
                    : AgentModelCatalog.Sol,
                ModelFamily = modelFamily,
                DeprecatedModel = AgentModelCatalog.IsDeprecated(modelFamily == AgentModelCatalog.Gpt6Family ? AgentModelCatalog.Astra6 : AgentModelCatalog.Sol),
                Rationale = "The task combines renderer/GPU scope with unresolved debugging, design, correctness, or lifetime reasoning.",
            };
        }

        return new AgentRouteRecommendation
        {
            RecommendedModel = modelFamily == AgentModelCatalog.Gpt6Family
                ? AgentModelCatalog.Sol6
                : AgentModelCatalog.Terra,
            ModelFamily = modelFamily,
            DeprecatedModel = AgentModelCatalog.IsDeprecated(modelFamily == AgentModelCatalog.Gpt6Family ? AgentModelCatalog.Sol6 : AgentModelCatalog.Terra),
            Rationale = modelFamily == AgentModelCatalog.Gpt6Family
                ? "GPT-6 Sol is the default for ordinary implementation, debugging, review, and integration."
                : "Terra is the repository default for ordinary implementation, debugging, review, and integration.",
        };
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> signals)
        => signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
