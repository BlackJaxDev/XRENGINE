namespace XREngine.Components;

/// <summary>
/// Immutable request metadata. Results are delayed snapshots and must not be
/// interpreted as authoritative current-frame simulation state.
/// </summary>
public sealed record PhysicsChainReadbackRequestInfo
{
    public required PhysicsChainRuntimeHandle InstanceHandle { get; init; }
    public required PhysicsChainReadbackFields Fields { get; init; }
    public required ReadOnlyMemory<int> SelectedElementIndices { get; init; }
    public required long SubmissionFrame { get; init; }
    public required long EarliestCompletionFrame { get; init; }
    public required long ExpiryFrame { get; init; }
    public required int ExpectedByteCount { get; init; }
    public required int ExpectedElementCount { get; init; }
    public PhysicsChainReadbackStatus Status { get; internal set; }
    public long CompletionFrame { get; internal set; } = -1L;
    public PhysicsChainReadbackResult? Result { get; internal set; }
    internal PhysicsChainReadbackGatherPlan? ActiveGatherPlan { get; set; }
}
