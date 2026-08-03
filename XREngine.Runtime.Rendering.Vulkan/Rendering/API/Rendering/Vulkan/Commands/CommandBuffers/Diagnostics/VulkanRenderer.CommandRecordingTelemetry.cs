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
        private readonly long _beginBoundaryAllocatedBytes;
        private readonly VulkanCpuSpanProfiler.VulkanCpuSpanToken _spanToken;

        public VulkanCpuStageScope(EVulkanCpuStage stage)
        {
            _stage = stage;
            _startTimestamp = Stopwatch.GetTimestamp();
            long beforeBeginAllocatedBytes =
                GC.GetAllocatedBytesForCurrentThread();
            _spanToken = VulkanCpuSpanProfiler.Begin(
                stage,
                _startTimestamp,
                beforeBeginAllocatedBytes);
            _startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            _beginBoundaryAllocatedBytes = Math.Max(
                0,
                _startAllocatedBytes - beforeBeginAllocatedBytes);
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
            long afterBoundaryAllocatedBytes =
                GC.GetAllocatedBytesForCurrentThread();
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanCpuStageBoundaryAllocation(
                    _stage,
                    _beginBoundaryAllocatedBytes + Math.Max(
                        0,
                        afterBoundaryAllocatedBytes - endAllocatedBytes));
        }
    }
}
