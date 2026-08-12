using System;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        private static bool IsTransientResourceRetirementRecordingFailure(InvalidOperationException exception)
            => IsTransientResourceRetirementRecordingFailure(exception.Message);

        private static bool IsTransientResourceRetirementRecordingFailure(string failureReason)
            => failureReason.Contains(
                "attempted to record retired Vulkan resource",
                StringComparison.Ordinal);

        private static bool IsSwapchainResourceRetirementRecordingFailure(string failureReason)
            => IsTransientResourceRetirementRecordingFailure(failureReason) &&
               (failureReason.Contains("Swapchain.Color", StringComparison.Ordinal) ||
                failureReason.Contains("Swapchain.Depth", StringComparison.Ordinal));
    }
}
