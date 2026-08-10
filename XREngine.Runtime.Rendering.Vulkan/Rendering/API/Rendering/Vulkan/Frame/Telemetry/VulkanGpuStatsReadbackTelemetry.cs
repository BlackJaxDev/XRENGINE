namespace XREngine.Rendering.Vulkan;

/// <summary>Renderer-free value conversion used by delayed GPU statistics telemetry.</summary>
internal static class VulkanGpuStatsReadbackTelemetry
{
    internal static int SaturateToInt(ulong value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    internal static int SaturateToInt(uint value)
        => value > int.MaxValue ? int.MaxValue : (int)value;
}
