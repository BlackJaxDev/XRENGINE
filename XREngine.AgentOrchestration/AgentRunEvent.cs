namespace XREngine.AgentOrchestration;

/// <summary>
/// A provider-neutral progress event emitted during orchestration.
/// </summary>
public sealed record AgentRunEvent
{
    public AgentRunEventKind Kind { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? ToolName { get; init; }

    public string? CallId { get; init; }

    public AgentToolResult? ToolResult { get; init; }

    public AgentToolEvidence? ToolEvidence { get; init; }

    public AgentTokenUsage? Usage { get; init; }

    public AgentProviderAttemptDiagnostic? ProviderAttempt { get; init; }
}
