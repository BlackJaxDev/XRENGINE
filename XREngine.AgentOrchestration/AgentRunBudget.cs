namespace XREngine.AgentOrchestration;

/// <summary>
/// Bounds provider work, local tool work, output size, and elapsed time for one run.
/// </summary>
public sealed record AgentRunBudget
{
    public int MaxTurns { get; init; } = 10;

    public int MaxToolCalls { get; init; } = 24;

    public int MaxOutputTokens { get; init; } = 8_192;

    public int MaxToolResultBytes { get; init; } = 262_144;

    public int MaxElapsedSeconds { get; init; } = 300;

    public int MaxRetries { get; init; } = 2;

    public int MaxConcurrency { get; init; } = 1;
}
