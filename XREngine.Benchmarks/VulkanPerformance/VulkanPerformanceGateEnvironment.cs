namespace XREngine.Benchmarks;

/// <summary>
/// Owner-selected machine and display identity for the primary Gate lane.
/// </summary>
public sealed class VulkanPerformanceGateEnvironment
{
    public string GpuName { get; init; } = string.Empty;
    public string GpuDriver { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string DisplayMode { get; init; } = string.Empty;
}
