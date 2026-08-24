using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// A checked view of host-visible Vulkan memory.  This value contains no native pointer;
/// callers must acquire a short-lived lease before touching its bytes.
/// </summary>
internal readonly record struct VulkanMappedMemorySlice(
    Buffer Buffer,
    DeviceMemory Memory,
    ulong AllocationOffset,
    ulong AllocationSize,
    ulong Offset,
    ulong Length,
    ulong RequiredAlignment,
    ulong DeviceIdentity,
    long AllocationGeneration,
    bool IsCoherent,
    bool IsHostVisible)
{
    internal ulong End => checked(Offset + Length);
    internal ulong AllocationEnd => checked(AllocationOffset + AllocationSize);
    internal ulong MemoryOffset => checked(AllocationOffset + Offset);
}
