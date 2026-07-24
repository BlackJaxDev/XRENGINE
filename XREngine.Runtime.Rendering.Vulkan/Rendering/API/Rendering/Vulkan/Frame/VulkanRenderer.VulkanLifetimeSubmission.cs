namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanLifetimeSubmission(
        ulong QueueHandle,
        EVulkanLifetimeQueueDomain QueueDomain,
        ulong QueueSequence,
        ulong TimelineSemaphoreHandle,
        ulong TimelineValue,
        ulong FenceHandle);
}
