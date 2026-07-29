using System.Collections.Concurrent;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns engine-allocated Vulkan image allocations and their copied diagnostics.
/// Imported/external images are intentionally absent unless ownership transfers
/// to the renderer allocator.
/// </summary>
internal sealed class VulkanImageAllocationTracker
{
    internal ConcurrentDictionary<ulong, VulkanMemoryAllocation> Allocations { get; } = new();
    internal ConcurrentDictionary<ulong, VulkanImageAllocationDebugInfo> DebugInfo { get; } = new();
}
