namespace XREngine.Rendering.Vulkan;

/// <summary>Deferred telemetry-only services for Vulkan program wrappers.</summary>
internal sealed class VulkanProgramTelemetryPort
{
    private VulkanWrapperCpuProfilingPort? _profiling;

    internal VulkanWrapperCpuProfilingPort Profiling
        => Volatile.Read(ref _profiling) ?? throw new InvalidOperationException(
            "Vulkan program telemetry has not been published.");

    internal void Publish(VulkanFrameTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        VulkanWrapperCpuProfilingPort profiling = new(telemetry);
        VulkanWrapperCpuProfilingPort? current = Interlocked.CompareExchange(ref _profiling, profiling, null);
        if (current is not null && !ReferenceEquals(current, profiling))
            throw new InvalidOperationException("Vulkan program telemetry was already published.");
    }

    internal void RecordComputePipelineCacheMiss()
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit: false);
}
