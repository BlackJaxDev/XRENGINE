namespace XREngine.AgentOrchestration;

/// <summary>A single exact, review-approved text replacement proposed by a leaf.</summary>
public sealed record AgentSwarmCodeChange
{
    public string Path { get; init; } = string.Empty;
    public string BaseSha256 { get; init; } = string.Empty;
    public string OldText { get; init; } = string.Empty;
    public string NewText { get; init; } = string.Empty;
    public string NodeId { get; init; } = string.Empty;
}
