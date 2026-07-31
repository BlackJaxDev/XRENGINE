namespace XREngine.Rendering.Vulkan;

/// <summary>
/// A submitted release barrier awaiting its paired acquire barrier.
/// </summary>
internal readonly record struct VulkanPendingQueueOwnershipRelease(
    VulkanQueueOwnershipTransferRequirement Requirement,
    VulkanLifetimeSubmission Submission);
