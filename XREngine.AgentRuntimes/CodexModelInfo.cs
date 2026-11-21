namespace XREngine.AgentRuntimes;

/// <summary>Describes an exact model and its effort options from the runtime catalog.</summary>
public sealed record CodexModelInfo
{
    public string Id { get; init; } = string.Empty;
    public IReadOnlyList<string> SupportedReasoningEfforts { get; init; } = [];
    public string? DefaultReasoningEffort { get; init; }
}
