namespace XREngine.AgentOrchestration;

/// <summary>
/// Opts a broker worker into bounded, read-only repository discovery.
/// </summary>
public sealed record AgentRepositoryAccessPolicy
{
    public bool Enabled { get; init; }

    /// <summary>
    /// Repository-relative directories that repository tools may inspect.
    /// At least one explicit root is required when access is enabled.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];
}
