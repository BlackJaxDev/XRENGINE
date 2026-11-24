namespace XREngine.AgentRuntimes;

/// <summary>Reports client capabilities without exposing account identifiers or credentials.</summary>
public sealed record SubscriptionConnectionInfo
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccountFingerprint { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string AuthenticationMode { get; init; } = string.Empty;
    /// <summary>Confirms native sign-in when the client does not expose an account identifier.</summary>
    public bool IsAuthenticated { get; init; }
    /// <summary>Allows prior messages only when the client can detect account changes.</summary>
    public bool SupportsConversationReplay { get; init; } = true;
    public IReadOnlyList<SubscriptionModelInfo> Models { get; init; } = [];
}
