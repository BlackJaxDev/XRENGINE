namespace XREngine.LocalAgentBroker;

/// <summary>
/// Exact public model IDs approved by the repository routing policy.
/// </summary>
public static class AgentModelCatalog
{
    public const string Luna = "gpt-5.6-luna";
    public const string Terra = "gpt-5.6-terra";
    public const string Sol = "gpt-5.6-sol";

    private static readonly HashSet<string> s_modelSet =
        new(StringComparer.Ordinal) { Luna, Terra, Sol };

    public static IReadOnlyList<string> Models { get; } =
        Array.AsReadOnly([Luna, Terra, Sol]);

    public static bool IsApproved(string model)
        => s_modelSet.Contains(model);

    /// <summary>
    /// Response controls are accepted only for the broker's exact approved
    /// model IDs. Aliases and dated provider snapshots are not normalized.
    /// </summary>
    public static bool SupportsResponseControls(string model)
        => IsApproved(model);
}
