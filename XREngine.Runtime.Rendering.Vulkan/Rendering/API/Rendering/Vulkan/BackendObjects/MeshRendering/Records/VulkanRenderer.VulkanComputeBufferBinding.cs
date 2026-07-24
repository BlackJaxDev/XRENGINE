using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Engine buffer identity plus the immutable Vulkan generation captured when a compute
    /// dispatch is enqueued. Keeping both in one value preserves the existing snapshot
    /// dictionary count and avoids adding hot-path allocations.
    /// </summary>
    internal readonly record struct VulkanComputeBufferBinding(
        XRDataBuffer Data,
        Buffer Buffer,
        ulong Range,
        BufferUsageFlags UsageFlags);
}
