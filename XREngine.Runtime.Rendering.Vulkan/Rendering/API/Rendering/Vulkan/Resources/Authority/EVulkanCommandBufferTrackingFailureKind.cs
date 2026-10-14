namespace XREngine.Rendering.Vulkan;

/// <summary>Identifies why a recorded resource dependency cannot be published.</summary>
internal enum EVulkanCommandBufferTrackingFailureKind : byte
{
    None,
    QueuedForSubmission,
    RetiredDependency,
}
