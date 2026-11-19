namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanLifetimeSubmission(
    ulong QueueHandle,
    EVulkanLifetimeQueueDomain QueueDomain,
    ulong QueueSequence,
    ulong TimelineSemaphoreHandle,
    ulong TimelineValue,
    ulong FenceHandle,
    bool CompletionObserved = false,
    // Diagnostics only: what submitted the work (VulkanLifetimeSubmissionKinds id) and when
    // (Stopwatch timestamp). An id keeps the record unmanaged for stack buffers.
    ushort KindId = 0,
    long SubmitTimestamp = 0L);
