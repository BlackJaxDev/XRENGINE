namespace XREngine.Rendering.Vulkan;

/// <summary>Concrete dependency responsible for a non-work interval.</summary>
public enum EVulkanFrameWaitReason
{
    None,
    FrameSlot,
    FrameLimiterSleep,
    FrameLimiterSpin,
    Snapshot,
    Completion,
    OutputImage,
    SwapchainAcquire,
    QueueGateway,
    QueueSubmitAdmission,
    NativeQueueSubmit,
    QueuePresentAdmission,
    NativeQueuePresent,
    CommandPool,
    DescriptorArena,
    SynchronizationLock,
    SubmissionStateLock,
    QueueLeaseLock,
    ResourceLifetimeLock,
    DescriptorPublicationLock,
    UploadLock,
    PipelineCompilerLock,
    Driver,
    ExternalRuntime,
}
