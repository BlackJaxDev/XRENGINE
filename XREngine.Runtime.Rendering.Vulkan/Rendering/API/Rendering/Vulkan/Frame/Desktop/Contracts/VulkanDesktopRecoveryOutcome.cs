namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free ownership settlement required after a post-acquire failure.
/// </summary>
internal readonly record struct VulkanDesktopRecoveryOutcome(
    EVulkanDesktopPolicyFlow Flow,
    EVulkanDesktopPolicyReason Reason,
    EVulkanDesktopRecoveryDirective RecoveryDirective,
    EVulkanDesktopAcquireOwnership RequiredAcquireOwnership,
    EVulkanDesktopUploadOwnership RequiredUploadOwnership,
    bool MustSettlePresentation,
    bool AdvanceFrameSlotAfterSettlement);
