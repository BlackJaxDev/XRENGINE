namespace XREngine.AgentOrchestration;

/// <summary>
/// Provider-neutral input for one explicitly routed agent run.
/// </summary>
public sealed record AgentRunRequest
{
    public string Objective { get; init; } = string.Empty;

    public IReadOnlyList<string> SuccessCriteria { get; init; } = [];

    public IReadOnlyList<string> Constraints { get; init; } = [];

    public string RequestedModel { get; init; } = string.Empty;

    public string ReasoningEffort { get; init; } = "medium";

    /// <summary>
    /// Requested Responses API text verbosity. This shapes visible response
    /// length but never increases the run's hard output-token budget.
    /// </summary>
    public string TextVerbosity { get; init; } = "medium";

    public AgentEvidencePacket EvidencePacket { get; init; } = new();

    /// <summary>
    /// Names the editor MCP session available to the worker. Leave unset for a
    /// reasoning-only run with no local tools.
    /// </summary>
    public string? EditorSession { get; init; }

    public AgentToolPolicy ToolPolicy { get; init; } = new();

    public AgentRunBudget Budget { get; init; } = new();

    public IReadOnlyList<AgentHostedTool> HostedTools { get; init; } = [];

    public bool UseCompactHandoffPrompt { get; init; } = true;

    public bool RequireToolUse { get; init; }

    /// <summary>
    /// Uses Responses API background execution and polling for each provider turn.
    /// </summary>
    public bool UseBackgroundMode { get; init; }

    public string SystemInstructions { get; init; } = string.Empty;

    public string? InitialImageDataUri { get; init; }

    public string AdditionalInstructions { get; init; } = string.Empty;
}
