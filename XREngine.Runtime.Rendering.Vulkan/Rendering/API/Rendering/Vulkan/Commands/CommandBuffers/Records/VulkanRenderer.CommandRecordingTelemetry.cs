using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly ref struct VulkanCpuStageScope
    {
        private readonly EVulkanCpuStage _stage;
        private readonly long _startTimestamp;
        private readonly long _startAllocatedBytes;
        private readonly VulkanCpuSpanProfiler.VulkanCpuSpanToken _spanToken;

        public VulkanCpuStageScope(EVulkanCpuStage stage)
        {
            _stage = stage;
            _startTimestamp = Stopwatch.GetTimestamp();
            _startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            _spanToken = VulkanCpuSpanProfiler.Begin(stage, _startTimestamp, _startAllocatedBytes);
        }

        public void Dispose()
        {
            long endAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            long endTimestamp = Stopwatch.GetTimestamp();
            VulkanCpuSpanProfiler.End(_spanToken, endTimestamp, endAllocatedBytes);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCpuStage(
                _stage,
                Stopwatch.GetElapsedTime(_startTimestamp, endTimestamp),
                Math.Max(0, endAllocatedBytes - _startAllocatedBytes));
        }
    }
}
