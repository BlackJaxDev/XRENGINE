namespace XREngine.Rendering.Vulkan;

/// <summary>Process-stable selection keeps shader and arena capabilities in agreement.</summary>
internal static class VulkanNativeShadingRootPolicy
{
    internal const string EnvironmentVariable = "XRE_VK_NATIVE_SHADING_ROOT";
    internal const uint AbiVersion = 1;
    internal static EVulkanNativeShadingRootMode Requested { get; } = ReadRequestedMode();
    internal static bool UsesAddress => Requested == EVulkanNativeShadingRootMode.BufferDeviceAddress;

    private static EVulkanNativeShadingRootMode ReadRequestedMode()
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value) || value.Equals("Immediate", StringComparison.OrdinalIgnoreCase))
            return EVulkanNativeShadingRootMode.Immediate;
        if (value.Equals("BufferDeviceAddress", StringComparison.OrdinalIgnoreCase))
            return EVulkanNativeShadingRootMode.BufferDeviceAddress;
        throw new NotSupportedException($"{EnvironmentVariable}='{value}' is unsupported. Select Immediate or BufferDeviceAddress before Vulkan initialization.");
    }
}
