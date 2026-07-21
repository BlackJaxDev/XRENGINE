namespace XREngine.Components;

/// <summary>
/// Owned delayed snapshot copied out of the staging ring before its slot is
/// reused. Consumers locate typed values through the gather-plan item ranges.
/// </summary>
public sealed record PhysicsChainReadbackResult
{
    public required PhysicsChainReadbackGatherPlan Plan { get; init; }
    public required ReadOnlyMemory<byte> PackedData { get; init; }
    public required long DeliveryFrame { get; init; }
    public required long LatencyFrames { get; init; }
}
