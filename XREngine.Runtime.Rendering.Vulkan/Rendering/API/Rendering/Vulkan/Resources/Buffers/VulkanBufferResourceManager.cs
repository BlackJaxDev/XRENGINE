using System.Collections.Concurrent;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Vulkan buffer allocation registries and the selected memory allocator.
/// Renderer buffer operations are behavior-only clients of this state.
/// </summary>
internal sealed class VulkanBufferResourceManager
{
    internal IVulkanMemoryAllocator? MemoryAllocator { get; set; }
    internal ConcurrentDictionary<ulong, VulkanMemoryAllocation> Allocations { get; } = new();
    internal ConcurrentDictionary<ulong, VulkanMemoryAllocation> LegacyAllocations { get; } = new();
    internal ConcurrentDictionary<ulong, byte> LiveHandles { get; } = new();
}
