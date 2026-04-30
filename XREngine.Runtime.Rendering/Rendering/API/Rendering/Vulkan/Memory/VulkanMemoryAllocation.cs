using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a single Vulkan memory allocation, which may be a dedicated allocation
/// (offset = 0, owns the full DeviceMemory) or a suballocation within a larger block.
/// </summary>
internal readonly record struct VulkanMemoryAllocation(
    DeviceMemory Memory,
    ulong Offset,
    ulong Size,
    uint MemoryTypeIndex,
    MemoryPropertyFlags Properties,
    int BlockId)
{
    /// <summary>A sentinel allocation representing a failed or empty allocation.</summary>
    public static VulkanMemoryAllocation Null => default;

    public bool IsNull => Memory.Handle == 0;

    /// <summary>Whether this allocation is host-visible and can be mapped.</summary>
    public bool IsHostVisible => Properties.HasFlag(MemoryPropertyFlags.HostVisibleBit);

    /// <summary>Whether this allocation uses coherent memory that doesn't need flush/invalidate.</summary>
    public bool IsCoherent => Properties.HasFlag(MemoryPropertyFlags.HostCoherentBit);
}
