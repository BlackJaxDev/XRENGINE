using System;
using System.Buffers;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanFrameLoop
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

    internal void CreateFrameTimingResources()
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

    private void EnsureFrameTimingQueryPoolCapacity(int slotCount)
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
        _telemetry._vulkanGpuProfilerCommandBufferInstrumented = null;
        _telemetry._vulkanGpuProfilerCommandBufferFrameSlots = null;
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

    private void SampleFrameTimingQueries(int frameSlot)
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

    private void CreateVulkanGpuProfilerResources()
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
            QueryCount = VulkanGpuProfilerQueryCount,
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

    private void EnsureVulkanGpuProfilerSlotCapacity(int slotCount)
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
            QueryCount = VulkanGpuProfilerQueryCount,
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
        _telemetry._vulkanGpuProfilerCommandBufferInstrumented = null;
        _telemetry._vulkanGpuProfilerCommandBufferFrameSlots = null;
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

    private bool IsVulkanGpuProfilerCommandBufferStateDirty(uint imageIndex, bool profilingActive, int frameSlot)
    {
        if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled)
            return false;

        EnsureVulkanGpuProfilerCommandBufferStateCapacity();

        if (_telemetry._vulkanGpuProfilerCommandBufferInstrumented is null ||
            _telemetry._vulkanGpuProfilerCommandBufferFrameSlots is null ||
            imageIndex >= _telemetry._vulkanGpuProfilerCommandBufferInstrumented.Length)
        {
            return false;
        }

        bool recordedInstrumented = _telemetry._vulkanGpuProfilerCommandBufferInstrumented[imageIndex];
        if (recordedInstrumented != profilingActive)
            return true;

        return profilingActive && _telemetry._vulkanGpuProfilerCommandBufferFrameSlots[imageIndex] != frameSlot;
    }

    private void UpdateVulkanGpuProfilerCommandBufferState(uint imageIndex, bool profilingActive, int frameSlot)
    {
        EnsureVulkanGpuProfilerCommandBufferStateCapacity();

        if (_telemetry._vulkanGpuProfilerCommandBufferInstrumented is null ||
            _telemetry._vulkanGpuProfilerCommandBufferFrameSlots is null ||
            imageIndex >= _telemetry._vulkanGpuProfilerCommandBufferInstrumented.Length)
        {
            return;
        }

        _telemetry._vulkanGpuProfilerCommandBufferInstrumented[imageIndex] = profilingActive;
        _telemetry._vulkanGpuProfilerCommandBufferFrameSlots[imageIndex] = profilingActive ? frameSlot : -1;
    }

    private void EnsureVulkanGpuProfilerCommandBufferStateCapacity()
    {
        int length = _commandRuntime.CommandBuffers.Buffers?.Length ?? 0;
        if (length <= 0)
        {
            _telemetry._vulkanGpuProfilerCommandBufferInstrumented = null;
            _telemetry._vulkanGpuProfilerCommandBufferFrameSlots = null;
            return;
        }

        if (_telemetry._vulkanGpuProfilerCommandBufferInstrumented is { Length: var instrumentedLength } &&
            _telemetry._vulkanGpuProfilerCommandBufferFrameSlots is { Length: var slotsLength } &&
            instrumentedLength == length &&
            slotsLength == length)
        {
            return;
        }

        _telemetry._vulkanGpuProfilerCommandBufferInstrumented = new bool[length];
        _telemetry._vulkanGpuProfilerCommandBufferFrameSlots = new int[length];
        Array.Fill(_telemetry._vulkanGpuProfilerCommandBufferFrameSlots, -1);
    }

    private void BeginVulkanGpuProfilerQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        _telemetry._vulkanGpuProfilerRecordingActive = false;
        _telemetry._vulkanGpuProfilerRecordingFrameSlot = -1;
        _telemetry._vulkanGpuProfilerNextQuery = 0;
        _telemetry._vulkanGpuProfilerBudgetWarningIssued = false;

        if (_telemetry._vulkanGpuProfilerPendingScopes is not null &&
            frameSlot >= 0 &&
            frameSlot < _telemetry._vulkanGpuProfilerPendingScopes.Length)
        {
            _telemetry._vulkanGpuProfilerPendingScopes[frameSlot].Clear();
        }

        if (_telemetry._vulkanGpuProfilerPendingQueryCounts is not null &&
            frameSlot >= 0 &&
            frameSlot < _telemetry._vulkanGpuProfilerPendingQueryCounts.Length)
        {
            _telemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
        }

        if (_telemetry._vulkanGpuProfilerSubmittedFrameIds is not null &&
            frameSlot >= 0 &&
            frameSlot < _telemetry._vulkanGpuProfilerSubmittedFrameIds.Length)
        {
            _telemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
        }

        if (_telemetry._vulkanGpuProfilerQueryReady is not null &&
            frameSlot >= 0 &&
            frameSlot < _telemetry._vulkanGpuProfilerQueryReady.Length)
        {
            _telemetry._vulkanGpuProfilerQueryReady[frameSlot] = false;
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

        if (!_telemetry._vulkanGpuProfilerEnabled ||
            !RenderPipelineGpuProfiler.Instance.IsProfilingActive ||
            _telemetry._vulkanGpuProfilerQueryPools is null ||
            frameSlot < 0 ||
            frameSlot >= _telemetry._vulkanGpuProfilerQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _telemetry._vulkanGpuProfilerQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        _commandRuntime.TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.QueryPool,
            queryPool.Handle,
            "GpuProfiler.QueryPool");
        _deviceContext.Api.CmdResetQueryPool(commandBuffer, queryPool, 0, VulkanGpuProfilerQueryCount);
        _telemetry._vulkanGpuProfilerRecordingActive = true;
        _telemetry._vulkanGpuProfilerRecordingFrameSlot = frameSlot;
    }

    private VulkanGpuProfilerScope TryBeginVulkanGpuProfilerScope(CommandBuffer commandBuffer, FrameOp op, int passIndex)
    {
        if (!TryReserveVulkanGpuProfilerQueries(commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery))
            return default;

        string[] path = BuildVulkanGpuProfilerPath(op, passIndex);
        _deviceContext.Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, startQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        return new VulkanGpuProfilerScope(Api!, _telemetry, commandBuffer, queryPool, _telemetry._vulkanGpuProfilerRecordingFrameSlot, endQuery, path);
    }

    private VulkanGpuProfilerScope TryBeginVulkanGpuProfilerScope(CommandBuffer commandBuffer, in FrameOpContext context, int passIndex, string scopeName)
    {
        if (!TryReserveVulkanGpuProfilerQueries(commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery))
            return default;

        string[] path = BuildVulkanGpuProfilerPath(context, passIndex, scopeName);
        _deviceContext.Api.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, startQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        return new VulkanGpuProfilerScope(Api!, _telemetry, commandBuffer, queryPool, _telemetry._vulkanGpuProfilerRecordingFrameSlot, endQuery, path);
    }

    private bool TryReserveVulkanGpuProfilerQueries(CommandBuffer commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery)
    {
        queryPool = default;
        startQuery = 0;
        endQuery = 0;

        if (!_telemetry._vulkanGpuProfilerRecordingActive ||
            _telemetry._vulkanGpuProfilerQueryPools is null ||
            _telemetry._vulkanGpuProfilerRecordingFrameSlot < 0 ||
            _telemetry._vulkanGpuProfilerRecordingFrameSlot >= _telemetry._vulkanGpuProfilerQueryPools.Length ||
            commandBuffer.Handle == 0)
        {
            return false;
        }

        if (_telemetry._vulkanGpuProfilerNextQuery + 1 >= VulkanGpuProfilerQueryCount)
        {
            if (!_telemetry._vulkanGpuProfilerBudgetWarningIssued)
            {
                _telemetry._vulkanGpuProfilerBudgetWarningIssued = true;
                RenderPipelineGpuProfiler.Instance.RecordBackendGpuTimingStatus(
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    VulkanGpuProfilerBackendName,
                    $"Vulkan GPU pipeline timing reached the per-frame timestamp scope budget ({VulkanGpuProfilerMaxScopesPerFrame}); later scopes were skipped.",
                    skippedSamples: 1);
            }

            return false;
        }

        queryPool = _telemetry._vulkanGpuProfilerQueryPools[_telemetry._vulkanGpuProfilerRecordingFrameSlot];
        if (queryPool.Handle == 0)
            return false;

        startQuery = _telemetry._vulkanGpuProfilerNextQuery++;
        endQuery = _telemetry._vulkanGpuProfilerNextQuery++;
        return true;
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

    private void CaptureVulkanGpuProfilerVariantScopes(int frameSlot, PrimaryCommandArtifactOwner variant)
    {
        if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled ||
            !_telemetry._vulkanGpuProfilerEnabled ||
            !RenderPipelineGpuProfiler.Instance.IsProfilingActive ||
            _telemetry._vulkanGpuProfilerPendingScopes is null ||
            _telemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            frameSlot < 0 ||
            frameSlot >= _telemetry._vulkanGpuProfilerPendingScopes.Length ||
            frameSlot >= _telemetry._vulkanGpuProfilerPendingQueryCounts.Length)
        {
            variant.GpuProfilerScopes = null;
            variant.GpuProfilerQueryCount = 0;
            return;
        }

        List<VulkanGpuProfilerPendingScope> scopes = _telemetry._vulkanGpuProfilerPendingScopes[frameSlot];
        int queryCount = _telemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot];
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
        if (_telemetry._vulkanGpuProfilerPendingScopes is not null &&
            frameSlot >= 0 &&
            frameSlot < _telemetry._vulkanGpuProfilerPendingScopes.Length)
        {
            _telemetry._vulkanGpuProfilerPendingScopes[frameSlot].Clear();
        }

        if (_telemetry._vulkanGpuProfilerPendingQueryCounts is not null &&
            frameSlot >= 0 &&
            frameSlot < _telemetry._vulkanGpuProfilerPendingQueryCounts.Length)
        {
            _telemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
        }

        if (_telemetry._vulkanGpuProfilerSubmittedFrameIds is not null &&
            frameSlot >= 0 &&
            frameSlot < _telemetry._vulkanGpuProfilerSubmittedFrameIds.Length)
        {
            _telemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
        }

        if (_telemetry._vulkanGpuProfilerQueryReady is not null &&
            frameSlot >= 0 &&
            frameSlot < _telemetry._vulkanGpuProfilerQueryReady.Length)
        {
            _telemetry._vulkanGpuProfilerQueryReady[frameSlot] = false;
        }

        if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled ||
            !_telemetry._vulkanGpuProfilerEnabled ||
            !profilingActive ||
            !variant.GpuProfilerActive ||
            variant.GpuProfilerFrameSlot != frameSlot ||
            variant.GpuProfilerScopes is not { Length: > 0 } scopes ||
            variant.GpuProfilerQueryCount <= 0 ||
            _telemetry._vulkanGpuProfilerPendingScopes is null ||
            _telemetry._vulkanGpuProfilerPendingQueryCounts is null ||
            frameSlot < 0 ||
            frameSlot >= _telemetry._vulkanGpuProfilerPendingScopes.Length ||
            frameSlot >= _telemetry._vulkanGpuProfilerPendingQueryCounts.Length)
        {
            return;
        }

        List<VulkanGpuProfilerPendingScope> pendingScopes = _telemetry._vulkanGpuProfilerPendingScopes[frameSlot];
        pendingScopes.AddRange(scopes);
        _telemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = variant.GpuProfilerQueryCount;
    }

    internal void SampleVulkanGpuProfilerQueries(int frameSlot)
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
            _telemetry._vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
            _telemetry._vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
            _telemetry._vulkanGpuProfilerQueryReady[frameSlot] = false;
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
