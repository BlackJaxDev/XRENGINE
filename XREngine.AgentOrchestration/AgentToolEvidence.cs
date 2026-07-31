namespace XREngine.AgentOrchestration;

/// <summary>
/// Bounded evidence retained from a completed tool call.
/// </summary>
public sealed record AgentToolEvidence
{
    public string CallId { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    public string ArgumentsSummary { get; init; } = string.Empty;

    public string ResultSummary { get; init; } = string.Empty;

    public bool IsError { get; init; }

    public bool IsMutation { get; init; }

    public bool IsVisualEvidence { get; init; }

    public string? EvidencePath { get; init; }
}
