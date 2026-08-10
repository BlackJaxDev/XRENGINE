namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Output policy flattened at the renderer composition boundary for one
/// physical-device admission attempt.
/// </summary>
internal readonly record struct VulkanOutputDeviceRequirements(
    bool RequirePresentQueue,
    bool RequireSwapchainOutput)
{
    public void Validate()
    {
        if (RequireSwapchainOutput && !RequirePresentQueue)
            throw new InvalidOperationException("A Vulkan swapchain output requires a present queue.");
    }
}
