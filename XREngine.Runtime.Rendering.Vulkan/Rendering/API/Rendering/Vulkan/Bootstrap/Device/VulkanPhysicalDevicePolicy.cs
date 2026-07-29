namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Applies engine selection policy to queried physical-device capabilities.
/// Discovery remains in <see cref="VulkanDeviceCapabilityQuery"/>.
/// </summary>
internal static class VulkanPhysicalDevicePolicy
{
    public static bool SupportsRequiredExtensions(
        VulkanDeviceExtensionSet availableExtensions,
        ReadOnlySpan<string> engineRequiredExtensions,
        ReadOnlySpan<string> integrationRequiredExtensions)
    {
        for (int i = 0; i < engineRequiredExtensions.Length; i++)
            if (!availableExtensions.Contains(engineRequiredExtensions[i]))
                return false;

        for (int i = 0; i < integrationRequiredExtensions.Length; i++)
            if (!availableExtensions.Contains(integrationRequiredExtensions[i]))
                return false;

        return true;
    }

    public static bool IsSwapchainAdequate(int formatCount, int presentModeCount)
        => formatCount > 0 && presentModeCount > 0;

    public static bool SupportsRayTracing(VulkanDeviceExtensionSet availableExtensions)
    {
        bool hasKhrRayTracing =
            availableExtensions.Contains("VK_KHR_ray_tracing_pipeline") &&
            availableExtensions.Contains("VK_KHR_acceleration_structure") &&
            availableExtensions.Contains("VK_KHR_deferred_host_operations");
        return hasKhrRayTracing || availableExtensions.Contains("VK_NV_ray_tracing");
    }
}
