using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>Optional targeted scope for benchmark components without aggregate telemetry.</summary>
public readonly ref struct VulkanCpuSpanScope
{
    private readonly VulkanCpuSpanProfiler.VulkanCpuSpanToken _token;

    public VulkanCpuSpanScope(EVulkanCpuStage stage)
    {
        _token = VulkanCpuSpanProfiler.IsStageCaptureEnabled(stage)
            ? VulkanCpuSpanProfiler.Begin(stage, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread())
            : default;
    }

    public void Dispose()
    {
        if (_token.Buffer is not null)
            VulkanCpuSpanProfiler.End(_token, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
    }
}
