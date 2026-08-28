using System.Text.Json.Serialization;

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
    /// length independently of any optional run output-token limit.
    /// </summary>
    public string TextVerbosity { get; init; } = "medium";

    public AgentEvidencePacket EvidencePacket { get; init; } = new();

    /// <summary>
    /// Repository text files captured before the run is accepted. Paths are
    /// resolved by the broker and are never opened by the remote model.
    /// </summary>
    public IReadOnlyList<AgentContextFileRequest> ContextFiles { get; init; } = [];

    /// <summary>
    /// Broker-resolved immutable context. Callers cannot populate this field.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<AgentContextFileSnapshot> ContextFileSnapshots { get; init; } = [];

    /// <summary>
    /// Optional read-only repository tools. This policy is independent from
    /// the editor tool and mutation policy.
    /// </summary>
    public AgentRepositoryAccessPolicy RepositoryAccess { get; init; } = new();

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
