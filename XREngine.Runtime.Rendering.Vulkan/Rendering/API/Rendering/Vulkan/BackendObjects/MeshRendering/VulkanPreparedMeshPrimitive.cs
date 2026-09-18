using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Fully resolved native state for one primitive topology emitted by a
/// prepared mesh draw.
/// </summary>
internal readonly record struct VulkanPreparedMeshPrimitive(
    Pipeline Pipeline,
    PrimitiveTopology Topology,
    VkBufferHandle IndexBuffer,
    IndexType IndexType,
    uint ElementCount,
    bool Indexed)
{
    internal bool Matches(VulkanPreparedMeshPrimitive other)
        => Pipeline.Handle == other.Pipeline.Handle &&
           Topology == other.Topology &&
           IndexBuffer.Handle == other.IndexBuffer.Handle &&
           IndexType == other.IndexType &&
           ElementCount == other.ElementCount &&
           Indexed == other.Indexed;
}
