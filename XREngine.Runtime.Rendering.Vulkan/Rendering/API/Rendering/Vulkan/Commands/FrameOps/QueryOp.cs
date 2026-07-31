using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed record QueryOp(
    int PassIndex,
    XRFrameBuffer? Target,
    VkRenderQuery Query,
    RenderQueryDescriptor Descriptor,
    ERenderQueryOperation Operation,
    FrameOpContext Context,
    PipelineStageFlags2 TimestampStage = PipelineStageFlags2.AllCommandsBit,
    uint PointIndex = 0u,
    ReadOnlyMemory<ulong> SourceHandles = default,
    Silk.NET.Vulkan.Buffer ResultDestination = default,
    ulong ResultDestinationOffset = 0ul,
    ulong ResultStride = 0ul,
    bool IncludeAvailability = true) 
    : FrameOp(PassIndex, Target, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.Query;
}
