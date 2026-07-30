namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanLifetimeSubmission(
    ulong QueueHandle,
    VulkanRenderer.EVulkanLifetimeQueueDomain QueueDomain,
    ulong QueueSequence,
    ulong TimelineSemaphoreHandle,
    ulong TimelineValue,
    ulong FenceHandle);
