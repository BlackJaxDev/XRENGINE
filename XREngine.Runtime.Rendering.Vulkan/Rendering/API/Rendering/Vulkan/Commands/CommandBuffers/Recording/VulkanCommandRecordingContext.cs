using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

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
    FrameOp[] operations,
    int dynamicUiOperationCount,
    CommandChainSchedule? commandChainSchedule,
    bool preserveSwapchainForOverlay,
    bool transitionSwapchainToPresent,
    VulkanPrimaryCommandPlan primaryCommandPlan,
    uint? frameDataImageIndexOverride,
    VulkanRenderer.OpenXrEyeRenderTargetContext? openXrTargetContext,
    bool excludeDesktopSwapchainBarriers,
    VulkanRenderGraphPlan renderGraphPlan,
    FramePlan? framePlan)
{
    public readonly uint ImageIndex = imageIndex;
    public readonly CommandBuffer CommandBuffer = commandBuffer;
    public readonly CommandBuffer DynamicUiSecondaryCommandBuffer = dynamicUiSecondaryCommandBuffer;
    public readonly FrameOp[] Operations = operations;
    public readonly int DynamicUiOperationCount = dynamicUiOperationCount;
    public readonly CommandChainSchedule? CommandChainSchedule = commandChainSchedule;
    public readonly bool PreserveSwapchainForOverlay = preserveSwapchainForOverlay;
    public readonly bool TransitionSwapchainToPresent = transitionSwapchainToPresent;
    public readonly VulkanPrimaryCommandPlan PrimaryCommandPlan = primaryCommandPlan;
    public readonly uint? FrameDataImageIndexOverride = frameDataImageIndexOverride;
    public readonly VulkanRenderer.OpenXrEyeRenderTargetContext? OpenXrTargetContext = openXrTargetContext;
    public readonly bool ExcludeDesktopSwapchainBarriers = excludeDesktopSwapchainBarriers;
    public readonly VulkanRenderGraphPlan RenderGraphPlan = renderGraphPlan;
    public readonly FramePlan? FramePlan = framePlan;

    public int RecordedSwapchainWriteCount = 0;
    public ImageLayout RecordedSwapchainFinalLayout = ImageLayout.Undefined;
    public string RecordingDeferredReason = string.Empty;
    public bool QueryFrameOpsRequireRerecord = false;
}
