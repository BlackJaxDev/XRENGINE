namespace XREngine.AgentOrchestration;

/// <summary>
/// Bounds a read-only hierarchical GPT-6 Luna swarm proposal run.
/// </summary>
public sealed record AgentSwarmOptions
{
    public int MaxDepth { get; init; } = 3;
    public int MaxAgents { get; init; } = 16;
    public int MaxChildren { get; init; } = 4;
    public int MaxParallelAgents { get; init; } = 3;
    public int MaxOutputTokens { get; init; } = 131_072;
    public int MaxPhaseOutputTokens { get; init; } = 8_192;
    public int MaxElapsedSeconds { get; init; } = 900;
    public int MaxChangedLinesPerLeaf { get; init; } = 120;
    public bool AutoApply { get; init; }
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];
}
