namespace XREngine.AgentOrchestration;

/// <summary>
/// Defines the broker-side tool allowlist and mutation authorization for a run.
/// </summary>
public sealed record AgentToolPolicy
{
    public bool AllowMutation { get; init; }

    public bool AllowDestructive { get; init; }

    public bool RequireMutationEvidence { get; init; } = true;

    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    public IReadOnlyList<string> DeniedTools { get; init; } = [];
}
