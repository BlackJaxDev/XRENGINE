namespace XREngine.Rendering.Vulkan;

internal static class VulkanRecordingFailurePolicy
{
    internal static bool IsTransientResourceRetirement(
        InvalidOperationException exception)
        => IsTransientResourceRetirement(exception.Message);

    internal static bool IsTransientResourceRetirement(string failureReason)
        => failureReason.Contains(
            "attempted to record retired Vulkan resource",
            StringComparison.Ordinal);

    internal static bool IsSwapchainResourceRetirement(string failureReason)
        => IsTransientResourceRetirement(failureReason) &&
           (failureReason.Contains("Swapchain.Color", StringComparison.Ordinal) ||
            failureReason.Contains("Swapchain.Depth", StringComparison.Ordinal));
}