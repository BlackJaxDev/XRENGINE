using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly ref struct VulkanCpuStageScope
    {
        [ThreadStatic]
        private static long s_telemetryAllocatedBytes;

        private readonly EVulkanCpuStage _stage;
        private readonly long _startTimestamp;
        private readonly long _startAllocatedBytes;
        private readonly long _startTelemetryAllocatedBytes;

        public VulkanCpuStageScope(EVulkanCpuStage stage)
        {
            _stage = stage;
            _startTimestamp = Stopwatch.GetTimestamp();
            _startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            _startTelemetryAllocatedBytes = s_telemetryAllocatedBytes;
        }

        public void Dispose()
        {
            long endAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            long nestedTelemetryAllocatedBytes = s_telemetryAllocatedBytes - _startTelemetryAllocatedBytes;
            long measuredAllocatedBytes = endAllocatedBytes - _startAllocatedBytes - nestedTelemetryAllocatedBytes;
            long telemetryStartAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCpuStage(
                _stage,
                Stopwatch.GetElapsedTime(_startTimestamp),
                Math.Max(0, measuredAllocatedBytes));
            s_telemetryAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - telemetryStartAllocatedBytes;
        }
    }
}
