namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The single terminal result published by the desktop frame settlement pass.
/// </summary>
internal readonly record struct VulkanDesktopFrameTerminalResult(
    EVulkanFrameOutcome Outcome,
    EDesktopFrameReason Reason,
    VulkanDesktopFrameFailure Failure,
    bool OwnershipSettled)
{
    /// <summary>Every terminal result belongs to the settlement stage.</summary>
    public EVulkanFrameStage Stage => EVulkanFrameStage.FrameSettlement;

    /// <summary>Whether this value represents a published terminal outcome.</summary>
    public bool IsValid => Outcome != EVulkanFrameOutcome.NotReached;
}
