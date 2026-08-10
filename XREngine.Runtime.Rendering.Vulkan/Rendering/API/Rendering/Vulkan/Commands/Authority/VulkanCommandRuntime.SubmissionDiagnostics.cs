using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    internal VulkanSubmissionDiagnosticContext CreateOpenXrSubmissionDiagnosticContext(
        ulong frameId,
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
        => VulkanSubmissionDiagnosticContextFactory.CreateOpenXr(
            frameId,
            CommandBuffers.SnapshotDirtyGeneration(),
            outputTargetName,
            submissionKind,
            frameOpKind,
            openXrImageIndex,
            frameDataSlotIndex,
            extent,
            frameOpsSignature,
            plannerRevision,
            frameOpContextId,
            resourceGeneration,
            descriptorGeneration);

    internal VulkanSubmissionDiagnosticContext CreateOpenXrBatchSubmissionDiagnosticContext(
        ulong frameId,
        string submissionKind,
        string frameOpKind,
        in OpenXrRecordedEyeCommandBuffer recorded,
        Extent2D extent)
        => VulkanSubmissionDiagnosticContextFactory.CreateOpenXrBatch(
            frameId,
            CommandBuffers.SnapshotDirtyGeneration(),
            submissionKind,
            frameOpKind,
            in recorded,
            extent);

    internal VulkanSubmissionDiagnosticContext CreateOpenXrPublishBatchSubmissionDiagnosticContext(
        ulong frameId,
        string submissionKind,
        string frameOpKind,
        in OpenXrRecordedEyeCommandBuffer recorded,
        Extent2D extent,
        string outputTargetName)
        => VulkanSubmissionDiagnosticContextFactory.CreateOpenXrPublishBatch(
            frameId,
            CommandBuffers.SnapshotDirtyGeneration(),
            submissionKind,
            frameOpKind,
            in recorded,
            extent,
            outputTargetName);
}
