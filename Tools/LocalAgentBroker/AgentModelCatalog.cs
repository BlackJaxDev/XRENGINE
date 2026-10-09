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

    /// <summary>
    /// GPT-6.1 Sol model available for explicit broker runs.
    /// </summary>
    public const string Sol61 = "gpt-6.1-sol";

    public const string ClaudeFable51 = "claude-fable-5-1";
    public const string ClaudeOpus55 = "claude-opus-5-5";
    public const string ClaudeSonnet55 = "claude-sonnet-5-5";
    public const string ClaudeHaiku55 = "claude-haiku-5-5";

    private static readonly IReadOnlyList<string> s_allReasoningEfforts =
        Array.AsReadOnly(["none", "low", "medium", "high", "xhigh", "max"]);

    private static readonly IReadOnlyList<string> s_reasoningEffortsWithoutNone =
        Array.AsReadOnly(["low", "medium", "high", "xhigh", "max"]);

    private static readonly HashSet<string> s_modelSet =
        new(StringComparer.Ordinal)
        {
            Luna, Terra, Sol, Astra6, Luna6, Sol6, Sol61,
            ClaudeFable51, ClaudeOpus55, ClaudeSonnet55, ClaudeHaiku55,
        };

    public static IReadOnlyList<string> Models { get; } =
        Array.AsReadOnly([
            Luna6, Sol6, Sol61, Astra6, Luna, Terra, Sol,
            ClaudeFable51, ClaudeOpus55, ClaudeSonnet55, ClaudeHaiku55,
        ]);

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
    /// Selects Anthropic only for an exact supported Claude model ID.
    /// </summary>
    public static bool IsAnthropic(string model)
        => model is ClaudeFable51 or ClaudeOpus55 or ClaudeSonnet55 or ClaudeHaiku55;

    /// <summary>
    /// Identifies the exact models supported by the bounded code swarm.
    /// </summary>
    public static bool SupportsSwarm(string model)
        => model is Luna6 or ClaudeHaiku55;

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
            Astra6 or Sol61 or ClaudeFable51 or ClaudeOpus55 or ClaudeSonnet55 or ClaudeHaiku55
                => s_reasoningEffortsWithoutNone,
            Luna or Terra or Sol or Luna6 or Sol6 => s_allReasoningEfforts,
            _ => [],
        };

    /// <summary>
    /// Determines whether the exact requested model accepts the supplied provider effort.
    /// </summary>
    public static bool SupportsReasoningEffort(string model, string reasoningEffort)
        => GetSupportedReasoningEfforts(model).Contains(reasoningEffort, StringComparer.OrdinalIgnoreCase);
}
