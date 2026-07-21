namespace XREngine.Components;

/// <summary>
/// Identifies a physics-chain runtime slot without allowing a recycled slot to
/// be mistaken for the previous occupant.
/// </summary>
public readonly record struct PhysicsChainRuntimeHandle(int Slot, uint Generation)
{
    public static PhysicsChainRuntimeHandle Invalid => new(-1, 0u);

    public bool IsValid => Slot >= 0 && Generation != 0u;
}
