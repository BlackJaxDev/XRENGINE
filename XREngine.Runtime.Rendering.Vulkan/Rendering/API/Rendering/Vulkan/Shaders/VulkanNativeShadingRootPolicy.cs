namespace XREngine.Rendering.Vulkan;

/// <summary>Process-stable selection keeps shader and arena capabilities in agreement.</summary>
internal static class VulkanNativeShadingRootPolicy
{
    internal const string EnvironmentVariable = XREngineEnvironmentVariables.VkNativeShadingRoot;
    internal const uint AbiVersion = 1;
    internal static EVulkanNativeShadingRootMode Requested { get; } = ReadRequestedMode();
    internal static bool UsesAddress => Requested == EVulkanNativeShadingRootMode.BufferDeviceAddress;

    private static EVulkanNativeShadingRootMode ReadRequestedMode()
    {
        string? value = XREnvironment.GetValue(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Immediate", StringComparison.OrdinalIgnoreCase))
            return EVulkanNativeShadingRootMode.Immediate;
        if (value.Equals("BufferDeviceAddress", StringComparison.OrdinalIgnoreCase))
            return EVulkanNativeShadingRootMode.BufferDeviceAddress;
        throw new NotSupportedException($"{EnvironmentVariable}='{value}' is unsupported. Select Immediate or BufferDeviceAddress before Vulkan initialization.");
    }
}
