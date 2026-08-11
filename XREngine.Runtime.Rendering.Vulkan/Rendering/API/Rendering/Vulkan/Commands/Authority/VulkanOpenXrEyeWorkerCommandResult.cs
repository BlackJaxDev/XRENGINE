namespace XREngine.Rendering.Vulkan;

/// <summary>Outcome of the renderer-free OpenXR stereo command transaction.</summary>
internal readonly record struct VulkanOpenXrEyeWorkerCommandResult(
    OpenXrEyeRecordWorkerBatchResult Batch,
    bool Submitted,
    bool CommandBuffersCompleted);
