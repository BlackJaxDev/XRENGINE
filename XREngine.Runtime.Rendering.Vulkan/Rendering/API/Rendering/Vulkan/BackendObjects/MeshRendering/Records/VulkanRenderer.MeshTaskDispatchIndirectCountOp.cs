namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record MeshTaskDispatchIndirectCountOp(
        int PassIndex,
        VkDataBuffer IndirectBuffer,
        VkDataBuffer CountBuffer,
        uint MaxDrawCount,
        uint Stride,
        nuint ByteOffset,
        nuint CountByteOffset,
        VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);
}