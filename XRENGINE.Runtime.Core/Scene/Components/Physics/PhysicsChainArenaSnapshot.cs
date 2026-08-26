namespace XREngine.Components;

/// <summary>
/// Delayed-friendly capacity and fragmentation diagnostics for a chain arena.
/// </summary>
public readonly record struct PhysicsChainArenaSnapshot(
    int Capacity,
    int LiveCount,
    int FreeSlotCount,
    int GrowthCount,
    float FragmentationRatio);
