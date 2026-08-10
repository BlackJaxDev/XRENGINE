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
    FrameOp[]? NativeOperationsOverride = null,
    ulong LogicalViewId = 0,
    FrameOp[]? DynamicUiOperations = null,
    FrameOp[]? TextureUploadOperations = null);
