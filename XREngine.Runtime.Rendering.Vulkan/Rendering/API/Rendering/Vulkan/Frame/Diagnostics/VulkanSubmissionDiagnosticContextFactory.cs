using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Builds immutable submission diagnostics from already captured frame facts.
/// The factory deliberately owns no renderer or authority reference.
/// </summary>
internal static class VulkanSubmissionDiagnosticContextFactory
{
    internal static VulkanSubmissionDiagnosticContext CreateOpenXr(
        ulong frameId,
        long commandBufferDirtyGeneration,
        string outputTargetName,
        string submissionKind,
        string frameOpKind,
        uint openXrImageIndex,
        uint frameDataSlotIndex,
        Extent2D extent,
        ulong frameOpsSignature,
        ulong plannerRevision,
        ulong frameOpContextId,
        ulong resourceGeneration,
        ulong descriptorGeneration)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = frameOpKind,
            OutputTargetName = outputTargetName,
            OutputWidth = extent.Width,
            OutputHeight = extent.Height,
            InternalWidth = extent.Width,
            InternalHeight = extent.Height,
            FrameId = frameId,
            FrameSlot = unchecked((int)Math.Min(frameDataSlotIndex, int.MaxValue)),
            SwapchainImageIndex = openXrImageIndex,
            CommandBufferDirtyGeneration = commandBufferDirtyGeneration,
            FrameOpsSignature = frameOpsSignature,
            PlannerRevision = plannerRevision,
            FrameOpContextId = frameOpContextId,
            ResourceGeneration = resourceGeneration,
            DescriptorGeneration = descriptorGeneration,
        };

    internal static VulkanSubmissionDiagnosticContext CreateOpenXrBatch(
        ulong frameId,
        long commandBufferDirtyGeneration,
        string submissionKind,
        string frameOpKind,
        in OpenXrRecordedEyeCommandBuffer recorded,
        Extent2D extent)
        => new()
        {
            SubmissionKind = submissionKind,
            FrameOpKind = frameOpKind,
            OutputTargetName = "OpenXRBatch",
            OutputWidth = extent.Width,
            OutputHeight = extent.Height,
            InternalWidth = extent.Width,
            InternalHeight = extent.Height,
            FrameId = frameId,
            FrameSlot = unchecked((int)Math.Min(recorded.FrameDataSlotIndex, int.MaxValue)),
            SwapchainImageIndex = recorded.OpenXrImageIndex,
            CommandBufferDirtyGeneration = commandBufferDirtyGeneration,
            FrameOpsSignature = recorded.FrameOpsSignature,
            PlannerRevision = recorded.PlannerRevision,
            FrameOpContextId = recorded.FrameOpContextId,
            ResourceGeneration = recorded.ResourceGeneration,
            DescriptorGeneration = recorded.DescriptorGeneration,
        };

    internal static VulkanSubmissionDiagnosticContext CreateOpenXrPublishBatch(
        ulong frameId,
        long commandBufferDirtyGeneration,
        string submissionKind,
        string frameOpKind,
        in OpenXrRecordedEyeCommandBuffer recorded,
        Extent2D extent,
        string outputTargetName)
        => CreateOpenXrBatch(
            frameId,
            commandBufferDirtyGeneration,
            submissionKind,
            frameOpKind,
            in recorded,
            extent) with
        {
            OutputTargetName = outputTargetName,
        };
}
