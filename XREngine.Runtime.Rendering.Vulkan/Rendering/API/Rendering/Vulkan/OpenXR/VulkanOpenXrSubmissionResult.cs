namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit outcome of the synchronous OpenXR command submission and settlement
/// transaction.
/// </summary>
internal readonly record struct VulkanOpenXrSubmissionResult(
    bool Succeeded,
    bool CommandBuffersCompleted,
    EVulkanQueueSubmissionDisposition SubmissionDisposition,
    EOpenXrStrictSpsFaultInjectionStage InjectedFailureStage,
    VulkanSubmissionReceipt SubmissionReceipt);
