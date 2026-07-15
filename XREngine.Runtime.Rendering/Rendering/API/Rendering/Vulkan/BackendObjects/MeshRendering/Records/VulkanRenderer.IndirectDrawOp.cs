namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record IndirectDrawOp(
        int PassIndex,
        XRFrameBuffer? Target,
        VkDataBuffer IndirectBuffer,
        VkDataBuffer? ParameterBuffer,
        VkMeshRenderer MeshRenderer,
        PendingMeshDraw Draw,
        uint DrawCount,
        uint Stride,
        nuint ByteOffset,
        nuint CountByteOffset,
        bool UseCount,
        VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures,
        FrameOpContext Context) : FrameOp(PassIndex, Target, Context);
}