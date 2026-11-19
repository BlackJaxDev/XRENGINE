namespace XREngine.Rendering.Vulkan;

internal sealed record SubmissionMarkerOp(
    int PassIndex,
    VulkanTimelineGpuFence Fence,
    string Label,
    FrameOpContext Context,
    int RequiredOperationCount = 0)
    : FrameOp(PassIndex, null, Context)
{
    public VulkanTimelineGpuFence Fence { get; private set; } = Fence;
    public string Label { get; private set; } = Label;
    public int RequiredOperationCount { get; private set; } = RequiredOperationCount;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.SubmissionMarker;
    internal override bool RequiresPrimaryRecordingContext => false;

    internal static SubmissionMarkerOp Rent(
        int passIndex,
        VulkanTimelineGpuFence fence,
        string label,
        in FrameOpContext context,
        int requiredOperationCount = 0)
    {
        bool frameOwned = TryRentForCurrentFrame(context, out SubmissionMarkerOp? reusable);
        if (reusable is null)
        {
            SubmissionMarkerOp created = new(passIndex, fence, label, context, requiredOperationCount);
            return frameOwned ? RetainForCurrentFrame(created, context) : created;
        }

        reusable.PassIndex = passIndex;
        reusable.Target = null;
        reusable.Fence = fence;
        reusable.Label = label;
        reusable.Context = context;
        reusable.RequiredOperationCount = requiredOperationCount;
        return reusable;
    }
}
