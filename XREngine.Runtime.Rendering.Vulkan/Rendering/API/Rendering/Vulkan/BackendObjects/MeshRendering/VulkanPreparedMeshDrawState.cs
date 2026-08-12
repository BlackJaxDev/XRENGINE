using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Backend-ready state consumed by the prepared mesh encoder without
/// traversing live material or reflected binding data.
/// </summary>
internal readonly record struct VulkanPreparedMeshDrawState(
    PipelineLayout PipelineLayout,
    bool UsesDescriptorHeap,
    uint DescriptorHeapPushByteCount,
    VulkanPreparedStreamRange DescriptorBindings,
    VulkanPreparedStreamRange DynamicOffsets,
    VulkanPreparedStreamRange DescriptorImagePayloads,
    VulkanPreparedStreamRange DescriptorImageRequirements,
    VulkanPreparedStreamRange DescriptorHeapPushDwords,
    VulkanPreparedStreamRange VertexBuffers,
    VulkanPreparedMeshPrimitive Primitive0,
    VulkanPreparedMeshPrimitive Primitive1,
    VulkanPreparedMeshPrimitive Primitive2,
    int PrimitiveCount,
    int FrameIndex,
    int DrawUniformSlot,
    VulkanPreparedStreamRange FrameDataPayloadHandles,
    VkMeshRenderer.MeshDrawPushConstants PushConstants,
    uint InstanceCount,
    int ColdDataIndex)
{
    internal VulkanPreparedMeshPrimitive GetPrimitive(int index)
        => index switch
        {
            0 when PrimitiveCount > 0 => Primitive0,
            1 when PrimitiveCount > 1 => Primitive1,
            2 when PrimitiveCount > 2 => Primitive2,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

}
