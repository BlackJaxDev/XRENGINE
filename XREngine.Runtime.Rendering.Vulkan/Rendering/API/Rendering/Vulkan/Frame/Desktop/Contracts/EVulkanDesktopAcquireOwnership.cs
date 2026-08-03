namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks the combined acquire-semaphore and swapchain-image obligation.
/// </summary>
internal enum EVulkanDesktopAcquireOwnership
{
    None,
    AcquiredUnresolved,
    ConsumedBySubmissionImagePendingPresent,
    ConsumedByRecoveryImagePendingPresent,
    ResolvedByPresentation,
    ResolvedBySwapchainInvalidation,
    IndeterminateAfterDeviceLoss,
}
