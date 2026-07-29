namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable authority for the capabilities that survived device enablement and
/// extension-command loading. Advertised, required, and enabled extensions remain
/// separate so diagnostics cannot confuse availability with active runtime support.
/// </summary>
internal sealed class VulkanDeviceCapabilities
{
    public static VulkanDeviceCapabilities Empty { get; } = new(
        VulkanDeviceExtensionSet.Empty,
        VulkanDeviceExtensionSet.Empty,
        VulkanDeviceExtensionSet.Empty,
        EVulkanDeviceCapability.None,
        EVulkanDeviceFallback.None);

    public VulkanDeviceCapabilities(
        VulkanDeviceExtensionSet availableExtensions,
        VulkanDeviceExtensionSet requiredExtensions,
        VulkanDeviceExtensionSet enabledExtensions,
        EVulkanDeviceCapability enabledCapabilities,
        EVulkanDeviceFallback activeFallbacks)
    {
        AvailableExtensions = availableExtensions;
        RequiredExtensions = requiredExtensions;
        EnabledExtensions = enabledExtensions;
        EnabledCapabilities = enabledCapabilities;
        ActiveFallbacks = activeFallbacks;
    }

    public VulkanDeviceExtensionSet AvailableExtensions { get; }
    public VulkanDeviceExtensionSet RequiredExtensions { get; }
    public VulkanDeviceExtensionSet EnabledExtensions { get; }
    public EVulkanDeviceCapability EnabledCapabilities { get; }
    public EVulkanDeviceFallback ActiveFallbacks { get; }
    public bool IsInitialized => EnabledExtensions.Count != 0 || AvailableExtensions.Count != 0;

    public bool Supports(EVulkanDeviceCapability capability)
        => (EnabledCapabilities & capability) == capability;

    public bool UsesFallback(EVulkanDeviceFallback fallback)
        => (ActiveFallbacks & fallback) == fallback;
}
