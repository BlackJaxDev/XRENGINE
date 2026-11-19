namespace XREngine.AgentRuntimes;

/// <summary>Maps a client model to its label and advertised effort options.</summary>
public sealed record SubscriptionModelInfo(string Id, string DisplayName)
{
    public IReadOnlyList<string> SupportedReasoningEfforts { get; init; } = [];
    public string? DefaultReasoningEffort { get; init; }
}
