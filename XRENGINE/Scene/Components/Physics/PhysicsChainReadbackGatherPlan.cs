namespace XREngine.Components;

/// <summary>
/// Immutable compact gather manifest consumed by a CPU or GPU backend.
/// </summary>
public sealed record PhysicsChainReadbackGatherPlan
{
    public required PhysicsChainReadbackHandle RequestHandle { get; init; }
    public required PhysicsChainRuntimeHandle InstanceHandle { get; init; }
    public required PhysicsChainReadbackSourceEpoch SourceEpoch { get; init; }
    public required long GatherFrame { get; init; }
    public required int ElementCount { get; init; }
    public required int ByteCount { get; init; }
    public required ReadOnlyMemory<PhysicsChainReadbackGatherItem> Items { get; init; }
}
