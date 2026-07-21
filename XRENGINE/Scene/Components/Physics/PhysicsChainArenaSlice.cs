namespace XREngine.Components;

/// <summary>
/// Stable generational range within a physics-chain buffer arena.
/// </summary>
public readonly record struct PhysicsChainArenaSlice(int Offset, int Count, uint Generation)
{
    public static PhysicsChainArenaSlice Invalid => new(-1, 0, 0u);
    public bool IsValid => Offset >= 0 && Count > 0 && Generation != 0u;
}
