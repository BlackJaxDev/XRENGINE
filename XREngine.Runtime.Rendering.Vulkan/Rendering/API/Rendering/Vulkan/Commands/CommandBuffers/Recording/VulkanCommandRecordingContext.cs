using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal enum EVulkanCommandRecordingFailureKind : byte
{
    None,
    Deferred,
    ReplanRequired,
}

/// <summary>
/// Stack-only capture of all frame-local inputs and outputs for one primary
/// command-buffer recording attempt.
/// ref struct is used to ensure that this context is not accidentally captured
/// by a lambda or async method, which would extend its lifetime beyond the intended scope.
/// </summary>
internal ref struct VulkanCommandRecordingContext(
    uint imageIndex,
    CommandBuffer commandBuffer,
    CommandBuffer dynamicUiSecondaryCommandBuffer,
    FrameOperationSequence operations,
    int dynamicUiOperationCount,
    CommandChainSchedule? commandChainSchedule,
    bool preserveSwapchainForOverlay,
    bool transitionSwapchainToPresent,
    VulkanPrimaryCommandPlan primaryCommandPlan,
    uint? frameDataImageIndexOverride,
    OpenXrEyeRenderTargetContext? openXrTargetContext,
    bool excludeDesktopSwapchainBarriers,
    VulkanRenderGraphPlan renderGraphPlan,
    FramePlan? framePlan,
    SwapchainRecordingTarget recordingTarget = default,
    VulkanPresentationSourceTuple presentationSource = default,
    VulkanCommandRecordingPolicySnapshot policy = default,
    VulkanPreparedResourcePlanStamp resourcePlanStamp = default,
    VulkanCommandClearStateSnapshot clearState = default)
{
    public readonly uint ImageIndex = imageIndex;
    public readonly CommandBuffer CommandBuffer = commandBuffer;
    public readonly CommandBuffer DynamicUiSecondaryCommandBuffer = dynamicUiSecondaryCommandBuffer;
    public readonly FrameOperationSequence Operations = operations;
    public readonly int DynamicUiOperationCount = dynamicUiOperationCount;
    public readonly CommandChainSchedule? CommandChainSchedule = commandChainSchedule;
    public readonly bool PreserveSwapchainForOverlay = preserveSwapchainForOverlay;
    public readonly bool TransitionSwapchainToPresent = transitionSwapchainToPresent;
    public readonly VulkanPrimaryCommandPlan PrimaryCommandPlan = primaryCommandPlan;
    public readonly uint? FrameDataImageIndexOverride = frameDataImageIndexOverride;
    public readonly OpenXrEyeRenderTargetContext? OpenXrTargetContext = openXrTargetContext;
    public readonly bool ExcludeDesktopSwapchainBarriers = excludeDesktopSwapchainBarriers;
    public readonly VulkanRenderGraphPlan RenderGraphPlan = renderGraphPlan;
    public readonly FramePlan? FramePlan = framePlan;
    public readonly SwapchainRecordingTarget RecordingTarget = recordingTarget;
    public readonly VulkanPresentationSourceTuple PresentationSource = presentationSource;
    public readonly VulkanCommandRecordingPolicySnapshot Policy = policy;
    public readonly VulkanPreparedResourcePlanStamp ResourcePlanStamp = resourcePlanStamp;
    public readonly VulkanCommandClearStateSnapshot ClearState = clearState;

    public int RecordedSwapchainWriteCount = 0;
    public ImageLayout RecordedSwapchainFinalLayout = ImageLayout.Undefined;
    public string RecordingDeferredReason = string.Empty;
    public EVulkanCommandRecordingFailureKind FailureKind = EVulkanCommandRecordingFailureKind.None;
    /// <summary>
    /// True when this recording intentionally omitted transient work and therefore
    /// must not be published as a reusable primary artifact.
    /// </summary>
    public bool FrameOpsRequireRerecord = false;
}
