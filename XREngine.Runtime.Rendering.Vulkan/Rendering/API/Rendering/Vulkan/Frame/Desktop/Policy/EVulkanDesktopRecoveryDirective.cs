namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies the GPU ownership or renderer recovery work required by a policy outcome.
/// </summary>
internal enum EVulkanDesktopRecoveryDirective
{
    None,
    ResolveAcquiredWork,
    ResolveAcquiredWorkThenRecreateSwapchain,
    RecreateSwapchain,
    RestartRenderer,
    TerminalDeviceLoss,
}
