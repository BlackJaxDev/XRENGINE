namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks exactly-once ownership of an optional desktop texture-upload command buffer.
/// </summary>
internal enum EVulkanDesktopUploadOwnership
{
    None,
    Recorded,
    SubmittedDeferredFree,
    Retired,
    CancelledFreed,
    AbandonedAfterDeviceLoss,
}
