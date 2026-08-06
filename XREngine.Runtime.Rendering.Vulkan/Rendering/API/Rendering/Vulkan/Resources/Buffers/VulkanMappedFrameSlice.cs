using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// A validated view of one frame-slot-owned range in a persistently mapped Vulkan buffer.
/// The value deliberately contains no host pointer: callers can only obtain writable bytes
/// through <see cref="VulkanMappedFrameArena.TryBeginWrite"/>.
/// </summary>
internal readonly record struct VulkanMappedFrameSlice(
    ulong ArenaIdentity,
    ulong BufferIdentity,
    ulong MemoryIdentity,
    ulong Offset,
    uint Length,
    uint Alignment,
    int FrameSlot,
    ulong Generation,
    Buffer Buffer,
    DeviceMemory Memory)
{
    internal bool IsValid =>
        ArenaIdentity != 0 &&
        BufferIdentity != 0 &&
        MemoryIdentity != 0 &&
        Length != 0 &&
        FrameSlot >= 0 &&
        Generation != 0 &&
        Buffer.Handle == BufferIdentity &&
        Memory.Handle == MemoryIdentity;
}
