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
        "Vulkan GPU pipeline command timing is quarantined because timestamp instrumentation must not mutate main render command buffers.";

    private QueryPool[]? _frameTimingQueryPools;
    private bool[]? _frameTimingQueryReady;
    private bool _frameTimingGpuEnabled;
    private double _frameTimingTimestampPeriodNanoseconds = 1.0;
    private QueryPool[]? _vulkanGpuProfilerQueryPools;
    private bool[]? _vulkanGpuProfilerQueryReady;
    private List<VulkanGpuProfilerPendingScope>[]? _vulkanGpuProfilerPendingScopes;
    private int[]? _vulkanGpuProfilerPendingQueryCounts;
    private ulong[]? _vulkanGpuProfilerSubmittedFrameIds;
    private bool _vulkanGpuProfilerEnabled;
    private bool _vulkanGpuProfilerRecordingActive;
    private bool _vulkanGpuProfilerBudgetWarningIssued;
    private int _vulkanGpuProfilerRecordingFrameSlot = -1;
    private uint _vulkanGpuProfilerNextQuery;
    private bool[]? _vulkanGpuProfilerCommandBufferInstrumented;
    private int[]? _vulkanGpuProfilerCommandBufferFrameSlots;

    private static bool IsVulkanGpuProfilerCommandBufferInstrumentationEnabled
        => EnableVulkanGpuProfilerCommandBufferInstrumentation;

    internal static string VulkanGpuProfilerCommandTimingStatusMessage
        => IsVulkanGpuProfilerCommandBufferInstrumentationEnabled
            ? "Vulkan GPU timings are collected from recorded command buffers."
            : VulkanGpuProfilerQuarantinedMessage;

    private readonly struct VulkanGpuProfilerPendingScope(string[] path, uint startQuery, uint endQuery)
    {
        public string[] Path { get; } = path;
        public uint StartQuery { get; } = startQuery;
        public uint EndQuery { get; } = endQuery;
    }

    private readonly struct VulkanGpuProfilerScope : IDisposable
    {
        private readonly VulkanRenderer? _renderer;
        private readonly CommandBuffer _commandBuffer;
        private readonly QueryPool _queryPool;
        private readonly int _frameSlot;
        private readonly uint _endQuery;
        private readonly string[]? _path;

        public VulkanGpuProfilerScope(
            VulkanRenderer renderer,
            CommandBuffer commandBuffer,
            QueryPool queryPool,
            int frameSlot,
            uint endQuery,
            string[] path)
        {
            _renderer = renderer;
            _commandBuffer = commandBuffer;
            _queryPool = queryPool;
            _frameSlot = frameSlot;
            _endQuery = endQuery;
            _path = path;
        }

        public void Dispose()
            => _renderer?.EndVulkanGpuProfilerScope(_commandBuffer, _queryPool, _frameSlot, _endQuery, _path);
    }

    private void CreateFrameTimingResources()
    {
        DestroyFrameTimingResources();

        if (device.Handle == 0)
            return;

        Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        _frameTimingTimestampPeriodNanoseconds = Math.Max(properties.Limits.TimestampPeriod, 0.0001f);

        int timingSlotCount = Math.Max(swapChainImages?.Length ?? 0, MAX_FRAMES_IN_FLIGHT);
        _frameTimingQueryPools = new QueryPool[timingSlotCount];
        _frameTimingQueryReady = new bool[timingSlotCount];

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
        CreateVulkanGpuProfilerResources();
    }

    private void DestroyFrameTimingResources()
    {
        DestroyVulkanGpuProfilerResources();

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
        _frameTimingQueryReady = null;
        _frameTimingGpuEnabled = false;
        _vulkanGpuProfilerCommandBufferInstrumented = null;
        _vulkanGpuProfilerCommandBufferFrameSlots = null;
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
        SampleVulkanGpuProfilerQueries(frameSlot);

        if (!_frameTimingGpuEnabled || _frameTimingQueryPools is null ||
            _frameTimingQueryReady is null ||
            frameSlot < 0 || frameSlot >= _frameTimingQueryPools.Length)
        {
            return;
        }

        if (!_frameTimingQueryReady[frameSlot])
            return;

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
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameGpuCommandBufferTime(TimeSpan.FromMilliseconds(gpuMilliseconds));
    }

    private void MarkFrameTimingSubmitted(int frameSlot)
    {
        if (_frameTimingQueryReady is null || frameSlot < 0 || frameSlot >= _frameTimingQueryReady.Length)
            return;

        _frameTimingQueryReady[frameSlot] = true;
        MarkVulkanGpuProfilerSubmitted(frameSlot);
    }

    private void CreateVulkanGpuProfilerResources()
    {
        DestroyVulkanGpuProfilerResources();

        if (device.Handle == 0)
            return;

        int profilerSlotCount = Math.Max(swapChainImages?.Length ?? 0, MAX_FRAMES_IN_FLIGHT);
        _vulkanGpuProfilerQueryPools = new QueryPool[profilerSlotCount];
        _vulkanGpuProfilerQueryReady = new bool[profilerSlotCount];
        _vulkanGpuProfilerPendingScopes = new List<VulkanGpuProfilerPendingScope>[profilerSlotCount];
        _vulkanGpuProfilerPendingQueryCounts = new int[profilerSlotCount];
        _vulkanGpuProfilerSubmittedFrameIds = new ulong[profilerSlotCount];

        QueryPoolCreateInfo createInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = VulkanGpuProfilerQueryCount,
        };

        for (int i = 0; i < _vulkanGpuProfilerQueryPools.Length; i++)
        {
            _vulkanGpuProfilerPendingScopes[i] = [];
            if (Api!.CreateQueryPool(device, ref createInfo, null, out _vulkanGpuProfilerQueryPools[i]) != Result.Success)
            {
                DestroyVulkanGpuProfilerResources();
                Debug.VulkanWarning("[Vulkan] GPU pipeline profiler query pool allocation failed; Vulkan GPU render-pipeline timing disabled.");
                return;
            }
        }

        _vulkanGpuProfilerEnabled = true;
    }

    private void DestroyVulkanGpuProfilerResources()
    {
        ClearVulkanGpuProfilerPendingQueries();

        if (_vulkanGpuProfilerQueryPools is not null)
        {
            for (int i = 0; i < _vulkanGpuProfilerQueryPools.Length; i++)
            {
                QueryPool queryPool = _vulkanGpuProfilerQueryPools[i];
                if (queryPool.Handle != 0)
                    Api!.DestroyQueryPool(device, queryPool, null);
            }
        }

        _vulkanGpuProfilerQueryPools = null;
        _vulkanGpuProfilerQueryReady = null;
        _vulkanGpuProfilerPendingScopes = null;
        _vulkanGpuProfilerPendingQueryCounts = null;
        _vulkanGpuProfilerSubmittedFrameIds = null;
        _vulkanGpuProfilerEnabled = false;
        _vulkanGpuProfilerCommandBufferInstrumented = null;
        _vulkanGpuProfilerCommandBufferFrameSlots = null;
    }

    private void ClearVulkanGpuProfilerPendingQueries()
    {
        _vulkanGpuProfilerRecordingActive = false;
        _vulkanGpuProfilerRecordingFrameSlot = -1;
        _vulkanGpuProfilerNextQuery = 0;
        _vulkanGpuProfilerBudgetWarningIssued = false;

        if (_vulkanGpuProfilerPendingScopes is not null)
        {
            for (int i = 0; i < _vulkanGpuProfilerPendingScopes.Length; i++)
                _vulkanGpuProfilerPendingScopes[i]?.Clear();
        }

        if (_vulkanGpuProfilerPendingQueryCounts is not null)
            Array.Fill(_vulkanGpuProfilerPendingQueryCounts, 0);

        if (_vulkanGpuProfilerSubmittedFrameIds is not null)
            Array.Fill(_vulkanGpuProfilerSubmittedFrameIds, 0UL);

        if (_vulkanGpuProfilerQueryReady is not null)
            Array.Fill(_vulkanGpuProfilerQueryReady, false);
    }

    private bool IsVulkanGpuProfilerCommandBufferStateDirty(uint imageIndex, bool profilingActive, int frameSlot)
    {
        if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled)
            return false;

        EnsureVulkanGpuProfilerCommandBufferStateCapacity();

        if (_vulkanGpuProfilerCommandBufferInstrumented is null ||
            _vulkanGpuProfilerCommandBufferFrameSlots is null ||
            imageIndex >= _vulkanGpuProfilerCommandBufferInstrumented.Length)
        {
            return false;
        }

        bool recordedInstrumented = _vulkanGpuProfilerCommandBufferInstrumented[imageIndex];
        if (recordedInstrumented != profilingActive)
            return true;

        return profilingActive && _vulkanGpuProfilerCommandBufferFrameSlots[imageIndex] != frameSlot;
    }

    private void UpdateVulkanGpuProfilerCommandBufferState(uint imageIndex, bool profilingActive, int frameSlot)
    {
        EnsureVulkanGpuProfilerCommandBufferStateCapacity();

        if (_vulkanGpuProfilerCommandBufferInstrumented is null ||
            _vulkanGpuProfilerCommandBufferFrameSlots is null ||
            imageIndex >= _vulkanGpuProfilerCommandBufferInstrumented.Length)
        {
            return;
        }

        _vulkanGpuProfilerCommandBufferInstrumented[imageIndex] = profilingActive;
        _vulkanGpuProfilerCommandBufferFrameSlots[imageIndex] = profilingActive ? frameSlot : -1;
    }

    private void EnsureVulkanGpuProfilerCommandBufferStateCapacity()
    {
        int length = _commandBuffers?.Length ?? 0;
        if (length <= 0)
        {
            _vulkanGpuProfilerCommandBufferInstrumented = null;
            _vulkanGpuProfilerCommandBufferFrameSlots = null;
            return;
        }

        if (_vulkanGpuProfilerCommandBufferInstrumented is { Length: var instrumentedLength } &&
            _vulkanGpuProfilerCommandBufferFrameSlots is { Length: var slotsLength } &&
            instrumentedLength == length &&
            slotsLength == length)
        {
            return;
        }

        _vulkanGpuProfilerCommandBufferInstrumented = new bool[length];
        _vulkanGpuProfilerCommandBufferFrameSlots = new int[length];
        Array.Fill(_vulkanGpuProfilerCommandBufferFrameSlots, -1);
    }

    private void BeginVulkanGpuProfilerQueries(CommandBuffer commandBuffer, int frameSlot)
    {
        _vulkanGpuProfilerRecordingActive = false;
        _vulkanGpuProfilerRecordingFrameSlot = -1;
        _vulkanGpuProfilerNextQuery = 0;
        _vulkanGpuProfilerBudgetWarningIssued = false;

        if (_vulkanGpuProfilerPendingScopes is not null &&
            frameSlot >= 0 &&
            frameSlot < _vulkanGpuProfilerPendingScopes.Length)
        {
            _vulkanGpuProfilerPendingScopes[frameSlot].Clear();
        }

        if (_vulkanGpuProfilerPendingQueryCounts is not null &&
            frameSlot >= 0 &&
            frameSlot < _vulkanGpuProfilerPendingQueryCounts.Length)
        {
            _vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
        }

        if (_vulkanGpuProfilerSubmittedFrameIds is not null &&
            frameSlot >= 0 &&
            frameSlot < _vulkanGpuProfilerSubmittedFrameIds.Length)
        {
            _vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
        }

        if (_vulkanGpuProfilerQueryReady is not null &&
            frameSlot >= 0 &&
            frameSlot < _vulkanGpuProfilerQueryReady.Length)
        {
            _vulkanGpuProfilerQueryReady[frameSlot] = false;
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

        if (!_vulkanGpuProfilerEnabled ||
            !RenderPipelineGpuProfiler.Instance.IsProfilingActive ||
            _vulkanGpuProfilerQueryPools is null ||
            frameSlot < 0 ||
            frameSlot >= _vulkanGpuProfilerQueryPools.Length)
        {
            return;
        }

        QueryPool queryPool = _vulkanGpuProfilerQueryPools[frameSlot];
        if (queryPool.Handle == 0)
            return;

        Api!.CmdResetQueryPool(commandBuffer, queryPool, 0, VulkanGpuProfilerQueryCount);
        _vulkanGpuProfilerRecordingActive = true;
        _vulkanGpuProfilerRecordingFrameSlot = frameSlot;
    }

    private VulkanGpuProfilerScope TryBeginVulkanGpuProfilerScope(CommandBuffer commandBuffer, FrameOp op, int passIndex)
    {
        if (!TryReserveVulkanGpuProfilerQueries(commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery))
            return default;

        string[] path = BuildVulkanGpuProfilerPath(op, passIndex);
        Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, startQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        return new VulkanGpuProfilerScope(this, commandBuffer, queryPool, _vulkanGpuProfilerRecordingFrameSlot, endQuery, path);
    }

    private VulkanGpuProfilerScope TryBeginVulkanGpuProfilerScope(CommandBuffer commandBuffer, in FrameOpContext context, int passIndex, string scopeName)
    {
        if (!TryReserveVulkanGpuProfilerQueries(commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery))
            return default;

        string[] path = BuildVulkanGpuProfilerPath(context, passIndex, scopeName);
        Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, queryPool, startQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        return new VulkanGpuProfilerScope(this, commandBuffer, queryPool, _vulkanGpuProfilerRecordingFrameSlot, endQuery, path);
    }

    private bool TryReserveVulkanGpuProfilerQueries(CommandBuffer commandBuffer, out QueryPool queryPool, out uint startQuery, out uint endQuery)
    {
        queryPool = default;
        startQuery = 0;
        endQuery = 0;

        if (!_vulkanGpuProfilerRecordingActive ||
            _vulkanGpuProfilerQueryPools is null ||
            _vulkanGpuProfilerRecordingFrameSlot < 0 ||
            _vulkanGpuProfilerRecordingFrameSlot >= _vulkanGpuProfilerQueryPools.Length ||
            commandBuffer.Handle == 0)
        {
            return false;
        }

        if (_vulkanGpuProfilerNextQuery + 1 >= VulkanGpuProfilerQueryCount)
        {
            if (!_vulkanGpuProfilerBudgetWarningIssued)
            {
                _vulkanGpuProfilerBudgetWarningIssued = true;
                RenderPipelineGpuProfiler.Instance.RecordBackendGpuTimingStatus(
                    RuntimeEngine.Rendering.State.RenderFrameId,
                    VulkanGpuProfilerBackendName,
                    $"Vulkan GPU pipeline timing reached the per-frame timestamp scope budget ({VulkanGpuProfilerMaxScopesPerFrame}); later scopes were skipped.",
                    skippedSamples: 1);
            }

            return false;
        }

        queryPool = _vulkanGpuProfilerQueryPools[_vulkanGpuProfilerRecordingFrameSlot];
        if (queryPool.Handle == 0)
            return false;

        startQuery = _vulkanGpuProfilerNextQuery++;
        endQuery = _vulkanGpuProfilerNextQuery++;
        return true;
    }

    private void EndVulkanGpuProfilerScope(CommandBuffer commandBuffer, QueryPool queryPool, int frameSlot, uint endQuery, string[]? path)
    {
        if (path is null ||
            !_vulkanGpuProfilerRecordingActive ||
            frameSlot < 0 ||
            _vulkanGpuProfilerPendingScopes is null ||
            _vulkanGpuProfilerPendingQueryCounts is null ||
            frameSlot >= _vulkanGpuProfilerPendingScopes.Length ||
            commandBuffer.Handle == 0 ||
            queryPool.Handle == 0)
        {
            return;
        }

        Api!.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, queryPool, endQuery);
        RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.TimestampQueryCount);
        _vulkanGpuProfilerPendingScopes[frameSlot].Add(new VulkanGpuProfilerPendingScope(path, endQuery - 1, endQuery));
        _vulkanGpuProfilerPendingQueryCounts[frameSlot] = Math.Max(_vulkanGpuProfilerPendingQueryCounts[frameSlot], (int)endQuery + 1);
    }

    private void MarkVulkanGpuProfilerSubmitted(int frameSlot)
    {
        if (_vulkanGpuProfilerQueryReady is null ||
            _vulkanGpuProfilerPendingScopes is null ||
            _vulkanGpuProfilerSubmittedFrameIds is null ||
            frameSlot < 0 ||
            frameSlot >= _vulkanGpuProfilerQueryReady.Length ||
            frameSlot >= _vulkanGpuProfilerPendingScopes.Length)
        {
            return;
        }

        _vulkanGpuProfilerSubmittedFrameIds[frameSlot] = RuntimeEngine.Rendering.State.RenderFrameId;
        _vulkanGpuProfilerQueryReady[frameSlot] = _vulkanGpuProfilerPendingScopes[frameSlot].Count > 0;
    }

    private void SampleVulkanGpuProfilerQueries(int frameSlot)
    {
        if (!_vulkanGpuProfilerEnabled ||
            _vulkanGpuProfilerQueryPools is null ||
            _vulkanGpuProfilerQueryReady is null ||
            _vulkanGpuProfilerPendingScopes is null ||
            _vulkanGpuProfilerPendingQueryCounts is null ||
            _vulkanGpuProfilerSubmittedFrameIds is null ||
            frameSlot < 0 ||
            frameSlot >= _vulkanGpuProfilerQueryPools.Length ||
            frameSlot >= _vulkanGpuProfilerQueryReady.Length ||
            frameSlot >= _vulkanGpuProfilerPendingScopes.Length ||
            frameSlot >= _vulkanGpuProfilerPendingQueryCounts.Length ||
            frameSlot >= _vulkanGpuProfilerSubmittedFrameIds.Length)
        {
            return;
        }

        if (!_vulkanGpuProfilerQueryReady[frameSlot])
            return;

        QueryPool queryPool = _vulkanGpuProfilerQueryPools[frameSlot];
        int queryCount = _vulkanGpuProfilerPendingQueryCounts[frameSlot];
        List<VulkanGpuProfilerPendingScope> samples = _vulkanGpuProfilerPendingScopes[frameSlot];
        ulong frameId = _vulkanGpuProfilerSubmittedFrameIds[frameSlot];
        if (queryPool.Handle == 0 || queryCount <= 0 || samples.Count == 0 || frameId == 0UL)
            return;

        ulong[] rented = ArrayPool<ulong>.Shared.Rent(queryCount);
        try
        {
            fixed (ulong* timestamps = rented)
            {
                Result result = Api!.GetQueryPoolResults(
                    device,
                    queryPool,
                    0,
                    (uint)queryCount,
                    (nuint)(sizeof(ulong) * queryCount),
                    timestamps,
                    (ulong)sizeof(ulong),
                    QueryResultFlags.Result64Bit);

                if (result != Result.Success)
                    return;

                for (int i = 0; i < samples.Count; i++)
                {
                    VulkanGpuProfilerPendingScope sample = samples[i];
                    if (sample.EndQuery >= queryCount || sample.StartQuery >= queryCount)
                        continue;

                    ulong start = timestamps[sample.StartQuery];
                    ulong end = timestamps[sample.EndQuery];
                    if (end <= start)
                        continue;

                    ulong nanoseconds = (ulong)Math.Round((end - start) * _frameTimingTimestampPeriodNanoseconds);
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
            _vulkanGpuProfilerPendingQueryCounts[frameSlot] = 0;
            _vulkanGpuProfilerSubmittedFrameIds[frameSlot] = 0UL;
            _vulkanGpuProfilerQueryReady[frameSlot] = false;
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
            return "Pass[-1:Swapchain]";

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
            IndirectDrawOp indirect => $"IndirectDraw[count={indirect.DrawCount}; stride={indirect.Stride}; useCount={indirect.UseCount}]",
            MeshTaskDispatchIndirectCountOp meshTask => $"MeshTaskDispatchIndirectCount[max={meshTask.MaxDrawCount}; stride={meshTask.Stride}]",
            TransformFeedbackOp transformFeedback => $"TransformFeedback[{transformFeedback.Operation}; target={GetTargetName(transformFeedback.Target)}]",
            ComputeDispatchOp compute => $"ComputeDispatch[program={GetDisplayName(compute.Program.Data.Name, "UnnamedProgram")}; groups={compute.GroupsX}x{compute.GroupsY}x{compute.GroupsZ}]",
            DlssFrameGenerationOp frameGeneration => $"DLSS.FrameGenerationInputs[{frameGeneration.Parameters.InputWidth}x{frameGeneration.Parameters.InputHeight}->{frameGeneration.Parameters.OutputWidth}x{frameGeneration.Parameters.OutputHeight}]",
            MemoryBarrierOp barrier => $"MemoryBarrier[mask={barrier.Mask}]",
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
        => GetDisplayName(target?.Name, "Swapchain");

    private static string GetDisplayName(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
