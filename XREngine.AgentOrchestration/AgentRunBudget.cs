namespace XREngine.AgentOrchestration;

/// <summary>
/// Controls provider work, local tool work, output size, and elapsed time for one run.
/// </summary>
public sealed record AgentRunBudget
{
    public int MaxTurns { get; init; } = 3;

    public int MaxToolCalls { get; init; } = 8;

    /// <summary>
    /// Optional run-wide output-token limit. A value of <c>0</c> disables the
    /// broker limit and omits <c>max_output_tokens</c> from provider requests.
    /// The provider and selected model may still impose their own limits.
    /// </summary>
    public int MaxOutputTokens { get; init; }

    public int MaxToolResultBytes { get; init; } = 262_144;

    /// <summary>Maximum number of repository files snapshotted at admission.</summary>
    public int MaxContextFiles { get; init; } = 16;

    /// <summary>Maximum raw size of one context file.</summary>
    public int MaxContextFileBytes { get; init; } = 262_144;

    /// <summary>Maximum aggregate raw size of all context files.</summary>
    public int MaxContextBytes { get; init; } = 1_048_576;

    /// <summary>
    /// Maximum UTF-8 size after context content and metadata are JSON-escaped
    /// into provider input blocks.
    /// </summary>
    public int MaxContextRenderedBytes { get; init; } = 2_097_152;

    /// <summary>
    /// Optional whole-run timeout in seconds. A value of <c>0</c> disables the
    /// broker elapsed-time timeout; caller cancellation remains available.
    /// </summary>
    public int MaxElapsedSeconds { get; init; }

    public int MaxRetries { get; init; } = 1;

    public int MaxConcurrency { get; init; } = 1;
}
