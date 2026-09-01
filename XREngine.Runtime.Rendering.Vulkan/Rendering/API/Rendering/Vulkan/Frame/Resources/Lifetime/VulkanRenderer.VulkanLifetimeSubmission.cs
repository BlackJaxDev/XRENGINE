namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanLifetimeSubmission(
    ulong QueueHandle,
    EVulkanLifetimeQueueDomain QueueDomain,
    ulong QueueSequence,
    ulong TimelineSemaphoreHandle,
    ulong TimelineValue,
    ulong FenceHandle,
    bool CompletionObserved = false);
