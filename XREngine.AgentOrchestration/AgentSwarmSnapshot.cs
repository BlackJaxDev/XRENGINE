namespace XREngine.AgentOrchestration;

/// <summary>Immutable progress view for a complete agent swarm.</summary>
public sealed record AgentSwarmSnapshot
{
    public string RunId { get; init; } = string.Empty;
    public AgentRunStatus Status { get; init; }
    public IReadOnlyList<AgentSwarmNodeSnapshot> Nodes { get; init; } = [];
    public IReadOnlyList<string> Failures { get; init; } = [];
    public AgentTokenUsage Usage { get; init; } = new();

    /// <summary>Total number of leaf artifacts admitted to this snapshot.</summary>
    public int ArtifactCount { get; init; }

    /// <summary>Output tokens conservatively reserved across all settled and in-flight phases.</summary>
    public long ReservedOutputTokens { get; init; }

    /// <summary>Rendered artifact characters conservatively reserved by leaf proposals.</summary>
    public long ReservedArtifactCharacters { get; init; }
}
