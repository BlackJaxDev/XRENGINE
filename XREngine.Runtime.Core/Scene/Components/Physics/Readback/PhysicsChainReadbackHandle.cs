namespace XREngine.Components;

public readonly record struct PhysicsChainReadbackHandle(int Slot, uint Generation)
{
    public static PhysicsChainReadbackHandle Invalid => new(-1, 0u);
    public bool IsValid => Slot >= 0 && Generation != 0u;
}
