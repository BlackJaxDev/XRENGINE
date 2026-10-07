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
    public VkDataBuffer SourceOwner { get; private set; } = SourceOwner;
    public Buffer SourceBuffer { get; private set; } = SourceBuffer;
    public ulong SourceOffset { get; private set; } = SourceOffset;
    public VkDataBuffer DestinationOwner { get; private set; } = DestinationOwner;
    public Buffer DestinationBuffer { get; private set; } = DestinationBuffer;
    public ulong DestinationOffset { get; private set; } = DestinationOffset;
    public ulong ByteCount { get; private set; } = ByteCount;
    public bool RequireGpuWriteVisibility { get; private set; } = RequireGpuWriteVisibility;
    public GpuDiagnosticSnapshotReceipt? DiagnosticReceipt { get; private set; } = DiagnosticReceipt;
    public string Label { get; private set; } = Label;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.BufferCopy;

    internal static BufferCopyOp Rent(
        int passIndex,
        VkDataBuffer sourceOwner,
        Buffer sourceBuffer,
        ulong sourceOffset,
        VkDataBuffer destinationOwner,
        Buffer destinationBuffer,
        ulong destinationOffset,
        ulong byteCount,
        bool requireGpuWriteVisibility,
        GpuDiagnosticSnapshotReceipt? diagnosticReceipt,
        string label,
        in FrameOpContext context)
    {
        bool frameOwned = TryRentForCurrentFrame(context, out BufferCopyOp? reusable);
        if (reusable is null)
        {
            BufferCopyOp created = new(
                passIndex,
                sourceOwner,
                sourceBuffer,
                sourceOffset,
                destinationOwner,
                destinationBuffer,
                destinationOffset,
                byteCount,
                requireGpuWriteVisibility,
                diagnosticReceipt,
                label,
                context);
            return frameOwned ? RetainForCurrentFrame(created, context) : created;
        }

        reusable.PassIndex = passIndex;
        reusable.Target = null;
        reusable.SourceOwner = sourceOwner;
        reusable.SourceBuffer = sourceBuffer;
        reusable.SourceOffset = sourceOffset;
        reusable.DestinationOwner = destinationOwner;
        reusable.DestinationBuffer = destinationBuffer;
        reusable.DestinationOffset = destinationOffset;
        reusable.ByteCount = byteCount;
        reusable.RequireGpuWriteVisibility = requireGpuWriteVisibility;
        reusable.DiagnosticReceipt = diagnosticReceipt;
        reusable.Label = label;
        reusable.Context = context;
        return reusable;
    }
}
