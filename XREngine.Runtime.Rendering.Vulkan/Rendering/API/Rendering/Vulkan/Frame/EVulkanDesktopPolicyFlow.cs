namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Describes how the desktop Vulkan coordinator should proceed after applying a pure policy.
/// </summary>
internal enum EVulkanDesktopPolicyFlow
{
    Continue,
    Stop,
    Completed,
    Faulted,
    TerminalDeviceLoss,
}
