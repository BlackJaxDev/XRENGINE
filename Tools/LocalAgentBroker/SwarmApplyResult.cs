namespace XREngine.LocalAgentBroker;

/// <summary>
/// Describes the observable outcome of applying a reviewed swarm proposal.
/// </summary>
public sealed record SwarmApplyResult
{
    public bool Success { get; init; }

    public bool Cancelled { get; init; }

    public IReadOnlyList<string> AppliedPaths { get; init; } = [];

    public string? Failure { get; init; }
}
