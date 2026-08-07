namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Mutable debounce and resize-settle state for desktop swapchain recreation.
/// The policy remains renderer-executed because swapchain recreation and
/// framebuffer invalidation are still renderer-owned in this cut.
/// </summary>
internal sealed class VulkanDesktopSwapchainPolicyState
{
    internal bool FrameBufferInvalidated;
    internal long RecreateRequestedAt;
    internal long ResizeLastChangedAt;
    internal uint PendingSurfaceWidth;
    internal uint PendingSurfaceHeight;
    internal long LastInteractiveRecreateTimestamp;

    internal void ResetAfterRecreate()
    {
        FrameBufferInvalidated = false;
        RecreateRequestedAt = 0;
        ResizeLastChangedAt = 0;
        PendingSurfaceWidth = 0;
        PendingSurfaceHeight = 0;
    }
}
