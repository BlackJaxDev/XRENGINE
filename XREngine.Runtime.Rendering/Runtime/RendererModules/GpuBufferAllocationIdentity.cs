namespace XREngine.Rendering;

/// <summary>
/// Identifies one exact native buffer allocation and byte range for cached GPU output.
/// </summary>
public readonly record struct GpuBufferAllocationIdentity(
    XRDataBuffer Owner,
    ulong NativeHandle,
    ulong Offset,
    ulong Length,
    uint LifetimeSlot,
    ulong LifetimeGeneration,
    ulong Usage)
{
    /// <summary>Returns true when both values identify the same owner allocation and range.</summary>
    public bool Matches(in GpuBufferAllocationIdentity other)
        => ReferenceEquals(Owner, other.Owner) &&
           NativeHandle == other.NativeHandle &&
           Offset == other.Offset &&
           Length == other.Length &&
           LifetimeSlot == other.LifetimeSlot &&
           LifetimeGeneration == other.LifetimeGeneration &&
           Usage == other.Usage;
}
