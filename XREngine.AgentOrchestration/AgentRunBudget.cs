namespace XREngine.AgentOrchestration;

/// <summary>
/// Bounds provider work, local tool work, output size, and elapsed time for one run.
/// </summary>
public sealed record AgentRunBudget
{
    public int MaxTurns { get; init; } = 3;

    public int MaxToolCalls { get; init; } = 8;

    public int MaxOutputTokens { get; init; } = 4_096;

    public int MaxToolResultBytes { get; init; } = 262_144;

    public int MaxElapsedSeconds { get; init; } = 120;

    public int MaxRetries { get; init; } = 1;

    public int MaxConcurrency { get; init; } = 1;
}
