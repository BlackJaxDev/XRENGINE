using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed record BufferCopyOp(
    int PassIndex,
    VkDataBuffer SourceOwner,
    Buffer SourceBuffer,
    ulong SourceOffset,
    VkDataBuffer DestinationOwner,
    Buffer DestinationBuffer,
    ulong DestinationOffset,
    ulong ByteCount,
    bool RequireGpuWriteVisibility,
    GpuDiagnosticSnapshotReceipt? DiagnosticReceipt,
    string Label,
    FrameOpContext Context) 
    : FrameOp(PassIndex, null, Context)
{
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.BufferCopy;

}
