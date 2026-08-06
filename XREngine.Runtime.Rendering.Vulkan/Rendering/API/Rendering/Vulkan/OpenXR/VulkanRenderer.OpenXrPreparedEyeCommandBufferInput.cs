namespace XREngine.Rendering.Vulkan;

internal readonly record struct OpenXrPreparedEyeCommandBufferInput(
    OpenXrEyeSwapchainRenderRequest Request,
    VulkanOpenXrFrameContext FrameContext,
    OpenXrEyeRenderTargetContext TargetContext,
    FrameOp[] Ops,
    VulkanOpenXrFrameDataRefreshRequestLease FrameDataRefreshLease,
    FrameOpContext PlannerContext,
    ulong FrameOpsSignature,
    ulong PlannerRevision,
    CommandChainSchedule? CommandChainSchedule,
    FramePlan? PairedLogicalPlan = null);
