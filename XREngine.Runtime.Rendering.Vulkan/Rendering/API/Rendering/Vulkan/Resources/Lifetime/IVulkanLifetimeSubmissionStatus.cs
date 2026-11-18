namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Answers whether the native work of one lifetime submission is complete, without waiting.
/// <see cref="VulkanResourceLifetimeTracker.SweepCompletions{TStatus}"/> takes it as a struct
/// type argument, so the production path queries Vulkan and a unit test supplies the answers,
/// and neither allocates.
/// </summary>
internal interface IVulkanLifetimeSubmissionStatus
{
    /// <summary>
    /// True when the submission's fence is signaled or its timeline semaphore has reached its
    /// value. False when it is still pending or the query failed.
    /// </summary>
    bool IsComplete(in VulkanLifetimeSubmission submission);
}
