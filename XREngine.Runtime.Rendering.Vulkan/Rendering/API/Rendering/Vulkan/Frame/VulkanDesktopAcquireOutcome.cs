using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free acquire classification including the resulting image ownership.
/// </summary>
internal readonly record struct VulkanDesktopAcquireOutcome(
    Result Result,
    EVulkanDesktopPolicyFlow Flow,
    EVulkanDesktopPolicyReason Reason,
    EVulkanDesktopRecoveryDirective RecoveryDirective,
    EVulkanDesktopAcquireOwnership Ownership)
{
    public bool ImageAcquired
        => Ownership == EVulkanDesktopAcquireOwnership.AcquiredUnresolved;

    public bool IsTransientSkip
        => Reason is EVulkanDesktopPolicyReason.AcquireNotReady
            or EVulkanDesktopPolicyReason.AcquireTimeout;
}
