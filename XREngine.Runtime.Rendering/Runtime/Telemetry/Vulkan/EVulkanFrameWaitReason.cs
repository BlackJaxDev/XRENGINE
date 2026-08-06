namespace XREngine.Rendering.Vulkan;

/// <summary>Concrete dependency responsible for a non-work interval.</summary>
public enum EVulkanFrameWaitReason
{
    None,
    FrameSlot,
    Snapshot,
    Completion,
    OutputImage,
    QueueGateway,
    Driver,
    ExternalRuntime,
}
