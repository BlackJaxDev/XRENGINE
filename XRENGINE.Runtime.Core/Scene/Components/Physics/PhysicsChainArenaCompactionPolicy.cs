namespace XREngine.Components;

/// <summary>
/// Decides when an arena may be rebuilt into a new allocation and atomically
/// swapped at a structural boundary. Live arenas are never compacted in place,
/// preserving handle generations while CPU/GPU frames may still reference them.
/// </summary>
public sealed record PhysicsChainArenaCompactionPolicy
{
    public int MinimumCapacity { get; init; } = 1_024;
    public int MinimumReclaimableSlots { get; init; } = 256;
    public double FragmentationThreshold { get; init; } = 0.35;

    public PhysicsChainArenaCompactionDecision Evaluate(
        in PhysicsChainArenaSnapshot snapshot,
        int activeFrameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeFrameCount);
        if (MinimumCapacity < 1 || MinimumReclaimableSlots < 1
            || !double.IsFinite(FragmentationThreshold)
            || FragmentationThreshold is <= 0.0 or >= 1.0)
            throw new InvalidOperationException("Arena compaction policy thresholds are invalid.");

        int reclaimableSlots = Math.Max(0, snapshot.Capacity - snapshot.LiveCount);
        double fragmentation = snapshot.Capacity == 0
            ? 0.0
            : (double)reclaimableSlots / snapshot.Capacity;
        bool required = snapshot.Capacity >= MinimumCapacity
            && reclaimableSlots >= MinimumReclaimableSlots
            && fragmentation >= FragmentationThreshold;
        if (!required)
            return new PhysicsChainArenaCompactionDecision(
                PhysicsChainArenaCompactionDecisionKind.NotRequired,
                snapshot.Capacity,
                snapshot.LiveCount,
                reclaimableSlots,
                fragmentation,
                "Capacity, reclaimable slots, or fragmentation is below threshold.");
        if (activeFrameCount != 0)
            return new PhysicsChainArenaCompactionDecision(
                PhysicsChainArenaCompactionDecisionKind.DeferredUntilFramesComplete,
                snapshot.Capacity,
                snapshot.LiveCount,
                reclaimableSlots,
                fragmentation,
                "One or more active frames still reference the current arena generation.");
        return new PhysicsChainArenaCompactionDecision(
            PhysicsChainArenaCompactionDecisionKind.RebuildAndSwap,
            snapshot.Capacity,
            snapshot.LiveCount,
            reclaimableSlots,
            fragmentation,
            "Rebuild a new arena, remap owners, then retire the old generation behind frame fences.");
    }
}
