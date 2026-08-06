using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free CPU stage scope that records into <see cref="VulkanFrameTelemetry"/>.</summary>
internal readonly ref struct VulkanCpuStageScope
{
    private readonly EVulkanCpuStage _stage;
    private readonly VulkanFrameTelemetry _telemetry;
    private readonly long _startTimestamp;
    private readonly long _startAllocatedBytes;
    private readonly long _beginBoundaryAllocatedBytes;
    private readonly VulkanCpuSpanProfiler.VulkanCpuSpanToken _spanToken;

    public VulkanCpuStageScope(VulkanFrameTelemetry telemetry, EVulkanCpuStage stage)
    {
        _telemetry = telemetry;
        _stage = stage;
        _startTimestamp = Stopwatch.GetTimestamp();
        long beforeBeginAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _spanToken = VulkanCpuSpanProfiler.Begin(stage, _startTimestamp, beforeBeginAllocatedBytes);
        _startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _beginBoundaryAllocatedBytes = Math.Max(0, _startAllocatedBytes - beforeBeginAllocatedBytes);
    }

    public void Dispose()
    {
        long endAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        long endTimestamp = Stopwatch.GetTimestamp();
        VulkanCpuSpanProfiler.End(_spanToken, endTimestamp, endAllocatedBytes);
        long afterBoundaryAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        _telemetry.RecordCpuStage(
            _stage,
            Stopwatch.GetElapsedTime(_startTimestamp, endTimestamp),
            Math.Max(0, endAllocatedBytes - _startAllocatedBytes),
            _beginBoundaryAllocatedBytes + Math.Max(0, afterBoundaryAllocatedBytes - endAllocatedBytes));
    }
}
