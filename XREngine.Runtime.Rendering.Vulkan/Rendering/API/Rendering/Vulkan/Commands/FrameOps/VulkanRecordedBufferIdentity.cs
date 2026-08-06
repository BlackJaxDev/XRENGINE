namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact native identity of a buffer referenced by a recorded packet.
/// </summary>
internal readonly record struct VulkanRecordedBufferIdentity(
    EVulkanRecordedBufferBindingKind Kind,
    uint Binding,
    ulong BufferHandle,
    ulong AllocationGeneration,
    ulong Offset,
    ulong Range)
{
    public bool IsBound => BufferHandle != 0UL;

    public bool IsComplete =>
        !IsBound ||
        (AllocationGeneration != 0UL && Range != 0UL);
}
