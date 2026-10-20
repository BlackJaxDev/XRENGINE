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

    internal bool Matches(VulkanFrameDataSlice other)
        => ArenaIdentity == other.ArenaIdentity &&
           BufferIdentity == other.BufferIdentity &&
           MemoryIdentity == other.MemoryIdentity &&
           Lane == other.Lane &&
           ChunkIndex == other.ChunkIndex &&
           FrameSlot == other.FrameSlot &&
           Offset == other.Offset &&
           Length == other.Length &&
           Alignment == other.Alignment &&
           Generation == other.Generation &&
           Buffer.Handle == other.Buffer.Handle &&
           Memory.Handle == other.Memory.Handle;

    /// <summary>
    /// Compares all fields. The check compares the native handles by value, so it does not box
    /// the Silk.NET handle structs.
    /// </summary>
    public bool Equals(VulkanFrameDataSlice other) => Matches(other);

    /// <summary>Combines primitive fields only, so the hash stays consistent with <see cref="Equals(VulkanFrameDataSlice)"/> and does not box.</summary>
    public override int GetHashCode()
        => HashCode.Combine(ArenaIdentity, BufferIdentity, MemoryIdentity, ChunkIndex, FrameSlot, Offset, Length, Generation);
}
