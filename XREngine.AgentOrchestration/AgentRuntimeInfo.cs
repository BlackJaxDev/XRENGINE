namespace XREngine.AgentOrchestration;

/// <summary>
/// Records non-secret runtime and account evidence returned by a local agent.
/// </summary>
public sealed record AgentRuntimeInfo
{
    /// <summary>Memory-only comparison value used to prevent cross-account conversation replay.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccountFingerprint { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string AuthenticationMode { get; init; } = string.Empty;
    public string ModelEvidence { get; init; } = string.Empty;
    /// <summary>States whether account identity permits reuse of prior conversation messages.</summary>
    public bool SupportsConversationReplay { get; init; } = true;
    public IReadOnlyList<string> EnforcedControls { get; init; } = [];
    public IReadOnlyList<string> UnsupportedControls { get; init; } = [];
}
