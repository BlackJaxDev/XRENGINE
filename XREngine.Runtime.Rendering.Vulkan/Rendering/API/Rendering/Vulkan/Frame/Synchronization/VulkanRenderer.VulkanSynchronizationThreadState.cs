using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Holds per-thread synchronization flags and reusable submit-info arrays.
    /// </summary>
    private sealed class VulkanSynchronizationThreadState
    {
        /// <summary>
        /// Gets or sets whether barrier recording should remove desktop-swapchain
        /// image barriers on this thread.
        /// </summary>
        public bool ExcludeDesktopSwapchainBarriers;

        /// <summary>Reusable synchronization2 wait-semaphore storage.</summary>
        public SemaphoreSubmitInfo[]? SubmitWaitInfoScratch;

        /// <summary>Reusable synchronization2 signal-semaphore storage.</summary>
        public SemaphoreSubmitInfo[]? SubmitSignalInfoScratch;

        /// <summary>Reusable synchronization2 command-buffer storage.</summary>
        public CommandBufferSubmitInfo[]? SubmitCommandBufferInfoScratch;

        /// <summary>
        /// Restores default flags and releases references to arrays retained by
        /// the current thread.
        /// </summary>
        public void Reset()
        {
            ExcludeDesktopSwapchainBarriers = false;
            SubmitWaitInfoScratch = null;
            SubmitSignalInfoScratch = null;
            SubmitCommandBufferInfoScratch = null;
        }
    }
}
