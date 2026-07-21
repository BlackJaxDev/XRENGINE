namespace XREngine.Components;

/// <summary>
/// Generation-safe claim on one of the world's three readback staging slots.
/// </summary>
public readonly record struct PhysicsChainReadbackStagingLease(
    int Slot,
    uint Generation,
    PhysicsChainReadbackHandle RequestHandle,
    int ByteCount)
{
    public bool IsValid
        => Slot >= 0 && Generation != 0u && RequestHandle.IsValid && ByteCount > 0;
}
