namespace XREngine;

/// <summary>
/// Identifies the first correctness-relevant image-entry field that prevented
/// reuse of a recorded Vulkan primary command buffer.
/// </summary>
public enum EVulkanPrimaryEntryStateMismatch
{
    None,
    MissingCommandBufferState,
    IncompleteSnapshot,
    MissingSubmittedState,
    UnknownExpectedLayout,
    UnknownActualLayout,
    Layout,
    StageMask,
    AccessMask,
    DescriptorLayout,
    ResourceGeneration,
    QueueFamily,
    ExternalOwnership,
}
