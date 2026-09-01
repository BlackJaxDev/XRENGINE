using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Defines the native <c>vkQueuePresentKHR</c> results that prove WSI accepted
/// responsibility for releasing the acquired swapchain image and its wait
/// semaphore.
/// </summary>
internal static class VulkanWsiPresentResult
{
    /// <summary>
    /// Returns whether the native result enqueued presentation-engine release
    /// work. Out-of-memory and unknown failures do not establish that proof.
    /// </summary>
    internal static bool EnqueuesPresentationRelease(Result result)
        => result is Result.Success or
            Result.SuboptimalKhr or
            Result.ErrorOutOfDateKhr or
            Result.ErrorSurfaceLostKhr or
            Result.ErrorFullScreenExclusiveModeLostExt;

    internal static bool DoesNotEnqueuePresentationRelease(
        bool dispatched,
        Result result)
        => !dispatched || !EnqueuesPresentationRelease(result);

    /// <summary>
    /// A dispatched result outside the documented enqueue set leaves WSI release
    /// indeterminate. Out-of-memory is synchronous non-enqueue failure that can be
    /// settled by explicit swapchain invalidation.
    /// </summary>
    internal static bool RequiresOutputQuarantine(bool dispatched, Result result)
        => dispatched &&
           result is not Result.ErrorOutOfHostMemory and
           not Result.ErrorOutOfDeviceMemory &&
           !EnqueuesPresentationRelease(result);
}
