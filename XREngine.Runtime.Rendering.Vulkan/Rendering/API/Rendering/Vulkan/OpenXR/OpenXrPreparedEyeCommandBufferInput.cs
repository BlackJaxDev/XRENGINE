namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable, command-worker-safe authority captured after main-thread OpenXR
/// preparation. It intentionally excludes frame-op producers and renderer state.
/// </summary>
internal readonly record struct OpenXrPreparedEyeCommandBufferInput(
    VulkanOpenXrFrameContext FrameContext,
    OpenXrEyeRenderTargetContext TargetContext,
    FrameOp[] Ops,
    FrameOpContext PlannerContext,
    ResourcePlannerRuntimeState PlannerState,
    VulkanPreparedResourcePlanStamp ResourcePlanStamp,
    ulong FrameOpsSignature,
    ulong PlannerRevision,
    FramePlan? PairedLogicalPlan = null);
