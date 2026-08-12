using System;
using System.Buffers;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    private const uint FrameTimingQueryCount = 2;

    internal unsafe void CreateFrameTimingResources()
    {
        DestroyFrameTimingResources();

        if (_deviceContext.Device.Handle == 0)
            return;

        _deviceContext.Api.GetPhysicalDeviceProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceProperties properties);
        _telemetry._frameTimingTimestampPeriodNanoseconds = Math.Max(properties.Limits.TimestampPeriod, 0.0001f);

        int timingSlotCount = Math.Max(_outputRuntime.Desktop.Images?.Length ?? 0, FrameSlotCount);
        _telemetry._frameTimingQueryPools = new QueryPool[timingSlotCount];
        _telemetry._frameTimingQueryReady = new bool[timingSlotCount];

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = FrameTimingQueryCount,
        };

        for (int i = 0; i < _telemetry._frameTimingQueryPools.Length; i++)
        {
            if (_deviceContext.Api.CreateQueryPool(_deviceContext.Device, ref createInfo, null, out _telemetry._frameTimingQueryPools[i]) != Result.Success)
            {
                DestroyFrameTimingResources();
                _telemetry._frameTimingGpuEnabled = false;
                Debug.VulkanWarning("[Vulkan] Frame timing query pool allocation failed; GPU frame timing instrumentation disabled.");
                return;
            }
            _resourceRuntime.RegisterResource(
                ObjectType.QueryPool,
                _telemetry._frameTimingQueryPools[i].Handle,
                $"FrameTiming.QueryPool[{i}]");
        }

        _telemetry._frameTimingGpuEnabled = true;
        CreateVulkanGpuProfilerResources();
    }

    private void EnsureFrameTimingSlotCapacity(int slotCount)
    {
        if (slotCount <= 0 || _deviceContext.Device.Handle == 0)
            return;

        EnsureFrameTimingQueryPoolCapacity(slotCount);
        EnsureVulkanGpuProfilerSlotCapacity(slotCount);
    }

    private unsafe void EnsureFrameTimingQueryPoolCapacity(int slotCount)
    {
        if (!_telemetry._frameTimingGpuEnabled ||
            _telemetry._frameTimingQueryPools is null ||
            _telemetry._frameTimingQueryReady is null ||
            _telemetry._frameTimingQueryPools.Length >= slotCount)
        {
            return;
        }

        int oldLength = _telemetry._frameTimingQueryPools.Length;
        Array.Resize(ref _telemetry._frameTimingQueryPools, slotCount);
        Array.Resize(ref _telemetry._frameTimingQueryReady, slotCount);

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = FrameTimingQueryCount,
        };

        for (int i = oldLength; i < slotCount; i++)
        {
            if (_deviceContext.Api.CreateQueryPool(_deviceContext.Device, ref createInfo, null, out _telemetry._frameTimingQueryPools[i]) != Result.Success)
            {
                Debug.VulkanWarning("[Vulkan] Frame timing query pool growth failed for frame slot {0}; GPU frame timing disabled for that slot.", i);
                _telemetry._frameTimingQueryPools[i] = default;
            }
            else
            {
                _resourceRuntime.RegisterResource(
                    ObjectType.QueryPool,
                    _telemetry._frameTimingQueryPools[i].Handle,
                    $"FrameTiming.QueryPool[{i}]");
            }
        }
    }

    internal void DestroyFrameTimingResources()
    {
        DestroyVulkanGpuProfilerResources();

        if (_telemetry._frameTimingQueryPools is not null)
        {
            for (int i = 0; i < _telemetry._frameTimingQueryPools.Length; i++)
            {
                QueryPool queryPool = _telemetry._frameTimingQueryPools[i];
                if (queryPool.Handle != 0)
                    _resourceRuntime.RetireQueryPool(queryPool, "VulkanFrameLoop.FrameTiming");
            }
        }

        _telemetry._frameTimingQueryPools = null;
        _telemetry._frameTimingQueryReady = null;
        _telemetry._frameTimingGpuEnabled = false;
    }

    private void BeginFrameTimingQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!_telemetry._frameTimingGpuEnabled || _telemetry._frameTimingQueryPools is null ||
            frameSlot < 0 || frameSlot >= _telemetry._frameTimingQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _telemetry._frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        _commandRuntime.TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.QueryPool,
            queryPool.Handle,
            "FrameTiming.QueryPool");
        _deviceContext.Api.CmdResetQueryPool(commandBuffer, queryPool, 0, FrameTimingQueryCount);
        _deviceContext.Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, 0);
    }

    private void EndFrameTimingQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!_telemetry._frameTimingGpuEnabled || _telemetry._frameTimingQueryPools is null ||
            frameSlot < 0 || frameSlot >= _telemetry._frameTimingQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _telemetry._frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        _deviceContext.Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, queryPool, 1);
    }

    private unsafe void SampleFrameTimingQueries(int frameSlot)
    {
        SampleVulkanGpuProfilerQueries(frameSlot);

        if (!_telemetry._frameTimingGpuEnabled || _telemetry._frameTimingQueryPools is null ||
            _telemetry._frameTimingQueryReady is null ||
            frameSlot < 0 || frameSlot >= _telemetry._frameTimingQueryPools.Length)
        {
            return;
        }

        if (!_telemetry._frameTimingQueryReady[frameSlot])
            return;

        QueryPool queryPool = _telemetry._frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        ulong* timestamps = stackalloc ulong[(int)FrameTimingQueryCount];
        Result result = _deviceContext.Api.GetQueryPoolResults(
            _deviceContext.Device,
            queryPool,
            0,
            FrameTimingQueryCount,
            (nuint)(sizeof(ulong) * FrameTimingQueryCount),
            timestamps,
            (ulong)sizeof(ulong),
            QueryResultFlags.Result64Bit);

        if (result != Result.Success)
            return;

        _resourceRuntime.NotifyResourceUseCompleted(ObjectType.QueryPool, queryPool.Handle);

        ulong start = timestamps[0];
        ulong end = timestamps[1];
        if (end < start)
            return;

        double gpuMilliseconds = (end - start) * _telemetry._frameTimingTimestampPeriodNanoseconds / 1_000_000.0;
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameGpuCommandBufferTime(TimeSpan.FromMilliseconds(gpuMilliseconds));
    }

    private void MarkFrameTimingSubmitted(int frameSlot)
    {
        if (_telemetry._frameTimingQueryReady is null || frameSlot < 0 || frameSlot >= _telemetry._frameTimingQueryReady.Length)
            return;

        _telemetry._frameTimingQueryReady[frameSlot] = true;
        MarkVulkanGpuProfilerSubmitted(frameSlot);
    }

    private unsafe void CreateVulkanGpuProfilerResources()
    {
        DestroyVulkanGpuProfilerResources();

        if (_deviceContext.Device.Handle == 0)
            return;

        int profilerSlotCount = Math.Max(_outputRuntime.Desktop.Images?.Length ?? 0, FrameSlotCount);
        _telemetry._vulkanGpuProfilerQueryPools = new QueryPool[profilerSlotCount];
        _telemetry._vulkanGpuProfilerQueryReady = new bool[profilerSlotCount];
        _telemetry._vulkanGpuProfilerPendingScopes = new List<VulkanGpuProfilerPendingScope>[profilerSlotCount];
        _telemetry._vulkanGpuProfilerPendingQueryCounts = new int[profilerSlotCount];
        _telemetry._vulkanGpuProfilerSubmittedFrameIds = new ulong[profilerSlotCount];

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = VulkanFrameTelemetry.GpuProfilerQueryCount,
        };

        for (int i = 0; i < _telemetry._vulkanGpuProfilerQueryPools.Length; i++)
        {
            _telemetry._vulkanGpuProfilerPendingScopes[i] = [];
            if (_deviceContext.Api.CreateQueryPool(_deviceContext.Device, ref createInfo, null, out _telemetry._vulkanGpuProfilerQueryPools[i]) != Result.Success)
            {
                DestroyVulkanGpuProfilerResources();
                Debug.VulkanWarning("[Vulkan] GPU pipeline profiler query pool allocation failed; Vulkan GPU render-pipeline timing disabled.");
                return;
            }
            _resourceRuntime.RegisterResource(
                ObjectType.QueryPool,
                _telemetry._vulkanGpuProfilerQueryPools[i].Handle,
                $"GpuProfiler.QueryPool[{i}]");
        }

        _telemetry._vulkanGpuProfilerEnabled = true;
    }

    private unsafe void EnsureVulkanGpuProfilerSlotCapacity(int slotCount)
    {
        if (!_telemetry._vulkanGpuProfilerEnabled ||
            _telemetry._vulkanGpuProfilerQueryPools is null ||
            _telemetry._vulkanGpuProfilerQueryReady is null ||
            _telemetry._vulkanGpuProfilerPendingScopes is null ||
            _telemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            _telemetry._vulkanGpuProfilerSubmittedFrameIds is null ||
            _telemetry._vulkanGpuProfilerQueryPools.Length >= slotCount)
        {
            return;
        }

        int oldLength = _telemetry._vulkanGpuProfilerQueryPools.Length;
        Array.Resize(ref _telemetry._vulkanGpuProfilerQueryPools, slotCount);
        Array.Resize(ref _telemetry._vulkanGpuProfilerQueryReady, slotCount);
        Array.Resize(ref _telemetry._vulkanGpuProfilerPendingScopes, slotCount);
        Array.Resize(ref _telemetry._vulkanGpuProfilerPendingQueryCounts, slotCount);
        Array.Resize(ref _telemetry._vulkanGpuProfilerSubmittedFrameIds, slotCount);

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = VulkanFrameTelemetry.GpuProfilerQueryCount,
        };

        for (int i = oldLength; i < slotCount; i++)
        {
            _telemetry._vulkanGpuProfilerPendingScopes[i] = [];
            if (_deviceContext.Api.CreateQueryPool(_deviceContext.Device, ref createInfo, null, out _telemetry._vulkanGpuProfilerQueryPools[i]) != Result.Success)
            {
                Debug.VulkanWarning("[Vulkan] GPU pipeline profiler query pool growth failed for frame slot {0}; render-pipeline GPU timings disabled for that slot.", i);
                _telemetry._vulkanGpuProfilerQueryPools[i] = default;
            }
            else
            {
                _resourceRuntime.RegisterResource(
                    ObjectType.QueryPool,
                    _telemetry._vulkanGpuProfilerQueryPools[i].Handle,
                    $"GpuProfiler.QueryPool[{i}]");
            }
        }
    }

    private void DestroyVulkanGpuProfilerResources()
    {
        ClearVulkanGpuProfilerPendingQueries();

        if (_telemetry._vulkanGpuProfilerQueryPools is not null)
        {
            for (int i = 0; i < _telemetry._vulkanGpuProfilerQueryPools.Length; i++)
            {
                QueryPool queryPool = _telemetry._vulkanGpuProfilerQueryPools[i];
                if (queryPool.Handle != 0)
                    _resourceRuntime.RetireQueryPool(queryPool, "VulkanFrameLoop.GpuProfiler");
            }
        }

        _telemetry._vulkanGpuProfilerQueryPools = null;
        _telemetry._vulkanGpuProfilerQueryReady = null;
        _telemetry._vulkanGpuProfilerPendingScopes = null;
        _telemetry._vulkanGpuProfilerPendingQueryCounts = null;
        _telemetry._vulkanGpuProfilerSubmittedFrameIds = null;
        _telemetry._vulkanGpuProfilerEnabled = false;
    }

    private void ClearVulkanGpuProfilerPendingQueries()
    {
        _telemetry._vulkanGpuProfilerRecordingActive = false;
        _telemetry._vulkanGpuProfilerRecordingFrameSlot = -1;
        _telemetry._vulkanGpuProfilerNextQuery = 0;
        _telemetry._vulkanGpuProfilerBudgetWarningIssued = false;

        if (_telemetry._vulkanGpuProfilerPendingScopes is not null)
        {
            for (int i = 0; i < _telemetry._vulkanGpuProfilerPendingScopes.Length; i++)
                _telemetry._vulkanGpuProfilerPendingScopes[i]?.Clear();
        }

        if (_telemetry._vulkanGpuProfilerPendingQueryCounts is not null)
            Array.Fill(_telemetry._vulkanGpuProfilerPendingQueryCounts, 0);

        if (_telemetry._vulkanGpuProfilerSubmittedFrameIds is not null)
            Array.Fill(_telemetry._vulkanGpuProfilerSubmittedFrameIds, 0UL);

        if (_telemetry._vulkanGpuProfilerQueryReady is not null)
            Array.Fill(_telemetry._vulkanGpuProfilerQueryReady, false);
    }

    internal void MarkVulkanGpuProfilerSubmitted(int frameSlot)
    {
        if (_telemetry._vulkanGpuProfilerQueryReady is null ||
            _telemetry._vulkanGpuProfilerPendingScopes is null ||
            _telemetry._vulkanGpuProfilerSubmittedFrameIds is null ||
            frameSlot < 0 ||
            frameSlot >= _telemetry._vulkanGpuProfilerQueryReady.Length ||
            frameSlot >= _telemetry._vulkanGpuProfilerPendingScopes.Length)
        {
            return;
        }

        _telemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = RuntimeEngine.Rendering.State.RenderFrameId;
        _telemetry._vulkanGpuProfilerQueryReady[frameSlot] = _telemetry._vulkanGpuProfilerPendingScopes[frameSlot].Count > 0;
    }

    internal unsafe void SampleVulkanGpuProfilerQueries(int frameSlot)
    {
        if (!_telemetry._vulkanGpuProfilerEnabled ||
            _telemetry._vulkanGpuProfilerQueryPools is null ||
            _telemetry._vulkanGpuProfilerQueryReady is null ||
            _telemetry._vulkanGpuProfilerPendingScopes is null ||
            _telemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            _telemetry._vulkanGpuProfilerSubmittedFrameIds is null ||
            frameSlot < 0 ||
            frameSlot >= _telemetry._vulkanGpuProfilerQueryPools.Length ||
            frameSlot >= _telemetry._vulkanGpuProfilerQueryReady.Length ||
            frameSlot >= _telemetry._vulkanGpuProfilerPendingScopes.Length ||
            frameSlot >= _telemetry._vulkanGpuProfilerPendingQueryCounts.Length ||
            frameSlot >= _telemetry._vulkanGpuProfilerSubmittedFrameIds.Length)
        {
            return;
        }

        if (!_telemetry._vulkanGpuProfilerQueryReady[frameSlot])
            return;

        QueryPool queryPool = _telemetry._vulkanGpuProfilerQueryPools[frameSlot];
        int queryCount = _telemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot];
        List<VulkanGpuProfilerPendingScope> samples = _telemetry._vulkanGpuProfilerPendingScopes[frameSlot];
        ulong frameId = _telemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot];
        if (queryPool.Handle == 0 || queryCount <= 0 || samples.Count == 0 || frameId == 0UL)
            return;

        ulong[] rented = ArrayPool<ulong>.Shared.Rent(queryCount);
        try
        {
            fixed (ulong* timestamps = rented)
            {
                Result result = _deviceContext.Api.GetQueryPoolResults(
                    _deviceContext.Device,
                    queryPool,
                    0,
                    (uint)queryCount,
                    (nuint)(sizeof(ulong) * queryCount),
                    timestamps,
                    (ulong)sizeof(ulong),
                    QueryResultFlags.Result64Bit);

                if (result != Result.Success)
                    return;

                _resourceRuntime.NotifyResourceUseCompleted(ObjectType.QueryPool, queryPool.Handle);

                for (int i = 0; i < samples.Count; i++)
                {
                    VulkanGpuProfilerPendingScope sample = samples[i];
                    if (sample.EndQuery >= queryCount || sample.StartQuery >= queryCount)
                        continue;

                    ulong start = timestamps[sample.StartQuery];
                    ulong end = timestamps[sample.EndQuery];
                    if (end <= start)
                        continue;

                    ulong nanoseconds = (ulong)Math.Round((end - start) * _telemetry._frameTimingTimestampPeriodNanoseconds);
                    RenderPipelineGpuProfiler.Instance.RecordBackendGpuTimingSample(
                        frameId,
                        VulkanFrameTelemetry.GpuProfilerBackendName,
                        sample.Path,
                        nanoseconds);
                }

                RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryReadbackBytes, queryCount * sizeof(ulong));
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
            samples.Clear();
            _telemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
            _telemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
            _telemetry._vulkanGpuProfilerQueryReady[frameSlot] = false;
        }
    }

}
