using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly ref struct VulkanCpuStageScope
    {
        private readonly EVulkanCpuStage _stage;
        private readonly long _startTimestamp;
        private readonly long _startAllocatedBytes;

        public VulkanCpuStageScope(EVulkanCpuStage stage)
        {
            _stage = stage;
            _startTimestamp = Stopwatch.GetTimestamp();
            _startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            long endAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCpuStage(
                _stage,
                Stopwatch.GetElapsedTime(_startTimestamp),
                Math.Max(0, endAllocatedBytes - _startAllocatedBytes));
        }
    }
}
