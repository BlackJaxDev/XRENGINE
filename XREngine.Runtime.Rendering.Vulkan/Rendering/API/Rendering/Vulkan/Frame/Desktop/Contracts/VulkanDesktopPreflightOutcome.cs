namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free result of desktop surface and resource preflight policy.
/// </summary>
internal readonly record struct VulkanDesktopPreflightOutcome(
    EVulkanDesktopPolicyFlow Flow,
    EVulkanDesktopPolicyReason Reason,
    EVulkanDesktopRecoveryDirective RecoveryDirective)
{
    public bool CanAcquire => Flow == EVulkanDesktopPolicyFlow.Continue;
}
