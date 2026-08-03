namespace XREngine.AgentOrchestration;

/// <summary>
/// Provider-neutral outcome from one model turn.
/// </summary>
public sealed record AgentModelTurnResult
{
    public string ResponseId { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    public string OutputText { get; init; } = string.Empty;

    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<AgentOutputItem> OutputItems { get; init; } = [];

    public AgentTokenUsage Usage { get; init; } = new();

    public AgentProviderAttemptDiagnostic ProviderAttempt { get; init; } = new();

    public string ContinuationJson { get; init; } = "[]";
}
