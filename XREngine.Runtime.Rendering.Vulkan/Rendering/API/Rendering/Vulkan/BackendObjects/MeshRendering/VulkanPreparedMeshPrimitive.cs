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
    bool Indexed);
