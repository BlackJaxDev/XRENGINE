using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed record ComputeDispatchIndirectOp(
        int PassIndex,
        VkRenderProgram Program,
        ComputeDispatchSnapshot Snapshot,
        VkDataBuffer ArgumentOwner,
        Buffer ArgumentBuffer,
        ulong ArgumentOffset,
        string Label,
        FrameOpContext Context) : FrameOp(PassIndex, null, Context);
}
