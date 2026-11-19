namespace XREngine.AgentRuntimes;

/// <summary>Exposes account category and available models without account identifiers or tokens.</summary>
public sealed record CodexConnectionInfo
{
    /// <summary>Memory-only account comparison value. Never persist this value in settings or history.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccountFingerprint { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;
    public string AuthenticationMode { get; init; } = string.Empty;
    public IReadOnlyList<CodexModelInfo> Models { get; init; } = [];
}
