namespace XREngine.Components;

/// <summary>Out-of-band arena compaction decision; never mutates live slots.</summary>
public readonly record struct PhysicsChainArenaCompactionDecision(
    PhysicsChainArenaCompactionDecisionKind Kind,
    int Capacity,
    int LiveCount,
    int ReclaimableSlots,
    double FragmentationRatio,
    string Reason);
