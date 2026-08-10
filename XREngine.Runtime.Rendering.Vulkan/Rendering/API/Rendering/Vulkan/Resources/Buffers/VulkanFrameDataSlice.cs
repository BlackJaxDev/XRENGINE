using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Validated native buffer range for one usage lane, chunk, and frame slot.</summary>
internal readonly record struct VulkanFrameDataSlice(
    ulong ArenaIdentity,
    ulong BufferIdentity,
    ulong MemoryIdentity,
    EVulkanFrameDataLane Lane,
    int ChunkIndex,
    int FrameSlot,
    ulong Offset,
    uint Length,
    uint Alignment,
    ulong Generation,
    Buffer Buffer,
    DeviceMemory Memory)
{
    internal bool IsValid => ArenaIdentity != 0 && BufferIdentity != 0 && MemoryIdentity != 0 &&
        ChunkIndex >= 0 && FrameSlot >= 0 && Length != 0 && Alignment != 0 && Generation != 0 &&
        Buffer.Handle == BufferIdentity && Memory.Handle == MemoryIdentity;
}
