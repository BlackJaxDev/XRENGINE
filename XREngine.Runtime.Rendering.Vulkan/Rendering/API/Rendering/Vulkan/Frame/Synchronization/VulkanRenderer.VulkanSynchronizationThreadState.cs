using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Holds per-thread synchronization flags and reusable submit-info arrays.
/// </summary>
internal sealed class VulkanSynchronizationThreadState
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

    /// <summary>Reusable synchronization2 memory-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<MemoryBarrier2> MemoryBarrier2Scratch = new();

    /// <summary>Reusable synchronization2 buffer-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<BufferMemoryBarrier2> BufferMemoryBarrier2Scratch = new();

    /// <summary>Reusable synchronization2 image-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<ImageMemoryBarrier2> ImageMemoryBarrier2Scratch = new();

    /// <summary>Reusable legacy image-barrier ABI storage.</summary>
    public readonly VulkanNativeScratchArena<ImageMemoryBarrier> ImageMemoryBarrierScratch = new();

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
        MemoryBarrier2Scratch.Reset();
        BufferMemoryBarrier2Scratch.Reset();
        ImageMemoryBarrier2Scratch.Reset();
        ImageMemoryBarrierScratch.Reset();
    }
}
