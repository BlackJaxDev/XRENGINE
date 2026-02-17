using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const uint FrameTimingQueryCount = 2;
    private QueryPool[]? _frameTimingQueryPools;
    private bool _frameTimingGpuEnabled;
    private double _frameTimingTimestampPeriodNanoseconds = 1.0;

    private void CreateFrameTimingResources()
    {
        DestroyFrameTimingResources();

        if (device.Handle == 0)
            return;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        _frameTimingTimestampPeriodNanoseconds = Math.Max(properties.Limits.TimestampPeriod, 0.0001f);

        _frameTimingQueryPools = new QueryPool[MAX_FRAMES_IN_FLIGHT];

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = FrameTimingQueryCount,
        };

        for (int i = 0; i < _frameTimingQueryPools.Length; i++)
        {
            if (Api.CreateQueryPool(device, ref createInfo, null, out _frameTimingQueryPools[i]) != Result.Success)
            {
                DestroyFrameTimingResources();
                _frameTimingGpuEnabled = false;
                Debug.VulkanWarning("[Vulkan] Frame timing query pool allocation failed; GPU frame timing instrumentation disabled.");
                return;
            }
        }

        _frameTimingGpuEnabled = true;
    }

    private void DestroyFrameTimingResources()
    {
        if (_frameTimingQueryPools is not null)
        {
            for (int i = 0; i < _frameTimingQueryPools.Length; i++)
            {
                QueryPool queryPool = _frameTimingQueryPools[i];
                if (queryPool.Handle != 0)
                    Api!.DestroyQueryPool(device, queryPool, null);
            }
        }

        _frameTimingQueryPools = null;
        _frameTimingGpuEnabled = false;
    }

    private void BeginFrameTimingQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!_frameTimingGpuEnabled || _frameTimingQueryPools is null ||
            frameSlot < 0 || frameSlot >= _frameTimingQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        Api!.CmdResetQueryPool(commandBuffer, queryPool, 0, FrameTimingQueryCount);
        Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, 0);
    }

    private void EndFrameTimingQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!_frameTimingGpuEnabled || _frameTimingQueryPools is null ||
            frameSlot < 0 || frameSlot >= _frameTimingQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, queryPool, 1);
    }

    private void SampleFrameTimingQueries(int frameSlot)
    {
        if (!_frameTimingGpuEnabled || _frameTimingQueryPools is null ||
            frameSlot < 0 || frameSlot >= _frameTimingQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        ulong* timestamps = stackalloc ulong[(int)FrameTimingQueryCount];
        Result result = Api!.GetQueryPoolResults(
            device,
            queryPool,
            0,
            FrameTimingQueryCount,
            (nuint)(sizeof(ulong) * FrameTimingQueryCount),
            timestamps,
            (ulong)sizeof(ulong),
            QueryResultFlags.Result64Bit);

        if (result != Result.Success)
            return;

        ulong start = timestamps[0];
        ulong end = timestamps[1];
        if (end < start)
            return;

        double gpuMilliseconds = (end - start) * _frameTimingTimestampPeriodNanoseconds / 1_000_000.0;
        Engine.Rendering.Stats.RecordVulkanFrameGpuCommandBufferTime(TimeSpan.FromMilliseconds(gpuMilliseconds));
    }
}
