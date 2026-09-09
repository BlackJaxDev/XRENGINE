using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable logical bounds and creation usage for a live Vulkan buffer.
/// Allocation sizes are deliberately excluded because allocator blocks can be larger than the buffer.
/// </summary>
internal readonly record struct VulkanBufferDescriptorMetadata(
    ulong LogicalSize,
    BufferUsageFlags Usage);
