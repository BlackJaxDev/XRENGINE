using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Backend-ready state consumed by the prepared mesh encoder without
/// traversing live material or reflected binding data.
/// </summary>
internal readonly record struct VulkanPreparedMeshDrawState(
    VkMeshRenderer OwnerIdentity,
    VulkanRenderer Renderer,
    VkRenderProgram Program,
    PipelineLayout PipelineLayout,
    bool UsesDescriptorHeap,
    VulkanPreparedDescriptorSetBinding[]? DescriptorBindings,
    int DescriptorBindingCount,
    uint[]? DynamicOffsets,
    uint[]? DescriptorHeapPushDwords,
    int DescriptorHeapPushDwordCount,
    VkBufferHandle[]? VertexBuffers,
    uint[]? VertexBindings,
    int VertexBufferCount,
    VulkanPreparedMeshPrimitive Primitive0,
    VulkanPreparedMeshPrimitive Primitive1,
    VulkanPreparedMeshPrimitive Primitive2,
    int PrimitiveCount,
    int FrameIndex,
    int DrawUniformSlot,
    ulong FrameDataGeneration,
    VulkanPreparedFrameDataPayloadHandle[]? FrameDataPayloadHandles,
    int FrameDataPayloadHandleCount,
    VkMeshRenderer.MeshDrawPushConstants PushConstants,
    uint InstanceCount)
{
    internal VulkanPreparedMeshPrimitive GetPrimitive(int index)
        => index switch
        {
            0 when PrimitiveCount > 0 => Primitive0,
            1 when PrimitiveCount > 1 => Primitive1,
            2 when PrimitiveCount > 2 => Primitive2,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    internal bool HasValidFrameDataPayloadHandles()
    {
        if (FrameDataPayloadHandleCount == 0)
            return true;
        if (FrameDataPayloadHandles is not { } handles ||
            handles.Length < FrameDataPayloadHandleCount)
        {
            return false;
        }

        for (int index = 0; index < FrameDataPayloadHandleCount; index++)
        {
            if (!handles[index].IsValidFor(
                    OwnerIdentity,
                    FrameIndex,
                    DrawUniformSlot,
                    FrameDataGeneration))
            {
                return false;
            }
        }

        return true;
    }
}
