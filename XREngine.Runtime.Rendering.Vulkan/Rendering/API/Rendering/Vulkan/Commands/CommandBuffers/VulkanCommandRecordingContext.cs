using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stack-only capture of all frame-local inputs and outputs for one primary
/// command-buffer recording attempt.
/// </summary>
internal ref struct VulkanCommandRecordingContext
{
    public VulkanCommandRecordingContext(
        uint imageIndex,
        CommandBuffer commandBuffer,
        CommandBuffer dynamicUiSecondaryCommandBuffer,
        VulkanRenderer.FrameOp[] operations,
        int dynamicUiOperationCount,
        CommandChainSchedule? commandChainSchedule,
        bool preserveSwapchainForOverlay,
        bool transitionSwapchainToPresent,
        uint? frameDataImageIndexOverride,
        VulkanRenderer.OpenXrEyeRenderTargetContext? openXrTargetContext,
        bool excludeDesktopSwapchainBarriers,
        VulkanRenderGraphPlan renderGraphPlan)
    {
        ImageIndex = imageIndex;
        CommandBuffer = commandBuffer;
        DynamicUiSecondaryCommandBuffer = dynamicUiSecondaryCommandBuffer;
        Operations = operations;
        DynamicUiOperationCount = dynamicUiOperationCount;
        CommandChainSchedule = commandChainSchedule;
        PreserveSwapchainForOverlay = preserveSwapchainForOverlay;
        TransitionSwapchainToPresent = transitionSwapchainToPresent;
        FrameDataImageIndexOverride = frameDataImageIndexOverride;
        OpenXrTargetContext = openXrTargetContext;
        ExcludeDesktopSwapchainBarriers = excludeDesktopSwapchainBarriers;
        RenderGraphPlan = renderGraphPlan;
        RecordedSwapchainWriteCount = 0;
        RecordedSwapchainFinalLayout = ImageLayout.Undefined;
        RecordingDeferredReason = string.Empty;
        QueryFrameOpsRequireRerecord = false;
    }

    public readonly uint ImageIndex;
    public readonly CommandBuffer CommandBuffer;
    public readonly CommandBuffer DynamicUiSecondaryCommandBuffer;
    public readonly VulkanRenderer.FrameOp[] Operations;
    public readonly int DynamicUiOperationCount;
    public readonly CommandChainSchedule? CommandChainSchedule;
    public readonly bool PreserveSwapchainForOverlay;
    public readonly bool TransitionSwapchainToPresent;
    public readonly uint? FrameDataImageIndexOverride;
    public readonly VulkanRenderer.OpenXrEyeRenderTargetContext? OpenXrTargetContext;
    public readonly bool ExcludeDesktopSwapchainBarriers;
    public readonly VulkanRenderGraphPlan RenderGraphPlan;

    public int RecordedSwapchainWriteCount;
    public ImageLayout RecordedSwapchainFinalLayout;
    public string RecordingDeferredReason;
    public bool QueryFrameOpsRequireRerecord;
}
