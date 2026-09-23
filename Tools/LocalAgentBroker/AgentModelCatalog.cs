namespace XREngine.LocalAgentBroker;

/// <summary>
/// Exact public model IDs approved by the repository routing policy.
/// </summary>
public static class AgentModelCatalog
{
    public const string Gpt56Family = "gpt-5.6";
    public const string Gpt6Family = "gpt-6";

    /// <summary>
    /// Deprecated GPT-5.6 Luna model retained for explicit compatibility requests.
    /// </summary>
    public const string Luna = "gpt-5.6-luna";
    public const string Terra = "gpt-5.6-terra";
    public const string Sol = "gpt-5.6-sol";
    public const string Astra6 = "gpt-6-astra";
    public const string Luna6 = "gpt-6-luna";
    public const string Sol6 = "gpt-6-sol";

    private static readonly IReadOnlyList<string> s_allReasoningEfforts =
        Array.AsReadOnly(["none", "low", "medium", "high", "xhigh", "max"]);

    private static readonly IReadOnlyList<string> s_gpt6AstraReasoningEfforts =
        Array.AsReadOnly(["low", "medium", "high", "xhigh", "max"]);

    private static readonly HashSet<string> s_modelSet =
        new(StringComparer.Ordinal) { Luna, Terra, Sol, Astra6, Luna6, Sol6 };

    public static IReadOnlyList<string> Models { get; } =
        Array.AsReadOnly([Luna6, Sol6, Astra6, Luna, Terra, Sol]);

    /// <summary>
    /// Preferred GPT-6 models for all new broker routing and agent configuration.
    /// </summary>
    public static IReadOnlyList<string> PreferredModels6 { get; } =
        Array.AsReadOnly([Astra6, Luna6, Sol6]);

    public static IReadOnlyList<string> ModelFamilies { get; } =
        Array.AsReadOnly([Gpt6Family, Gpt56Family]);

    public static bool IsApproved(string model)
        => s_modelSet.Contains(model);

    /// <summary>
    /// Indicates that a model remains supported only for an explicit legacy request.
    /// </summary>
    public static bool IsDeprecated(string model)
        => model is Luna or Terra or Sol;

    public static bool IsApprovedModelFamily(string modelFamily)
        => ModelFamilies.Contains(modelFamily, StringComparer.Ordinal);

    /// <summary>
    /// Response controls are accepted only for the broker's exact approved
    /// model IDs. Aliases and dated provider snapshots are not normalized.
    /// </summary>
    public static bool SupportsResponseControls(string model)
        => IsApproved(model);

    /// <summary>
    /// Gets the exact reasoning effort values accepted by an approved model.
    /// </summary>
    public static IReadOnlyList<string> GetSupportedReasoningEfforts(string model)
        => model switch
        {
            Astra6 => s_gpt6AstraReasoningEfforts,
            Luna or Terra or Sol or Luna6 or Sol6 => s_allReasoningEfforts,
            _ => [],
        };

    /// <summary>
    /// Determines whether the exact requested model accepts the supplied provider effort.
    /// </summary>
    public static bool SupportsReasoningEffort(string model, string reasoningEffort)
        => GetSupportedReasoningEfforts(model).Contains(reasoningEffort, StringComparer.OrdinalIgnoreCase);
}
