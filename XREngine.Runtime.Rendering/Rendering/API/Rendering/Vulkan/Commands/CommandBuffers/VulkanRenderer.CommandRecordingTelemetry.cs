using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly struct VulkanCpuStageScope : IDisposable
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
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCpuStage(
                _stage,
                Stopwatch.GetElapsedTime(_startTimestamp),
                GC.GetAllocatedBytesForCurrentThread() - _startAllocatedBytes);
        }
    }
}
