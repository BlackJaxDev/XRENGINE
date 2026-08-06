using System;
using System.Buffers;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const uint FrameTimingQueryCount = 2;
    private const uint VulkanGpuProfilerMaxScopesPerFrame = 512;
    private const uint VulkanGpuProfilerQueryCount = VulkanGpuProfilerMaxScopesPerFrame * 2;
    private const string VulkanGpuProfilerBackendName = "Vulkan";
    private const bool EnableVulkanGpuProfilerCommandBufferInstrumentation = false;
    private const string VulkanGpuProfilerQuarantinedMessage =
        "Vulkan GPU pipeline command timing is disabled; set XRE_GPU_TIMESTAMP_DENSE=1 for dense diagnostic command timestamps. Coarse Vulkan command-buffer GPU timing remains available.";
    private static bool VulkanGpuTimestampDenseModeEnabled
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.GpuTimestampDense);


    private static bool IsVulkanGpuProfilerCommandBufferInstrumentationEnabled
        => EnableVulkanGpuProfilerCommandBufferInstrumentation ||
           VulkanGpuTimestampDenseModeEnabled;

    internal static string VulkanGpuProfilerCommandTimingStatusMessage
        => IsVulkanGpuProfilerCommandBufferInstrumentationEnabled
            ? "Vulkan GPU timings are collected from recorded command buffers."
            : VulkanGpuProfilerQuarantinedMessage;

    private void CreateFrameTimingResources()
    {
        DestroyFrameTimingResources();

        if (_deviceContext.Device.Handle == 0)
            return;

        Api!.GetPhysicalDeviceProperties(_deviceContext.PhysicalDevice, out PhysicalDeviceProperties properties);
        _frameTelemetry._frameTimingTimestampPeriodNanoseconds = Math.Max(properties.Limits.TimestampPeriod, 0.0001f);

        int timingSlotCount = Math.Max(OutputRuntime.Desktop.Images?.Length ?? 0, MAX_FRAMES_IN_FLIGHT);
        _frameTelemetry._frameTimingQueryPools = new QueryPool[timingSlotCount];
        _frameTelemetry._frameTimingQueryReady = new bool[timingSlotCount];

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = FrameTimingQueryCount,
        };

        for (int i = 0; i < _frameTelemetry._frameTimingQueryPools.Length; i++)
        {
            if (Api.CreateQueryPool(_deviceContext.Device, ref createInfo, null, out _frameTelemetry._frameTimingQueryPools[i]) != Result.Success)
            {
                DestroyFrameTimingResources();
                _frameTelemetry._frameTimingGpuEnabled = false;
                Debug.VulkanWarning("[Vulkan] Frame timing query pool allocation failed; GPU frame timing instrumentation disabled.");
                return;
            }
            RegisterVulkanResource(
                ObjectType.QueryPool,
                _frameTelemetry._frameTimingQueryPools[i].Handle,
                $"FrameTiming.QueryPool[{i}]");
        }

        _frameTelemetry._frameTimingGpuEnabled = true;
        CreateVulkanGpuProfilerResources();
    }

    private void EnsureFrameTimingSlotCapacity(int slotCount)
    {
        if (slotCount <= 0 || _deviceContext.Device.Handle == 0)
            return;

        EnsureFrameTimingQueryPoolCapacity(slotCount);
        EnsureVulkanGpuProfilerSlotCapacity(slotCount);
    }

    private void EnsureFrameTimingQueryPoolCapacity(int slotCount)
    {
        if (!_frameTelemetry._frameTimingGpuEnabled ||
            _frameTelemetry._frameTimingQueryPools is null ||
            _frameTelemetry._frameTimingQueryReady is null ||
            _frameTelemetry._frameTimingQueryPools.Length >= slotCount)
        {
            return;
        }

        int oldLength = _frameTelemetry._frameTimingQueryPools.Length;
        Array.Resize(ref _frameTelemetry._frameTimingQueryPools, slotCount);
        Array.Resize(ref _frameTelemetry._frameTimingQueryReady, slotCount);

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = FrameTimingQueryCount,
        };

        for (int i = oldLength; i < slotCount; i++)
        {
            if (Api!.CreateQueryPool(_deviceContext.Device, ref createInfo, null, out _frameTelemetry._frameTimingQueryPools[i]) != Result.Success)
            {
                Debug.VulkanWarning("[Vulkan] Frame timing query pool growth failed for frame slot {0}; GPU frame timing disabled for that slot.", i);
                _frameTelemetry._frameTimingQueryPools[i] = default;
            }
            else
            {
                RegisterVulkanResource(
                    ObjectType.QueryPool,
                    _frameTelemetry._frameTimingQueryPools[i].Handle,
                    $"FrameTiming.QueryPool[{i}]");
            }
        }
    }

    private void DestroyFrameTimingResources()
    {
        DestroyVulkanGpuProfilerResources();

        if (_frameTelemetry._frameTimingQueryPools is not null)
        {
            for (int i = 0; i < _frameTelemetry._frameTimingQueryPools.Length; i++)
            {
                QueryPool queryPool = _frameTelemetry._frameTimingQueryPools[i];
                if (queryPool.Handle != 0)
                    RetireQueryPool(queryPool);
            }
        }

        _frameTelemetry._frameTimingQueryPools = null;
        _frameTelemetry._frameTimingQueryReady = null;
        _frameTelemetry._frameTimingGpuEnabled = false;
        _frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented = null;
        _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots = null;
    }

    private void BeginFrameTimingQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!_frameTelemetry._frameTimingGpuEnabled || _frameTelemetry._frameTimingQueryPools is null ||
            frameSlot < 0 || frameSlot >= _frameTelemetry._frameTimingQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _frameTelemetry._frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.QueryPool,
            queryPool.Handle,
            "FrameTiming.QueryPool");
        Api!.CmdResetQueryPool(commandBuffer, queryPool, 0, FrameTimingQueryCount);
        Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, 0);
    }

    private void EndFrameTimingQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        if (!_frameTelemetry._frameTimingGpuEnabled || _frameTelemetry._frameTimingQueryPools is null ||
            frameSlot < 0 || frameSlot >= _frameTelemetry._frameTimingQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _frameTelemetry._frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, queryPool, 1);
    }

    private void SampleFrameTimingQueries(int frameSlot)
    {
        SampleVulkanGpuProfilerQueries(frameSlot);

        if (!_frameTelemetry._frameTimingGpuEnabled || _frameTelemetry._frameTimingQueryPools is null ||
            _frameTelemetry._frameTimingQueryReady is null ||
            frameSlot < 0 || frameSlot >= _frameTelemetry._frameTimingQueryPools.Length)
        {
            return;
        }

        if (!_frameTelemetry._frameTimingQueryReady[frameSlot])
            return;

        QueryPool queryPool = _frameTelemetry._frameTimingQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        ulong* timestamps = stackalloc ulong[(int)FrameTimingQueryCount];
        Result result = Api!.GetQueryPoolResults(
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

        NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, queryPool.Handle);

        ulong start = timestamps[0];
        ulong end = timestamps[1];
        if (end < start)
            return;

        double gpuMilliseconds = (end - start) * _frameTelemetry._frameTimingTimestampPeriodNanoseconds / 1_000_000.0;
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameGpuCommandBufferTime(TimeSpan.FromMilliseconds(gpuMilliseconds));
    }

    private void MarkFrameTimingSubmitted(int frameSlot)
    {
        if (_frameTelemetry._frameTimingQueryReady is null || frameSlot < 0 || frameSlot >= _frameTelemetry._frameTimingQueryReady.Length)
            return;

        _frameTelemetry._frameTimingQueryReady[frameSlot] = true;
        MarkVulkanGpuProfilerSubmitted(frameSlot);
    }

    private void CreateVulkanGpuProfilerResources()
    {
        DestroyVulkanGpuProfilerResources();

        if (_deviceContext.Device.Handle == 0)
            return;

        int profilerSlotCount = Math.Max(OutputRuntime.Desktop.Images?.Length ?? 0, MAX_FRAMES_IN_FLIGHT);
        _frameTelemetry._vulkanGpuProfilerQueryPools = new QueryPool[profilerSlotCount];
        _frameTelemetry._vulkanGpuProfilerQueryReady = new bool[profilerSlotCount];
        _frameTelemetry._vulkanGpuProfilerPendingScopes = new List<VulkanGpuProfilerPendingScope>[profilerSlotCount];
        _frameTelemetry._vulkanGpuProfilerPendingQueryCounts = new int[profilerSlotCount];
        _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds = new ulong[profilerSlotCount];

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = VulkanGpuProfilerQueryCount,
        };

        for (int i = 0; i < _frameTelemetry._vulkanGpuProfilerQueryPools.Length; i++)
        {
            _frameTelemetry._vulkanGpuProfilerPendingScopes[i] = [];
            if (Api!.CreateQueryPool(_deviceContext.Device, ref createInfo, null, out _frameTelemetry._vulkanGpuProfilerQueryPools[i]) != Result.Success)
            {
                DestroyVulkanGpuProfilerResources();
                Debug.VulkanWarning("[Vulkan] GPU pipeline profiler query pool allocation failed; Vulkan GPU render-pipeline timing disabled.");
                return;
            }
            RegisterVulkanResource(
                ObjectType.QueryPool,
                _frameTelemetry._vulkanGpuProfilerQueryPools[i].Handle,
                $"GpuProfiler.QueryPool[{i}]");
        }

        _frameTelemetry._vulkanGpuProfilerEnabled = true;
    }

    private void EnsureVulkanGpuProfilerSlotCapacity(int slotCount)
    {
        if (!_frameTelemetry._vulkanGpuProfilerEnabled ||
            _frameTelemetry._vulkanGpuProfilerQueryPools is null ||
            _frameTelemetry._vulkanGpuProfilerQueryReady is null ||
            _frameTelemetry._vulkanGpuProfilerPendingScopes is null ||
            _frameTelemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds is null ||
            _frameTelemetry._vulkanGpuProfilerQueryPools.Length >= slotCount)
        {
            return;
        }

        int oldLength = _frameTelemetry._vulkanGpuProfilerQueryPools.Length;
        Array.Resize(ref _frameTelemetry._vulkanGpuProfilerQueryPools, slotCount);
        Array.Resize(ref _frameTelemetry._vulkanGpuProfilerQueryReady, slotCount);
        Array.Resize(ref _frameTelemetry._vulkanGpuProfilerPendingScopes, slotCount);
        Array.Resize(ref _frameTelemetry._vulkanGpuProfilerPendingQueryCounts, slotCount);
        Array.Resize(ref _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds, slotCount);

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = VulkanGpuProfilerQueryCount,
        };

        for (int i = oldLength; i < slotCount; i++)
        {
            _frameTelemetry._vulkanGpuProfilerPendingScopes[i] = [];
            if (Api!.CreateQueryPool(_deviceContext.Device, ref createInfo, null, out _frameTelemetry._vulkanGpuProfilerQueryPools[i]) != Result.Success)
            {
                Debug.VulkanWarning("[Vulkan] GPU pipeline profiler query pool growth failed for frame slot {0}; render-pipeline GPU timings disabled for that slot.", i);
                _frameTelemetry._vulkanGpuProfilerQueryPools[i] = default;
            }
            else
            {
                RegisterVulkanResource(
                    ObjectType.QueryPool,
                    _frameTelemetry._vulkanGpuProfilerQueryPools[i].Handle,
                    $"GpuProfiler.QueryPool[{i}]");
            }
        }
    }

    private void DestroyVulkanGpuProfilerResources()
    {
        ClearVulkanGpuProfilerPendingQueries();

        if (_frameTelemetry._vulkanGpuProfilerQueryPools is not null)
        {
            for (int i = 0; i < _frameTelemetry._vulkanGpuProfilerQueryPools.Length; i++)
            {
                QueryPool queryPool = _frameTelemetry._vulkanGpuProfilerQueryPools[i];
                if (queryPool.Handle != 0)
                    RetireQueryPool(queryPool);
            }
        }

        _frameTelemetry._vulkanGpuProfilerQueryPools = null;
        _frameTelemetry._vulkanGpuProfilerQueryReady = null;
        _frameTelemetry._vulkanGpuProfilerPendingScopes = null;
        _frameTelemetry._vulkanGpuProfilerPendingQueryCounts = null;
        _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds = null;
        _frameTelemetry._vulkanGpuProfilerEnabled = false;
        _frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented = null;
        _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots = null;
    }

    private void ClearVulkanGpuProfilerPendingQueries()
    {
        _frameTelemetry._vulkanGpuProfilerRecordingActive = false;
        _frameTelemetry._vulkanGpuProfilerRecordingFrameSlot = -1;
        _frameTelemetry._vulkanGpuProfilerNextQuery = 0;
        _frameTelemetry._vulkanGpuProfilerBudgetWarningIssued = false;

        if (_frameTelemetry._vulkanGpuProfilerPendingScopes is not null)
        {
            for (int i = 0; i < _frameTelemetry._vulkanGpuProfilerPendingScopes.Length; i++)
                _frameTelemetry._vulkanGpuProfilerPendingScopes[i]?.Clear();
        }

        if (_frameTelemetry._vulkanGpuProfilerPendingQueryCounts is not null)
            Array.Fill(_frameTelemetry._vulkanGpuProfilerPendingQueryCounts, 0);

        if (_frameTelemetry._vulkanGpuProfilerSubmittedFrameIds is not null)
            Array.Fill(_frameTelemetry._vulkanGpuProfilerSubmittedFrameIds, 0UL);

        if (_frameTelemetry._vulkanGpuProfilerQueryReady is not null)
            Array.Fill(_frameTelemetry._vulkanGpuProfilerQueryReady, false);
    }

    private bool IsVulkanGpuProfilerCommandBufferStateDirty(uint imageIndex, bool profilingActive, int frameSlot)
    {
        if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled)
            return false;

        EnsureVulkanGpuProfilerCommandBufferStateCapacity();

        if (_frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented is null ||
            _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots is null ||
            imageIndex >= _frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented.Length)
        {
            return false;
        }

        bool recordedInstrumented = _frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented[imageIndex];
        if (recordedInstrumented != profilingActive)
            return true;

        return profilingActive && _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots[imageIndex] != frameSlot;
    }

    private void UpdateVulkanGpuProfilerCommandBufferState(uint imageIndex, bool profilingActive, int frameSlot)
    {
        EnsureVulkanGpuProfilerCommandBufferStateCapacity();

        if (_frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented is null ||
            _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots is null ||
            imageIndex >= _frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented.Length)
        {
            return;
        }

        _frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented[imageIndex] = profilingActive;
        _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots[imageIndex] = profilingActive ? frameSlot : -1;
    }

    private void EnsureVulkanGpuProfilerCommandBufferStateCapacity()
    {
        int length = _commandBuffers?.Length ?? 0;
        if (length <= 0)
        {
            _frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented = null;
            _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots = null;
            return;
        }

        if (_frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented is { Length: var instrumentedLength } &&
            _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots is { Length: var slotsLength } &&
            instrumentedLength == length &&
            slotsLength == length)
        {
            return;
        }

        _frameTelemetry._vulkanGpuProfilerCommandBufferInstrumented = new bool[length];
        _frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots = new int[length];
        Array.Fill(_frameTelemetry._vulkanGpuProfilerCommandBufferFrameSlots, -1);
    }

    private void BeginVulkanGpuProfilerQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        _frameTelemetry._vulkanGpuProfilerRecordingActive = false;
        _frameTelemetry._vulkanGpuProfilerRecordingFrameSlot = -1;
        _frameTelemetry._vulkanGpuProfilerNextQuery = 0;
        _frameTelemetry._vulkanGpuProfilerBudgetWarningIssued = false;

        if (_frameTelemetry._vulkanGpuProfilerPendingScopes is not null &&
            frameSlot >= 0 &&
            frameSlot < _frameTelemetry._vulkanGpuProfilerPendingScopes.Length)
        {
            _frameTelemetry._vulkanGpuProfilerPendingScopes[frameSlot].Clear();
        }

        if (_frameTelemetry._vulkanGpuProfilerPendingQueryCounts is not null &&
            frameSlot >= 0 &&
            frameSlot < _frameTelemetry._vulkanGpuProfilerPendingQueryCounts.Length)
        {
            _frameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
        }

        if (_frameTelemetry._vulkanGpuProfilerSubmittedFrameIds is not null &&
            frameSlot >= 0 &&
            frameSlot < _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds.Length)
        {
            _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
        }

        if (_frameTelemetry._vulkanGpuProfilerQueryReady is not null &&
            frameSlot >= 0 &&
            frameSlot < _frameTelemetry._vulkanGpuProfilerQueryReady.Length)
        {
            _frameTelemetry._vulkanGpuProfilerQueryReady[frameSlot] = false;
        }

        if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled)
        {
            if (RenderPipelineGpuProfiler.Instance.IsProfilingActive)
            {
                RenderPipelineGpuProfiler.Instance.RecordBackendGpuTimingStatus(
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    VulkanGpuProfilerBackendName,
                    VulkanGpuProfilerCommandTimingStatusMessage);
            }

            return;
        }

        if (!_frameTelemetry._vulkanGpuProfilerEnabled ||
            !RenderPipelineGpuProfiler.Instance.IsProfilingActive ||
            _frameTelemetry._vulkanGpuProfilerQueryPools is null ||
            frameSlot < 0 ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _frameTelemetry._vulkanGpuProfilerQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.QueryPool,
            queryPool.Handle,
            "GpuProfiler.QueryPool");
        Api!.CmdResetQueryPool(commandBuffer, queryPool, 0, VulkanGpuProfilerQueryCount);
        _frameTelemetry._vulkanGpuProfilerRecordingActive = true;
        _frameTelemetry._vulkanGpuProfilerRecordingFrameSlot = frameSlot;
    }

    private VulkanGpuProfilerScope TryBeginVulkanGpuProfilerScope(CommandBuffer commandBuffer, FrameOp op, int passIndex)
    {
        if (!TryReserveVulkanGpuProfilerQueries(commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery))
            return default;

        string[] path = BuildVulkanGpuProfilerPath(op, passIndex);
        Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, startQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        return new VulkanGpuProfilerScope(this, commandBuffer, queryPool, _frameTelemetry._vulkanGpuProfilerRecordingFrameSlot, endQuery, path);
    }

    private VulkanGpuProfilerScope TryBeginVulkanGpuProfilerScope(CommandBuffer commandBuffer, in FrameOpContext context, int passIndex, string scopeName)
    {
        if (!TryReserveVulkanGpuProfilerQueries(commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery))
            return default;

        string[] path = BuildVulkanGpuProfilerPath(context, passIndex, scopeName);
        Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, startQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        return new VulkanGpuProfilerScope(this, commandBuffer, queryPool, _frameTelemetry._vulkanGpuProfilerRecordingFrameSlot, endQuery, path);
    }

    private bool TryReserveVulkanGpuProfilerQueries(CommandBuffer commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery)
    {
        queryPool = default;
        startQuery = 0;
        endQuery = 0;

        if (!_frameTelemetry._vulkanGpuProfilerRecordingActive ||
            _frameTelemetry._vulkanGpuProfilerQueryPools is null ||
            _frameTelemetry._vulkanGpuProfilerRecordingFrameSlot < 0 ||
            _frameTelemetry._vulkanGpuProfilerRecordingFrameSlot >= _frameTelemetry._vulkanGpuProfilerQueryPools.Length ||
            commandBuffer.Handle == 0)
        {
            return false;
        }

        if (_frameTelemetry._vulkanGpuProfilerNextQuery + 1 >= VulkanGpuProfilerQueryCount)
        {
            if (!_frameTelemetry._vulkanGpuProfilerBudgetWarningIssued)
            {
                _frameTelemetry._vulkanGpuProfilerBudgetWarningIssued = true;
                RenderPipelineGpuProfiler.Instance.RecordBackendGpuTimingStatus(
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    VulkanGpuProfilerBackendName,
                    $"Vulkan GPU pipeline timing reached the per-frame timestamp scope budget ({VulkanGpuProfilerMaxScopesPerFrame}); later scopes were skipped.",
                    skippedSamples: 1);
            }

            return false;
        }

        queryPool = _frameTelemetry._vulkanGpuProfilerQueryPools[_frameTelemetry._vulkanGpuProfilerRecordingFrameSlot];
        if (queryPool.Handle == 0)
            return false;

        startQuery = _frameTelemetry._vulkanGpuProfilerNextQuery++;
        endQuery = _frameTelemetry._vulkanGpuProfilerNextQuery++;
        return true;
    }

    private void EndVulkanGpuProfilerScope(CommandBuffer commandBuffer, QueryPool queryPool, int frameSlot, uint endQuery, string[]? path)
    {
        if (path is null ||
            !_frameTelemetry._vulkanGpuProfilerRecordingActive ||
            frameSlot < 0 ||
            _frameTelemetry._vulkanGpuProfilerPendingScopes is null ||
            _frameTelemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerPendingScopes.Length ||
            commandBuffer.Handle == 0 ||
            queryPool.Handle == 0)
        {
            return;
        }

        Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, queryPool, endQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        _frameTelemetry._vulkanGpuProfilerPendingScopes[frameSlot].Add(new VulkanGpuProfilerPendingScope(path, endQuery - 1, endQuery));
        _frameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = Math.Max(_frameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot], (int)endQuery + 1);
    }

    private void MarkVulkanGpuProfilerSubmitted(int frameSlot)
    {
        if (_frameTelemetry._vulkanGpuProfilerQueryReady is null ||
            _frameTelemetry._vulkanGpuProfilerPendingScopes is null ||
            _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds is null ||
            frameSlot < 0 ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerQueryReady.Length ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerPendingScopes.Length)
        {
            return;
        }

        _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = RuntimeEngine.Rendering.State.RenderFrameId;
        _frameTelemetry._vulkanGpuProfilerQueryReady[frameSlot] = _frameTelemetry._vulkanGpuProfilerPendingScopes[frameSlot].Count > 0;
    }

    private void CaptureVulkanGpuProfilerVariantScopes(int frameSlot, PrimaryCommandArtifactOwner variant)
    {
        if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled ||
            !_frameTelemetry._vulkanGpuProfilerEnabled ||
            !RenderPipelineGpuProfiler.Instance.IsProfilingActive ||
            _frameTelemetry._vulkanGpuProfilerPendingScopes is null ||
            _frameTelemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            frameSlot < 0 ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerPendingScopes.Length ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerPendingQueryCounts.Length)
        {
            variant.GpuProfilerScopes = null;
            variant.GpuProfilerQueryCount = 0;
            return;
        }

        List<VulkanGpuProfilerPendingScope> scopes = _frameTelemetry._vulkanGpuProfilerPendingScopes[frameSlot];
        int queryCount = _frameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot];
        if (scopes.Count == 0 || queryCount <= 0)
        {
            variant.GpuProfilerScopes = [];
            variant.GpuProfilerQueryCount = 0;
            return;
        }

        variant.GpuProfilerScopes = scopes.ToArray();
        variant.GpuProfilerQueryCount = queryCount;
    }

    private void PrepareVulkanGpuProfilerReusableSubmission(
        int frameSlot,
        PrimaryCommandArtifactOwner variant,
        bool profilingActive)
    {
        if (_frameTelemetry._vulkanGpuProfilerPendingScopes is not null &&
            frameSlot >= 0 &&
            frameSlot < _frameTelemetry._vulkanGpuProfilerPendingScopes.Length)
        {
            _frameTelemetry._vulkanGpuProfilerPendingScopes[frameSlot].Clear();
        }

        if (_frameTelemetry._vulkanGpuProfilerPendingQueryCounts is not null &&
            frameSlot >= 0 &&
            frameSlot < _frameTelemetry._vulkanGpuProfilerPendingQueryCounts.Length)
        {
            _frameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
        }

        if (_frameTelemetry._vulkanGpuProfilerSubmittedFrameIds is not null &&
            frameSlot >= 0 &&
            frameSlot < _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds.Length)
        {
            _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
        }

        if (_frameTelemetry._vulkanGpuProfilerQueryReady is not null &&
            frameSlot >= 0 &&
            frameSlot < _frameTelemetry._vulkanGpuProfilerQueryReady.Length)
        {
            _frameTelemetry._vulkanGpuProfilerQueryReady[frameSlot] = false;
        }

        if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled ||
            !_frameTelemetry._vulkanGpuProfilerEnabled ||
            !profilingActive ||
            !variant.GpuProfilerActive ||
            variant.GpuProfilerFrameSlot != frameSlot ||
            variant.GpuProfilerScopes is not { Length: > 0 } scopes ||
            variant.GpuProfilerQueryCount <= 0 ||
            _frameTelemetry._vulkanGpuProfilerPendingScopes is null ||
            _frameTelemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            frameSlot < 0 ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerPendingScopes.Length ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerPendingQueryCounts.Length)
        {
            return;
        }

        List<VulkanGpuProfilerPendingScope> pendingScopes = _frameTelemetry._vulkanGpuProfilerPendingScopes[frameSlot];
        pendingScopes.AddRange(scopes);
        _frameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = variant.GpuProfilerQueryCount;
    }

    private void SampleVulkanGpuProfilerQueries(int frameSlot)
    {
        if (!_frameTelemetry._vulkanGpuProfilerEnabled ||
            _frameTelemetry._vulkanGpuProfilerQueryPools is null ||
            _frameTelemetry._vulkanGpuProfilerQueryReady is null ||
            _frameTelemetry._vulkanGpuProfilerPendingScopes is null ||
            _frameTelemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds is null ||
            frameSlot < 0 ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerQueryPools.Length ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerQueryReady.Length ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerPendingScopes.Length ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerPendingQueryCounts.Length ||
            frameSlot >= _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds.Length)
        {
            return;
        }

        if (!_frameTelemetry._vulkanGpuProfilerQueryReady[frameSlot])
            return;

        QueryPool queryPool = _frameTelemetry._vulkanGpuProfilerQueryPools[frameSlot];
        int queryCount = _frameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot];
        List<VulkanGpuProfilerPendingScope> samples = _frameTelemetry._vulkanGpuProfilerPendingScopes[frameSlot];
        ulong frameId = _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot];
        if (queryPool.Handle == 0 || queryCount <= 0 || samples.Count == 0 || frameId == 0UL)
            return;

        ulong[] rented = ArrayPool<ulong>.Shared.Rent(queryCount);
        try
        {
            fixed (ulong* timestamps = rented)
            {
                Result result = Api!.GetQueryPoolResults(
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

                NotifyVulkanResourceUseCompleted(ObjectType.QueryPool, queryPool.Handle);

                for (int i = 0; i < samples.Count; i++)
                {
                    VulkanGpuProfilerPendingScope sample = samples[i];
                    if (sample.EndQuery >= queryCount || sample.StartQuery >= queryCount)
                        continue;

                    ulong start = timestamps[sample.StartQuery];
                    ulong end = timestamps[sample.EndQuery];
                    if (end <= start)
                        continue;

                    ulong nanoseconds = (ulong)Math.Round((end - start) * _frameTelemetry._frameTimingTimestampPeriodNanoseconds);
                    RenderPipelineGpuProfiler.Instance.RecordBackendGpuTimingSample(
                        frameId,
                        VulkanGpuProfilerBackendName,
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
            _frameTelemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
            _frameTelemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
            _frameTelemetry._vulkanGpuProfilerQueryReady[frameSlot] = false;
        }
    }

    private static string[] BuildVulkanGpuProfilerPath(FrameOp op, int passIndex)
        => BuildVulkanGpuProfilerPath(op.Context, passIndex, BuildVulkanGpuProfilerOpLabel(op));

    private static string[] BuildVulkanGpuProfilerPath(in FrameOpContext context, int passIndex, string scopeName)
    {
        string pipelineName = context.PipelineInstance?.ProfilerKey ??
            context.PipelineInstance?.DebugName ??
            (context.PipelineIdentity != 0 ? $"Pipeline#{context.PipelineIdentity}" : "Vulkan");

        string passName = ResolveVulkanGpuProfilerPassName(passIndex, context.PassMetadata);
        return [pipelineName, passName, scopeName];
    }

    private static string ResolveVulkanGpuProfilerPassName(int passIndex, IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passIndex == VulkanBarrierPlanner.SwapchainPassIndex)
            return $"Pass[{VulkanBarrierPlanner.SwapchainPassIndex}:Swapchain]";

        if (passMetadata is not null)
        {
            foreach (RenderPassMetadata metadata in passMetadata)
            {
                if (metadata.PassIndex == passIndex)
                    return $"Pass[{passIndex}:{metadata.Name}]";
            }
        }

        return passIndex == int.MinValue ? "Pass[Unknown]" : $"Pass[{passIndex}]";
    }

    private static string BuildVulkanGpuProfilerOpLabel(FrameOp op)
    {
        return op switch
        {
            ClearOp clear => $"Clear[target={GetTargetName(clear.Target)}; color={clear.ClearColor}; depth={clear.ClearDepth}; stencil={clear.ClearStencil}]",
            BlitOp blit => $"Blit[src={GetTargetName(blit.InFbo)}; dst={GetTargetName(blit.OutFbo)}; color={blit.ColorBit}; depth={blit.DepthBit}; stencil={blit.StencilBit}]",
            MeshDrawOp draw => BuildVulkanGpuProfilerMeshDrawLabel(draw),
            QueryOp query => $"Query[{query.Operation}; descriptor={query.Descriptor}; fbo={GetTargetName(query.Target)}]",
            IndirectDrawOp indirect => $"IndirectDraw[count={indirect.DrawCount}; stride={indirect.Stride}; useCount={indirect.UseCount}]",
            MeshTaskDispatchIndirectCountOp meshTask => $"MeshTaskDispatchIndirectCount[max={meshTask.MaxDrawCount}; stride={meshTask.Stride}]",
            TransformFeedbackOp transformFeedback => $"TransformFeedback[{transformFeedback.Operation}; target={GetTargetName(transformFeedback.Target)}]",
            ComputeDispatchOp compute => $"ComputeDispatch[program={GetDisplayName(compute.Program.Data.Name, "UnnamedProgram")}; groups={compute.GroupsX}x{compute.GroupsY}x{compute.GroupsZ}]",
            ComputeDispatchIndirectOp computeIndirect => $"ComputeDispatchIndirect[program={GetDisplayName(computeIndirect.Program.Data.Name, "UnnamedProgram")}; offset={computeIndirect.ArgumentOffset}]",
            BufferCopyOp copy => $"BufferCopy[bytes={copy.ByteCount}; srcOffset={copy.SourceOffset}; dstOffset={copy.DestinationOffset}]",
            SubmissionMarkerOp marker => $"SubmissionMarker[label={marker.Label}]",
            DlssFrameGenerationOp frameGeneration => $"DLSS.FrameGenerationInputs[{frameGeneration.Parameters.InputWidth}x{frameGeneration.Parameters.InputHeight}->{frameGeneration.Parameters.OutputWidth}x{frameGeneration.Parameters.OutputHeight}]",
            MemoryBarrierOp barrier => $"MemoryBarrier[mask={barrier.Mask}]",
            PublishFramebufferForSamplingOp publish => $"PublishFramebufferForSampling[fbo={GetTargetName(publish.FrameBuffer)}]",
            _ => op.GetType().Name,
        };
    }

    private static string BuildVulkanGpuProfilerMeshDrawLabel(MeshDrawOp draw)
    {
        var meshRenderer = draw.Draw.Renderer.MeshRenderer;
        string meshName = GetDisplayName(meshRenderer.Mesh?.Name, "UnnamedMesh");
        string materialName = GetDisplayName((draw.Draw.MaterialOverride ?? meshRenderer.Material)?.Name, "UnnamedMaterial");
        string targetName = GetTargetName(draw.Target);
        return $"MeshDraw[mesh={meshName}; material={materialName}; target={targetName}; instances={draw.Draw.Instances}]";
    }

    private static string GetTargetName(XRFrameBuffer? target)
        => target is null ? "Swapchain" : GetDisplayName(target.Name, "UnnamedFbo");

    private static string GetDisplayName(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
