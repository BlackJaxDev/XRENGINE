namespace XREngine.AgentOrchestration;

/// <summary>
/// One provider turn, including opaque provider continuation state.
/// </summary>
public sealed record AgentModelTurnRequest
{
    public required AgentRunRequest Run { get; init; }

    public required string Prompt { get; init; }

    public required IReadOnlyList<AgentToolDefinition> Tools { get; init; }

    public string? ContinuationJson { get; init; }

    public IReadOnlyList<AgentModelToolOutput> ToolOutputs { get; init; } = [];

    public int TurnIndex { get; init; }

    public bool ForceTextResponse { get; init; }

    /// <summary>
    /// One-based provider attempt number within this turn.
    /// </summary>
    public int AttemptNumber { get; init; } = 1;

    /// <summary>
    /// Remaining run-wide output-token budget for this provider turn.
    /// </summary>
    public int MaxOutputTokens { get; init; }
}
