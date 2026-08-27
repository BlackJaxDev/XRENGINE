using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen command-side input for one desktop primary attempt. The frame loop
/// resolves the output target, image layout, presentation source, and planner
/// stamp before constructing this value.
/// </summary>
internal readonly record struct VulkanPreparedPrimaryCommandInput(
    uint ImageIndex,
    CommandBuffer PrimaryCommandBuffer,
    CommandBuffer DynamicUiSecondaryCommandBuffer,
    FramePlan FramePlan,
    VulkanPrimaryCommandPlan PrimaryCommandPlan,
    SwapchainRecordingTarget RecordingTarget,
    VulkanPresentationSourceTuple PresentationSource,
    VulkanPreparedResourcePlanStamp ResourcePlanStamp,
    VulkanCommandClearStateSnapshot ClearState,
    VulkanCommandRecordingPolicySnapshot Policy,
    ImageLayout TrackedTargetLayout,
    uint? FrameDataImageIndexOverride = null,
    OpenXrEyeRenderTargetContext? OpenXrTargetContext = null,
    CommandChainSchedule? CommandChainSchedule = null,
    bool ExcludeDesktopSwapchainBarriers = false,
    FrameOperationStream? LogicalViewOperationsOverride = null,
    ulong LogicalViewId = 0,
    ulong? RecordingStaticOperationSignatureOverride = null,
    bool CallerOwnsSubmissionMarkersUntilRecordingSucceeds = false)
{
    /// <summary>
    /// Structural identity of the exact static stream submitted to this
    /// recording attempt. A logical-view recording is a slice of its shared
    /// frame plan and therefore cannot use the combined plan signature.
    /// </summary>
    internal ulong RecordingStaticOperationSignature =>
        RecordingStaticOperationSignatureOverride ??
        FramePlan.StaticOperationSignature;
}
