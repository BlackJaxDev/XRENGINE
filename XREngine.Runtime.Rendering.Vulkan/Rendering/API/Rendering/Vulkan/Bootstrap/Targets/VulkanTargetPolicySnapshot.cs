namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable target-policy facts consumed outside the frame-loop authority.
/// The snapshot deliberately carries no target driver or native lifecycle callback.
/// </summary>
internal sealed record VulkanTargetPolicySnapshot(
    RenderExecutionMode ExecutionMode,
    string DriverName,
    bool RequiresPresentQueue,
    bool RequiresSwapchainOutput,
    bool SupportsStreamlinePresentation,
    bool HasExplicitFrameTarget,
    IReadOnlyList<string> RequiredDeviceExtensions)
{
    internal static VulkanTargetPolicySnapshot Capture(IVulkanRendererTargetDriver driver)
        => new(
            driver.ExecutionMode,
            driver.GetType().Name,
            driver.RequiresPresentQueue,
            driver.RequiresSwapchainOutput,
            driver.SupportsStreamlinePresentation,
            driver is IVulkanExplicitFrameTargetDriver,
            [.. driver.RequiredDeviceExtensions]);
}
