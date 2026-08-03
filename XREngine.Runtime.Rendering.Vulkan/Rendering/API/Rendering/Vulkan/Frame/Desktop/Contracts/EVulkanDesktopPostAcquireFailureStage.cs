namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies where a failure occurred after a swapchain image was acquired.
/// </summary>
internal enum EVulkanDesktopPostAcquireFailureStage
{
    ImagePreparation,
    Recording,
    Submission,
    PostSubmitAuxiliary,
    PostPresentAuxiliary,
}
