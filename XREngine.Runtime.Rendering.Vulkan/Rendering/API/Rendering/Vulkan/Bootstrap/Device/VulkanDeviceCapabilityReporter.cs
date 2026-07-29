namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reports the final immutable device snapshot after creation policy and command
/// loading have both completed.
/// </summary>
internal static class VulkanDeviceCapabilityReporter
{
    public static void LogSummary(VulkanDeviceCapabilities capabilities)
    {
        Debug.Vulkan(
            "[Vulkan.Device] Capability snapshot: advertisedExtensions={0}, requiredExtensions={1}, enabledExtensions={2}, enabledCapabilities=0x{3:X}, fallbacks={4}.",
            capabilities.AvailableExtensions.Count,
            capabilities.RequiredExtensions.Count,
            capabilities.EnabledExtensions.Count,
            (ulong)capabilities.EnabledCapabilities,
            capabilities.ActiveFallbacks);

        for (int i = 0; i < capabilities.RequiredExtensions.Count; i++)
        {
            string extension = capabilities.RequiredExtensions[i];
            Debug.Vulkan(
                "[Vulkan.Device] Required extension {0}: advertised={1}, enabled={2}.",
                extension,
                capabilities.AvailableExtensions.Contains(extension),
                capabilities.EnabledExtensions.Contains(extension));
        }
    }
}
