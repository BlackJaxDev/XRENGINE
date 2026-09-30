namespace XREngine.Components;

/// <summary>
/// Identifies one allocation in a physics-chain arena without aliasing a slot
/// after it has been freed and reused.
/// </summary>
public readonly record struct PhysicsChainArenaHandle(int Slot, uint Generation)
{
    public static PhysicsChainArenaHandle Invalid => new(-1, 0u);
    public bool IsValid => Slot >= 0 && Generation != 0u;
}
