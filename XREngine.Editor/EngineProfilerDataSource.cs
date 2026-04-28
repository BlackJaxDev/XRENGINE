using System.Diagnostics;
using XREngine;
using XREngine.Data.Profiling;

namespace XREngine.Editor;

/// <summary>
/// In-process implementation of <see cref="IProfilerDataSource"/> that reads
/// directly from <c>Engine.*</c> statics (no network roundtrip).
/// Reuses the same conversion patterns as <c>Engine.ProfilerSender</c>.
/// </summary>
internal sealed class EngineProfilerDataSource : IProfilerDataSource
{
    // ── Cached packet snapshots ──
    private ProfilerFramePacket? _latestFrame;
    private RenderStatsPacket? _latestRenderStats;
    private ThreadAllocationsPacket? _latestAllocations;
    private BvhMetricsPacket? _latestBvhMetrics;
    private JobSystemStatsPacket? _latestJobStats;
    private MainThreadInvokesPacket? _latestMainThreadInvokes;

    public ProfilerFramePacket? LatestFrame => _latestFrame;
    public RenderStatsPacket? LatestRenderStats => _latestRenderStats;
    public ThreadAllocationsPacket? LatestAllocations => _latestAllocations;
    public BvhMetricsPacket? LatestBvhMetrics => _latestBvhMetrics;
    public JobSystemStatsPacket? LatestJobStats => _latestJobStats;
    public MainThreadInvokesPacket? LatestMainThreadInvokes => _latestMainThreadInvokes;

    // In-process: no heartbeat packet, but we synthesize one for display
    public HeartbeatPacket? LatestHeartbeat { get; } = new HeartbeatPacket
    {
        ProcessName = Process.GetCurrentProcess().ProcessName,
        ProcessId = Environment.ProcessId,
        UptimeMs = Environment.TickCount64,
    };

    // Always connected — we're in-process
    public bool IsConnected => true;
    public double SecondsSinceLastHeartbeat => 0.0;

    // No network counters for in-process
    public long PacketsReceived => 0;
    public long BytesReceived => 0;
    public long ErrorsCount => 0;

    // No multi-instance for in-process
    public IReadOnlyList<ProfilerSourceInfo> GetKnownSources() => Array.Empty<ProfilerSourceInfo>();
    public bool HasMultipleSources => false;

    /// <summary>
    /// Collects all engine telemetry into the packet snapshots.
    /// Call once per frame, before the shared renderer's <c>ProcessLatestData()</c>.
    /// </summary>
    public void CollectFromEngine()
    {
        _latestFrame = CollectProfilerFrame();
        _latestRenderStats = CollectRenderStats();
        _latestAllocations = CollectThreadAllocations();
        _latestBvhMetrics = CollectBvhMetrics();
        _latestJobStats = CollectJobSystemStats();
        _latestMainThreadInvokes = CollectMainThreadInvokes();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Collectors — same patterns as Engine.ProfilerSender.cs
    // ═══════════════════════════════════════════════════════════════

    private static ProfilerFramePacket? CollectProfilerFrame()
    {
        if (!Engine.Profiler.TryGetSnapshot(out var snapshot, out var history) || snapshot is null)
            return null;

        var threads = new ProfilerThreadData[snapshot.Threads.Count];
        for (int i = 0; i < snapshot.Threads.Count; i++)
        {
            var t = snapshot.Threads[i];
            threads[i] = new ProfilerThreadData
            {
                ThreadId = t.ThreadId,
                TotalTimeMs = t.TotalTimeMs,
                RootNodes = ConvertNodes(t.RootNodes),
            };
        }

        return new ProfilerFramePacket
        {
            FrameTime = snapshot.FrameTime,
            Threads = threads,
            ThreadHistory = history ?? [],
            ComponentTimings = ConvertComponentTimings(snapshot.ComponentTimings?.Components),
        };
    }

    private static ProfilerNodeData[] ConvertNodes(IReadOnlyList<Engine.CodeProfiler.ProfilerNodeSnapshot> nodes)
    {
        if (nodes.Count == 0)
            return [];

        var result = new ProfilerNodeData[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            result[i] = new ProfilerNodeData
            {
                Name = n.Name,
                ElapsedMs = n.ElapsedMs,
                Children = ConvertNodes(n.Children),
            };
        }
        return result;
    }

    private static ProfilerComponentTimingData[] ConvertComponentTimings(IReadOnlyList<Engine.CodeProfiler.ProfilerComponentTimingSnapshot>? components)
    {
        if (components is null || components.Count == 0)
            return [];

        var result = new ProfilerComponentTimingData[components.Count];
        for (int i = 0; i < components.Count; i++)
        {
            var component = components[i];
            result[i] = new ProfilerComponentTimingData
            {
                ComponentId = component.ComponentId,
                ComponentName = component.ComponentName,
                ComponentType = component.ComponentType,
                SceneNodeName = component.SceneNodeName,
                ElapsedMs = component.ElapsedMs,
                CallCount = component.CallCount,
                TickGroupMask = component.TickGroupMask,
            };
        }

        return result;
    }

    private static RenderStatsPacket? CollectRenderStats()
    {
        var physicsChainSnapshot = XREngine.Rendering.Compute.GPUPhysicsChainDispatcher.GetBandwidthPressureSnapshot();
        var listenerSnapshot = Engine.Rendering.Stats.GetRenderMatrixListenerSnapshot();
        var listenerEntries = new RenderMatrixListenerEntry[listenerSnapshot.Length];
        for (int i = 0; i < listenerSnapshot.Length; i++)
        {
            listenerEntries[i] = new RenderMatrixListenerEntry
            {
                Name = listenerSnapshot[i].Key,
                Count = listenerSnapshot[i].Value,
            };
        }

        return new RenderStatsPacket
        {
            DrawCalls = Engine.Rendering.Stats.DrawCalls,
            MultiDrawCalls = Engine.Rendering.Stats.MultiDrawCalls,
            TrianglesRendered = Engine.Rendering.Stats.TrianglesRendered,
            GpuCpuFallbackEvents = Engine.Rendering.Stats.GpuCpuFallbackEvents,
            GpuCpuFallbackRecoveredCommands = Engine.Rendering.Stats.GpuCpuFallbackRecoveredCommands,
            GpuTransparencyOpaqueOrOtherVisible = Engine.Rendering.Stats.GpuTransparencyOpaqueOrOtherVisible,
            GpuTransparencyMaskedVisible = Engine.Rendering.Stats.GpuTransparencyMaskedVisible,
            GpuTransparencyApproximateVisible = Engine.Rendering.Stats.GpuTransparencyApproximateVisible,
            GpuTransparencyExactVisible = Engine.Rendering.Stats.GpuTransparencyExactVisible,
            VulkanPipelineBinds = Engine.Rendering.Stats.VulkanPipelineBinds,
            VulkanDescriptorBinds = Engine.Rendering.Stats.VulkanDescriptorBinds,
            VulkanPushConstantWrites = Engine.Rendering.Stats.VulkanPushConstantWrites,
            VulkanVertexBufferBinds = Engine.Rendering.Stats.VulkanVertexBufferBinds,
            VulkanIndexBufferBinds = Engine.Rendering.Stats.VulkanIndexBufferBinds,
            VulkanPipelineBindSkips = Engine.Rendering.Stats.VulkanPipelineBindSkips,
            VulkanDescriptorBindSkips = Engine.Rendering.Stats.VulkanDescriptorBindSkips,
            VulkanVertexBufferBindSkips = Engine.Rendering.Stats.VulkanVertexBufferBindSkips,
            VulkanIndexBufferBindSkips = Engine.Rendering.Stats.VulkanIndexBufferBindSkips,
            VulkanPipelineCacheLookupHits = Engine.Rendering.Stats.VulkanPipelineCacheLookupHits,
            VulkanPipelineCacheLookupMisses = Engine.Rendering.Stats.VulkanPipelineCacheLookupMisses,
            VulkanPipelineCacheLookupHitRate = Engine.Rendering.Stats.VulkanPipelineCacheLookupHitRate,
            VulkanPipelineCacheMissSummary = Engine.Rendering.Stats.VulkanPipelineCacheMissSummary,
            VulkanFrameWaitFenceMs = Engine.Rendering.Stats.VulkanFrameWaitFenceMs,
            VulkanFrameAcquireImageMs = Engine.Rendering.Stats.VulkanFrameAcquireImageMs,
            VulkanFrameRecordCommandBufferMs = Engine.Rendering.Stats.VulkanFrameRecordCommandBufferMs,
            VulkanFrameSubmitMs = Engine.Rendering.Stats.VulkanFrameSubmitMs,
            VulkanFrameTrimMs = Engine.Rendering.Stats.VulkanFrameTrimMs,
            VulkanFramePresentMs = Engine.Rendering.Stats.VulkanFramePresentMs,
            VulkanFrameTotalMs = Engine.Rendering.Stats.VulkanFrameTotalMs,
            VulkanFrameGpuCommandBufferMs = Engine.Rendering.Stats.VulkanFrameGpuCommandBufferMs,
            VulkanDeviceLocalAllocationCount = Engine.Rendering.Stats.VulkanDeviceLocalAllocationCount,
            VulkanDeviceLocalAllocatedBytes = Engine.Rendering.Stats.VulkanDeviceLocalAllocatedBytes,
            VulkanUploadAllocationCount = Engine.Rendering.Stats.VulkanUploadAllocationCount,
            VulkanUploadAllocatedBytes = Engine.Rendering.Stats.VulkanUploadAllocatedBytes,
            VulkanReadbackAllocationCount = Engine.Rendering.Stats.VulkanReadbackAllocationCount,
            VulkanReadbackAllocatedBytes = Engine.Rendering.Stats.VulkanReadbackAllocatedBytes,
            VulkanDescriptorPoolCreateCount = Engine.Rendering.Stats.VulkanDescriptorPoolCreateCount,
            VulkanDescriptorPoolDestroyCount = Engine.Rendering.Stats.VulkanDescriptorPoolDestroyCount,
            VulkanDescriptorPoolResetCount = Engine.Rendering.Stats.VulkanDescriptorPoolResetCount,
            VulkanQueueSubmitCount = Engine.Rendering.Stats.VulkanQueueSubmitCount,
            VulkanDroppedFrameOps = Engine.Rendering.Stats.VulkanDroppedFrameOps,
            VulkanDroppedDrawOps = Engine.Rendering.Stats.VulkanDroppedDrawOps,
            VulkanDroppedComputeOps = Engine.Rendering.Stats.VulkanDroppedComputeOps,
            VulkanSceneSwapchainWriters = Engine.Rendering.Stats.VulkanSceneSwapchainWriters,
            VulkanOverlaySwapchainWriters = Engine.Rendering.Stats.VulkanOverlaySwapchainWriters,
            VulkanForcedDiagnosticSwapchainWriters = Engine.Rendering.Stats.VulkanForcedDiagnosticSwapchainWriters,
            VulkanFboOnlyDrawOps = Engine.Rendering.Stats.VulkanFboOnlyDrawOps,
            VulkanFboOnlyBlitOps = Engine.Rendering.Stats.VulkanFboOnlyBlitOps,
            VulkanMissingSceneSwapchainWriteFrames = Engine.Rendering.Stats.VulkanMissingSceneSwapchainWriteFrames,
            VulkanFirstFailedFrameOpPassIndex = Engine.Rendering.Stats.VulkanFirstFailedFrameOpPassIndex,
            VulkanFirstFailedFrameOpPipelineIdentity = Engine.Rendering.Stats.VulkanFirstFailedFrameOpPipelineIdentity,
            VulkanFirstFailedFrameOpViewportIdentity = Engine.Rendering.Stats.VulkanFirstFailedFrameOpViewportIdentity,
            VulkanFirstFailedFrameOpType = Engine.Rendering.Stats.VulkanFirstFailedFrameOpType,
            VulkanFirstFailedFrameOpTargetName = Engine.Rendering.Stats.VulkanFirstFailedFrameOpTargetName,
            VulkanFirstFailedFrameOpMaterialName = Engine.Rendering.Stats.VulkanFirstFailedFrameOpMaterialName,
            VulkanFirstFailedFrameOpShaderName = Engine.Rendering.Stats.VulkanFirstFailedFrameOpShaderName,
            VulkanFirstFailedFrameOpMessage = Engine.Rendering.Stats.VulkanFirstFailedFrameOpMessage,
            VulkanFrameDiagnosticSummary = Engine.Rendering.Stats.VulkanFrameDiagnosticSummary,
            VulkanValidationMessageCount = Engine.Rendering.Stats.VulkanValidationMessageCount,
            VulkanValidationErrorCount = Engine.Rendering.Stats.VulkanValidationErrorCount,
            VulkanLastValidationMessage = Engine.Rendering.Stats.VulkanLastValidationMessage,
            VulkanDescriptorFallbackSampledImages = Engine.Rendering.Stats.VulkanDescriptorFallbackSampledImages,
            VulkanDescriptorFallbackStorageImages = Engine.Rendering.Stats.VulkanDescriptorFallbackStorageImages,
            VulkanDescriptorFallbackUniformBuffers = Engine.Rendering.Stats.VulkanDescriptorFallbackUniformBuffers,
            VulkanDescriptorFallbackStorageBuffers = Engine.Rendering.Stats.VulkanDescriptorFallbackStorageBuffers,
            VulkanDescriptorFallbackTexelBuffers = Engine.Rendering.Stats.VulkanDescriptorFallbackTexelBuffers,
            VulkanDescriptorBindingFailures = Engine.Rendering.Stats.VulkanDescriptorBindingFailures,
            VulkanDescriptorSkippedDraws = Engine.Rendering.Stats.VulkanDescriptorSkippedDraws,
            VulkanDescriptorSkippedDispatches = Engine.Rendering.Stats.VulkanDescriptorSkippedDispatches,
            VulkanDescriptorFallbackSummary = Engine.Rendering.Stats.VulkanDescriptorFallbackSummary,
            VulkanDescriptorFailureSummary = Engine.Rendering.Stats.VulkanDescriptorFailureSummary,
            VulkanDynamicUniformAllocations = Engine.Rendering.Stats.VulkanDynamicUniformAllocations,
            VulkanDynamicUniformAllocatedBytes = Engine.Rendering.Stats.VulkanDynamicUniformAllocatedBytes,
            VulkanDynamicUniformExhaustions = Engine.Rendering.Stats.VulkanDynamicUniformExhaustions,
            VulkanRetiredResourcePlanReplacements = Engine.Rendering.Stats.VulkanRetiredResourcePlanReplacements,
            VulkanRetiredResourcePlanImages = Engine.Rendering.Stats.VulkanRetiredResourcePlanImages,
            VulkanRetiredResourcePlanBuffers = Engine.Rendering.Stats.VulkanRetiredResourcePlanBuffers,
            AllocatedVRAMBytes = Engine.Rendering.Stats.AllocatedVRAMBytes,
            AllocatedBufferBytes = Engine.Rendering.Stats.AllocatedBufferBytes,
            AllocatedTextureBytes = Engine.Rendering.Stats.AllocatedTextureBytes,
            AllocatedRenderBufferBytes = Engine.Rendering.Stats.AllocatedRenderBufferBytes,
            FBOBandwidthBytes = Engine.Rendering.Stats.FBOBandwidthBytes,
            FBOBindCount = Engine.Rendering.Stats.FBOBindCount,
            PhysicsChainCpuUploadBytes = physicsChainSnapshot.CpuUploadBytes,
            PhysicsChainGpuCopyBytes = physicsChainSnapshot.GpuCopyBytes,
            PhysicsChainCpuReadbackBytes = physicsChainSnapshot.CpuReadbackBytes,
            PhysicsChainDispatchGroupCount = physicsChainSnapshot.DispatchGroupCount,
            PhysicsChainDispatchIterationCount = physicsChainSnapshot.DispatchIterationCount,
            PhysicsChainResidentParticleBytes = physicsChainSnapshot.ResidentParticleBytes,
            PhysicsChainStandaloneCpuUploadBytes = physicsChainSnapshot.StandaloneCpuUploadBytes,
            PhysicsChainStandaloneCpuReadbackBytes = physicsChainSnapshot.StandaloneCpuReadbackBytes,
            PhysicsChainBatchedCpuUploadBytes = physicsChainSnapshot.BatchedCpuUploadBytes,
            PhysicsChainBatchedGpuCopyBytes = physicsChainSnapshot.BatchedGpuCopyBytes,
            PhysicsChainBatchedCpuReadbackBytes = physicsChainSnapshot.BatchedCpuReadbackBytes,
            PhysicsChainHierarchyRecalcMilliseconds = physicsChainSnapshot.HierarchyRecalcMilliseconds,
            RenderMatrixStatsReady = Engine.Rendering.Stats.RenderMatrixStatsReady,
            RenderMatrixApplied = Engine.Rendering.Stats.RenderMatrixApplied,
            RenderMatrixBatchCount = Engine.Rendering.Stats.RenderMatrixBatchCount,
            RenderMatrixMaxBatchSize = Engine.Rendering.Stats.RenderMatrixMaxBatchSize,
            RenderMatrixSetCalls = Engine.Rendering.Stats.RenderMatrixSetCalls,
            RenderMatrixListenerInvocations = Engine.Rendering.Stats.RenderMatrixListenerInvocations,
            RenderMatrixListenerCounts = listenerEntries,
            SkinnedBoundsStatsReady = Engine.Rendering.Stats.SkinnedBoundsStatsReady,
            SkinnedBoundsDeferredScheduledCount = Engine.Rendering.Stats.SkinnedBoundsDeferredScheduledCount,
            SkinnedBoundsDeferredCompletedCount = Engine.Rendering.Stats.SkinnedBoundsDeferredCompletedCount,
            SkinnedBoundsDeferredFailedCount = Engine.Rendering.Stats.SkinnedBoundsDeferredFailedCount,
            SkinnedBoundsDeferredInFlightCount = Engine.Rendering.Stats.SkinnedBoundsDeferredInFlightCount,
            SkinnedBoundsDeferredMaxInFlightCount = Engine.Rendering.Stats.SkinnedBoundsDeferredMaxInFlightCount,
            SkinnedBoundsDeferredQueueWaitMs = Engine.Rendering.Stats.SkinnedBoundsDeferredQueueWaitMs,
            SkinnedBoundsDeferredCpuJobMs = Engine.Rendering.Stats.SkinnedBoundsDeferredCpuJobMs,
            SkinnedBoundsDeferredApplyMs = Engine.Rendering.Stats.SkinnedBoundsDeferredApplyMs,
            SkinnedBoundsDeferredMaxQueueWaitMs = Engine.Rendering.Stats.SkinnedBoundsDeferredMaxQueueWaitMs,
            SkinnedBoundsDeferredMaxCpuJobMs = Engine.Rendering.Stats.SkinnedBoundsDeferredMaxCpuJobMs,
            SkinnedBoundsDeferredMaxApplyMs = Engine.Rendering.Stats.SkinnedBoundsDeferredMaxApplyMs,
            SkinnedBoundsGpuCompletedCount = Engine.Rendering.Stats.SkinnedBoundsGpuCompletedCount,
            SkinnedBoundsGpuComputeMs = Engine.Rendering.Stats.SkinnedBoundsGpuComputeMs,
            SkinnedBoundsGpuApplyMs = Engine.Rendering.Stats.SkinnedBoundsGpuApplyMs,
            SkinnedBoundsGpuMaxComputeMs = Engine.Rendering.Stats.SkinnedBoundsGpuMaxComputeMs,
            SkinnedBoundsGpuMaxApplyMs = Engine.Rendering.Stats.SkinnedBoundsGpuMaxApplyMs,
            OctreeStatsReady = Engine.Rendering.Stats.OctreeStatsReady,
            OctreeCollectCallCount = Engine.Rendering.Stats.OctreeCollectCallCount,
            OctreeVisibleRenderableCount = Engine.Rendering.Stats.OctreeVisibleRenderableCount,
            OctreeEmittedCommandCount = Engine.Rendering.Stats.OctreeEmittedCommandCount,
            OctreeMaxVisibleRenderablesPerCollect = Engine.Rendering.Stats.OctreeMaxVisibleRenderablesPerCollect,
            OctreeMaxEmittedCommandsPerCollect = Engine.Rendering.Stats.OctreeMaxEmittedCommandsPerCollect,
            OctreeAddCount = Engine.Rendering.Stats.OctreeAddCount,
            OctreeMoveCount = Engine.Rendering.Stats.OctreeMoveCount,
            OctreeRemoveCount = Engine.Rendering.Stats.OctreeRemoveCount,
            OctreeSkippedMoveCount = Engine.Rendering.Stats.OctreeSkippedMoveCount,
            OctreeSwapDrainedCommandCount = Engine.Rendering.Stats.OctreeSwapDrainedCommandCount,
            OctreeSwapBufferedCommandCount = Engine.Rendering.Stats.OctreeSwapBufferedCommandCount,
            OctreeSwapExecutedCommandCount = Engine.Rendering.Stats.OctreeSwapExecutedCommandCount,
            OctreeSwapDrainMs = Engine.Rendering.Stats.OctreeSwapDrainMs,
            OctreeSwapExecuteMs = Engine.Rendering.Stats.OctreeSwapExecuteMs,
            OctreeSwapMaxCommandMs = Engine.Rendering.Stats.OctreeSwapMaxCommandMs,
            OctreeSwapMaxCommandKind = Engine.Rendering.Stats.OctreeSwapMaxCommandKind,
            OctreeRaycastProcessedCommandCount = Engine.Rendering.Stats.OctreeRaycastProcessedCommandCount,
            OctreeRaycastDroppedCommandCount = Engine.Rendering.Stats.OctreeRaycastDroppedCommandCount,
            OctreeRaycastTraversalMs = Engine.Rendering.Stats.OctreeRaycastTraversalMs,
            OctreeRaycastCallbackMs = Engine.Rendering.Stats.OctreeRaycastCallbackMs,
            OctreeRaycastMaxTraversalMs = Engine.Rendering.Stats.OctreeRaycastMaxTraversalMs,
            OctreeRaycastMaxCallbackMs = Engine.Rendering.Stats.OctreeRaycastMaxCallbackMs,
            OctreeRaycastMaxCommandMs = Engine.Rendering.Stats.OctreeRaycastMaxCommandMs,
            GpuRenderPipelineProfilingEnabled = Engine.Rendering.Stats.GpuRenderPipelineProfilingEnabled,
            GpuRenderPipelineProfilingSupported = Engine.Rendering.Stats.GpuRenderPipelineProfilingSupported,
            GpuRenderPipelineTimingsReady = Engine.Rendering.Stats.GpuRenderPipelineTimingsReady,
            GpuRenderPipelineBackend = Engine.Rendering.Stats.GpuRenderPipelineBackend,
            GpuRenderPipelineStatusMessage = Engine.Rendering.Stats.GpuRenderPipelineStatusMessage,
            GpuRenderPipelineFrameMs = Engine.Rendering.Stats.GpuRenderPipelineFrameMs,
            GpuRenderPipelineTimingRoots = Engine.Rendering.Stats.GetGpuRenderPipelineTimingRoots(),
        };
    }

    private static ThreadAllocationsPacket? CollectThreadAllocations()
    {
        var snap = Engine.Allocations.GetSnapshot();
        return new ThreadAllocationsPacket
        {
            Render = ToSlice(snap.Render),
            CollectSwap = ToSlice(snap.CollectSwap),
            Update = ToSlice(snap.Update),
            FixedUpdate = ToSlice(snap.FixedUpdate),
        };
    }

    private static AllocationSlice ToSlice(Engine.AllocationRingSnapshot ring)
        => new()
        {
            LastBytes = ring.LastBytes,
            AverageBytes = ring.AverageBytes,
            MaxBytes = ring.MaxBytes,
            Samples = ring.Samples,
            Capacity = ring.Capacity,
        };

    private static BvhMetricsPacket? CollectBvhMetrics()
    {
        var m = Engine.Rendering.BvhStats.Latest;
        return new BvhMetricsPacket
        {
            BuildCount = m.BuildCount,
            BuildMilliseconds = m.BuildMilliseconds,
            RefitCount = m.RefitCount,
            RefitMilliseconds = m.RefitMilliseconds,
            CullCount = m.CullCount,
            CullMilliseconds = m.CullMilliseconds,
            RaycastCount = m.RaycastCount,
            RaycastMilliseconds = m.RaycastMilliseconds,
        };
    }

    private static JobSystemStatsPacket? CollectJobSystemStats()
    {
        var jobs = Engine.Jobs;
        const int priorityCount = (int)JobPriority.Highest + 1;
        var priorities = new JobPriorityStatsEntry[priorityCount];

        for (int i = 0; i < priorityCount; i++)
        {
            var p = (JobPriority)i;
            priorities[i] = new JobPriorityStatsEntry
            {
                Priority = i,
                PriorityName = p.ToString(),
                QueuedAny = jobs.GetQueuedCount(p, JobAffinity.Any),
                QueuedMain = jobs.GetQueuedCount(p, JobAffinity.MainThread),
                QueuedCollect = jobs.GetQueuedCount(p, JobAffinity.CollectVisibleSwap),
                AvgWaitMs = jobs.GetAverageWait(p).TotalMilliseconds,
            };
        }

        return new JobSystemStatsPacket
        {
            WorkerCount = jobs.WorkerCount,
            IsQueueBounded = jobs.IsQueueBounded,
            QueueCapacity = jobs.QueueCapacity,
            QueueSlotsInUse = jobs.QueueSlotsInUse,
            QueueSlotsAvailable = jobs.QueueSlotsAvailable,
            Priorities = priorities,
        };
    }

    private static MainThreadInvokesPacket? CollectMainThreadInvokes()
    {
        var invokes = Engine.GetMainThreadInvokeLogSnapshot();
        if (invokes.Count == 0)
            return null;

        var entries = new MainThreadInvokeEntryData[invokes.Count];
        for (int i = 0; i < invokes.Count; i++)
        {
            var e = invokes[i];
            entries[i] = new MainThreadInvokeEntryData
            {
                Sequence = e.Sequence,
                TimestampTicks = e.Timestamp.Ticks,
                Reason = e.Reason,
                Mode = e.Mode.ToString(),
                CallerThreadId = e.CallerThreadId,
            };
        }

        return new MainThreadInvokesPacket { Entries = entries };
    }
}
