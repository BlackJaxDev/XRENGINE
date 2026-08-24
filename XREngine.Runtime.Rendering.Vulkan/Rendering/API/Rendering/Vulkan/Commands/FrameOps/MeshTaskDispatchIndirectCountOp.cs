using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed record MeshTaskDispatchIndirectCountOp(
    int PassIndex,
    VkRenderProgram Program,
    ulong ProgramLinkGeneration,
    ComputeDispatchSnapshot ProgramBindingSnapshot,
    VulkanMeshProducerSnapshot ProducerSnapshot,
    Pipeline Pipeline,
    VkDataBuffer IndirectBuffer,
    VkDataBuffer CountBuffer,
    uint MaxDrawCount,
    uint Stride,
    nuint ByteOffset,
    nuint CountByteOffset,
    VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures,
    FrameOpContext Context) 
    : FrameOp(PassIndex, ProducerSnapshot.Target, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount;

}
