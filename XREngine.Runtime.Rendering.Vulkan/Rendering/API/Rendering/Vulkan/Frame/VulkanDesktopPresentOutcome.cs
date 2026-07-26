using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free presentation classification and submitted-slot completion policy.
/// </summary>
internal readonly record struct VulkanDesktopPresentOutcome(
    Result Result,
    EVulkanDesktopPolicyFlow Flow,
    EVulkanDesktopPolicyReason Reason,
    EVulkanDesktopRecoveryDirective RecoveryDirective,
    bool PresentationAccepted,
    bool AdvanceFrameSlot);
