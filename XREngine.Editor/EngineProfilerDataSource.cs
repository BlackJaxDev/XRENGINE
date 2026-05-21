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
                ScopeKind = n.ScopeKind,
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
        var listenerSnapshot = Engine.Rendering.Stats.RenderMatrix.GetRenderMatrixListenerSnapshot();
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
            DrawCalls = Engine.Rendering.Stats.Frame.DrawCalls,
            MultiDrawCalls = Engine.Rendering.Stats.Frame.MultiDrawCalls,
            TrianglesRendered = Engine.Rendering.Stats.Frame.TrianglesRendered,
            GpuCpuFallbackEvents = Engine.Rendering.Stats.GpuFallback.GpuCpuFallbackEvents,
            GpuCpuFallbackRecoveredCommands = Engine.Rendering.Stats.GpuFallback.GpuCpuFallbackRecoveredCommands,
            GpuTransparencyOpaqueOrOtherVisible = Engine.Rendering.Stats.GpuTransparency.GpuTransparencyOpaqueOrOtherVisible,
            GpuTransparencyMaskedVisible = Engine.Rendering.Stats.GpuTransparency.GpuTransparencyMaskedVisible,
            GpuTransparencyApproximateVisible = Engine.Rendering.Stats.GpuTransparency.GpuTransparencyApproximateVisible,
            GpuTransparencyExactVisible = Engine.Rendering.Stats.GpuTransparency.GpuTransparencyExactVisible,
            GpuMeshletRequestedFrames = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletRequestedFrames,
            GpuMeshletProductionFrames = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletProductionFrames,
            GpuMeshletFallbackFrames = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletFallbackFrames,
            GpuMeshletDispatchSkipped = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletDispatchSkipped,
            GpuMeshletTaskRecordsEmitted = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsEmitted,
            GpuMeshletTaskRecordsFrustumCulled = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsFrustumCulled,
            GpuMeshletTaskRecordsConeCulled = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsConeCulled,
            GpuMeshletTaskRecordsHiZCulled = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsHiZCulled,
            GpuMeshletExpansionOverflowCount = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletExpansionOverflowCount,
            GpuMeshletBufferBytesResident = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletBufferBytesResident,
            GpuMeshletLastVisibleMeshletCount = Engine.Rendering.Stats.GpuMeshlets.LastVisibleMeshletCount,
            GpuMeshletLastDispatchedMeshletCount = Engine.Rendering.Stats.GpuMeshlets.LastDispatchedMeshletCount,
            GpuMeshletLastTaskRecordOverflowCount = Engine.Rendering.Stats.GpuMeshlets.LastTaskRecordOverflowCount,
            GpuMeshletLastDispatchMs = Engine.Rendering.Stats.GpuMeshlets.LastDispatchTime.TotalMilliseconds,
            GpuMeshletLastReadbackBytes = Engine.Rendering.Stats.GpuMeshlets.LastReadbackBytes,
            GpuMeshletCacheHits = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheHits,
            GpuMeshletCacheMisses = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheMisses,
            GpuMeshletCacheStale = Engine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheStale,
            VulkanPipelineBinds = Engine.Rendering.Stats.Vulkan.VulkanPipelineBinds,
            VulkanDescriptorBinds = Engine.Rendering.Stats.Vulkan.VulkanDescriptorBinds,
            VulkanPushConstantWrites = Engine.Rendering.Stats.Vulkan.VulkanPushConstantWrites,
            VulkanVertexBufferBinds = Engine.Rendering.Stats.Vulkan.VulkanVertexBufferBinds,
            VulkanIndexBufferBinds = Engine.Rendering.Stats.Vulkan.VulkanIndexBufferBinds,
            VulkanPipelineBindSkips = Engine.Rendering.Stats.Vulkan.VulkanPipelineBindSkips,
            VulkanDescriptorBindSkips = Engine.Rendering.Stats.Vulkan.VulkanDescriptorBindSkips,
            VulkanVertexBufferBindSkips = Engine.Rendering.Stats.Vulkan.VulkanVertexBufferBindSkips,
            VulkanIndexBufferBindSkips = Engine.Rendering.Stats.Vulkan.VulkanIndexBufferBindSkips,
            VulkanPipelineCacheLookupHits = Engine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupHits,
            VulkanPipelineCacheLookupMisses = Engine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupMisses,
            VulkanPipelineCacheLookupHitRate = Engine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupHitRate,
            VulkanPipelineCacheMissSummary = Engine.Rendering.Stats.Vulkan.VulkanPipelineCacheMissSummary,
            VulkanFrameWaitFenceMs = Engine.Rendering.Stats.Vulkan.VulkanFrameWaitFenceMs,
            VulkanFrameAcquireImageMs = Engine.Rendering.Stats.Vulkan.VulkanFrameAcquireImageMs,
            VulkanFrameRecordCommandBufferMs = Engine.Rendering.Stats.Vulkan.VulkanFrameRecordCommandBufferMs,
            VulkanFrameSubmitMs = Engine.Rendering.Stats.Vulkan.VulkanFrameSubmitMs,
            VulkanFrameTrimMs = Engine.Rendering.Stats.Vulkan.VulkanFrameTrimMs,
            VulkanFramePresentMs = Engine.Rendering.Stats.Vulkan.VulkanFramePresentMs,
            VulkanFrameTotalMs = Engine.Rendering.Stats.Vulkan.VulkanFrameTotalMs,
            VulkanFrameGpuCommandBufferMs = Engine.Rendering.Stats.Vulkan.VulkanFrameGpuCommandBufferMs,
            VulkanDeviceLocalAllocationCount = Engine.Rendering.Stats.Vulkan.VulkanDeviceLocalAllocationCount,
            VulkanDeviceLocalAllocatedBytes = Engine.Rendering.Stats.Vulkan.VulkanDeviceLocalAllocatedBytes,
            VulkanUploadAllocationCount = Engine.Rendering.Stats.Vulkan.VulkanUploadAllocationCount,
            VulkanUploadAllocatedBytes = Engine.Rendering.Stats.Vulkan.VulkanUploadAllocatedBytes,
            VulkanReadbackAllocationCount = Engine.Rendering.Stats.Vulkan.VulkanReadbackAllocationCount,
            VulkanReadbackAllocatedBytes = Engine.Rendering.Stats.Vulkan.VulkanReadbackAllocatedBytes,
            VulkanDescriptorPoolCreateCount = Engine.Rendering.Stats.Vulkan.VulkanDescriptorPoolCreateCount,
            VulkanDescriptorPoolDestroyCount = Engine.Rendering.Stats.Vulkan.VulkanDescriptorPoolDestroyCount,
            VulkanDescriptorPoolResetCount = Engine.Rendering.Stats.Vulkan.VulkanDescriptorPoolResetCount,
            VulkanQueueSubmitCount = Engine.Rendering.Stats.Vulkan.VulkanQueueSubmitCount,
            VulkanDroppedFrameOps = Engine.Rendering.Stats.Vulkan.VulkanDroppedFrameOps,
            VulkanDroppedDrawOps = Engine.Rendering.Stats.Vulkan.VulkanDroppedDrawOps,
            VulkanDroppedComputeOps = Engine.Rendering.Stats.Vulkan.VulkanDroppedComputeOps,
            VulkanSceneSwapchainWriters = Engine.Rendering.Stats.Vulkan.VulkanSceneSwapchainWriters,
            VulkanOverlaySwapchainWriters = Engine.Rendering.Stats.Vulkan.VulkanOverlaySwapchainWriters,
            VulkanForcedDiagnosticSwapchainWriters = Engine.Rendering.Stats.Vulkan.VulkanForcedDiagnosticSwapchainWriters,
            VulkanFboOnlyDrawOps = Engine.Rendering.Stats.Vulkan.VulkanFboOnlyDrawOps,
            VulkanFboOnlyBlitOps = Engine.Rendering.Stats.Vulkan.VulkanFboOnlyBlitOps,
            VulkanMissingSceneSwapchainWriteFrames = Engine.Rendering.Stats.Vulkan.VulkanMissingSceneSwapchainWriteFrames,
            VulkanFirstFailedFrameOpPassIndex = Engine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpPassIndex,
            VulkanFirstFailedFrameOpPipelineIdentity = Engine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpPipelineIdentity,
            VulkanFirstFailedFrameOpViewportIdentity = Engine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpViewportIdentity,
            VulkanFirstFailedFrameOpType = Engine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpType,
            VulkanFirstFailedFrameOpTargetName = Engine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpTargetName,
            VulkanFirstFailedFrameOpMaterialName = Engine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpMaterialName,
            VulkanFirstFailedFrameOpShaderName = Engine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpShaderName,
            VulkanFirstFailedFrameOpMessage = Engine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpMessage,
            VulkanFrameDiagnosticSummary = Engine.Rendering.Stats.Vulkan.VulkanFrameDiagnosticSummary,
            VulkanValidationMessageCount = Engine.Rendering.Stats.Vulkan.VulkanValidationMessageCount,
            VulkanValidationErrorCount = Engine.Rendering.Stats.Vulkan.VulkanValidationErrorCount,
            VulkanLastValidationMessage = Engine.Rendering.Stats.Vulkan.VulkanLastValidationMessage,
            VulkanDescriptorFallbackSampledImages = Engine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackSampledImages,
            VulkanDescriptorFallbackStorageImages = Engine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackStorageImages,
            VulkanDescriptorFallbackUniformBuffers = Engine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackUniformBuffers,
            VulkanDescriptorFallbackStorageBuffers = Engine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackStorageBuffers,
            VulkanDescriptorFallbackTexelBuffers = Engine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackTexelBuffers,
            VulkanDescriptorBindingFailures = Engine.Rendering.Stats.Vulkan.VulkanDescriptorBindingFailures,
            VulkanDescriptorSkippedDraws = Engine.Rendering.Stats.Vulkan.VulkanDescriptorSkippedDraws,
            VulkanDescriptorSkippedDispatches = Engine.Rendering.Stats.Vulkan.VulkanDescriptorSkippedDispatches,
            VulkanDescriptorFallbackSummary = Engine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackSummary,
            VulkanDescriptorFailureSummary = Engine.Rendering.Stats.Vulkan.VulkanDescriptorFailureSummary,
            VulkanDynamicUniformAllocations = Engine.Rendering.Stats.Vulkan.VulkanDynamicUniformAllocations,
            VulkanDynamicUniformAllocatedBytes = Engine.Rendering.Stats.Vulkan.VulkanDynamicUniformAllocatedBytes,
            VulkanDynamicUniformExhaustions = Engine.Rendering.Stats.Vulkan.VulkanDynamicUniformExhaustions,
            VulkanRetiredResourcePlanReplacements = Engine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanReplacements,
            VulkanRetiredResourcePlanImages = Engine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanImages,
            VulkanRetiredResourcePlanBuffers = Engine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanBuffers,
            AllocatedVRAMBytes = Engine.Rendering.Stats.Vram.AllocatedVRAMBytes,
            AllocatedBufferBytes = Engine.Rendering.Stats.Vram.AllocatedBufferBytes,
            AllocatedTextureBytes = Engine.Rendering.Stats.Vram.AllocatedTextureBytes,
            AllocatedRenderBufferBytes = Engine.Rendering.Stats.Vram.AllocatedRenderBufferBytes,
            FBOBandwidthBytes = Engine.Rendering.Stats.Vram.FBOBandwidthBytes,
            FBOBindCount = Engine.Rendering.Stats.Vram.FBOBindCount,
            VrLeftEyeDraws = Engine.Rendering.Stats.Vr.VrLeftEyeDraws,
            VrRightEyeDraws = Engine.Rendering.Stats.Vr.VrRightEyeDraws,
            VrLeftEyeVisible = Engine.Rendering.Stats.Vr.VrLeftEyeVisible,
            VrRightEyeVisible = Engine.Rendering.Stats.Vr.VrRightEyeVisible,
            VrLeftWorkerBuildTimeMs = Engine.Rendering.Stats.Vr.VrLeftWorkerBuildTimeMs,
            VrRightWorkerBuildTimeMs = Engine.Rendering.Stats.Vr.VrRightWorkerBuildTimeMs,
            VrRenderSubmitTimeMs = Engine.Rendering.Stats.Vr.VrRenderSubmitTimeMs,
            VrXrWaitFrameBlockTimeMs = Engine.Rendering.Stats.Vr.VrXrWaitFrameBlockTimeMs,
            VrXrEndFrameSubmitTimeMs = Engine.Rendering.Stats.Vr.VrXrEndFrameSubmitTimeMs,
            VrXrPredictedToLatePoseDeltaMillimeters = Engine.Rendering.Stats.Vr.VrXrPredictedToLatePoseDeltaMillimeters,
            VrXrPredictedToLatePoseDeltaDegrees = Engine.Rendering.Stats.Vr.VrXrPredictedToLatePoseDeltaDegrees,
            VrXrPredictedDisplayLeadTimeMs = Engine.Rendering.Stats.Vr.VrXrPredictedDisplayLeadTimeMs,
            VrXrMissedDeadlineFrames = Engine.Rendering.Stats.Vr.VrXrMissedDeadlineFrames,
            VrXrTrackingLossFrames = Engine.Rendering.Stats.Vr.VrXrTrackingLossFrames,
            VrXrRelocatePredictedTimeMs = Engine.Rendering.Stats.Vr.VrXrRelocatePredictedTimeMs,
            VrXrCollectFrustumExpansionDegrees = Engine.Rendering.Stats.Vr.VrXrCollectFrustumExpansionDegrees,
            VrXrPacingThreadIdleTimeMs = Engine.Rendering.Stats.Vr.VrXrPacingThreadIdleTimeMs,
            VrXrPacingHandoffStalls = Engine.Rendering.Stats.Vr.VrXrPacingHandoffStalls,
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
            RenderMatrixStatsReady = Engine.Rendering.Stats.RenderMatrix.RenderMatrixStatsReady,
            RenderMatrixApplied = Engine.Rendering.Stats.RenderMatrix.RenderMatrixApplied,
            RenderMatrixBatchCount = Engine.Rendering.Stats.RenderMatrix.RenderMatrixBatchCount,
            RenderMatrixMaxBatchSize = Engine.Rendering.Stats.RenderMatrix.RenderMatrixMaxBatchSize,
            RenderMatrixSetCalls = Engine.Rendering.Stats.RenderMatrix.RenderMatrixSetCalls,
            RenderMatrixListenerInvocations = Engine.Rendering.Stats.RenderMatrix.RenderMatrixListenerInvocations,
            RenderMatrixListenerCounts = listenerEntries,
            SkinnedBoundsStatsReady = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsStatsReady,
            SkinnedBoundsDeferredScheduledCount = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredScheduledCount,
            SkinnedBoundsDeferredCompletedCount = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCompletedCount,
            SkinnedBoundsDeferredFailedCount = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredFailedCount,
            SkinnedBoundsDeferredInFlightCount = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredInFlightCount,
            SkinnedBoundsDeferredMaxInFlightCount = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxInFlightCount,
            SkinnedBoundsDeferredQueueWaitMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredQueueWaitMs,
            SkinnedBoundsDeferredCpuJobMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCpuJobMs,
            SkinnedBoundsDeferredApplyMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredApplyMs,
            SkinnedBoundsDeferredMaxQueueWaitMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxQueueWaitMs,
            SkinnedBoundsDeferredMaxCpuJobMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxCpuJobMs,
            SkinnedBoundsDeferredMaxApplyMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxApplyMs,
            SkinnedBoundsGpuCompletedCount = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuCompletedCount,
            SkinnedBoundsGpuComputeMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuComputeMs,
            SkinnedBoundsGpuApplyMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuApplyMs,
            SkinnedBoundsGpuMaxComputeMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuMaxComputeMs,
            SkinnedBoundsGpuMaxApplyMs = Engine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuMaxApplyMs,
            OctreeStatsReady = Engine.Rendering.Stats.Octree.OctreeStatsReady,
            OctreeCollectCallCount = Engine.Rendering.Stats.Octree.OctreeCollectCallCount,
            OctreeVisibleRenderableCount = Engine.Rendering.Stats.Octree.OctreeVisibleRenderableCount,
            OctreeEmittedCommandCount = Engine.Rendering.Stats.Octree.OctreeEmittedCommandCount,
            OctreeMaxVisibleRenderablesPerCollect = Engine.Rendering.Stats.Octree.OctreeMaxVisibleRenderablesPerCollect,
            OctreeMaxEmittedCommandsPerCollect = Engine.Rendering.Stats.Octree.OctreeMaxEmittedCommandsPerCollect,
            OctreeAddCount = Engine.Rendering.Stats.Octree.OctreeAddCount,
            OctreeMoveCount = Engine.Rendering.Stats.Octree.OctreeMoveCount,
            OctreeRemoveCount = Engine.Rendering.Stats.Octree.OctreeRemoveCount,
            OctreeSkippedMoveCount = Engine.Rendering.Stats.Octree.OctreeSkippedMoveCount,
            OctreeSwapDrainedCommandCount = Engine.Rendering.Stats.Octree.OctreeSwapDrainedCommandCount,
            OctreeSwapBufferedCommandCount = Engine.Rendering.Stats.Octree.OctreeSwapBufferedCommandCount,
            OctreeSwapExecutedCommandCount = Engine.Rendering.Stats.Octree.OctreeSwapExecutedCommandCount,
            OctreeSwapDrainMs = Engine.Rendering.Stats.Octree.OctreeSwapDrainMs,
            OctreeSwapExecuteMs = Engine.Rendering.Stats.Octree.OctreeSwapExecuteMs,
            OctreeSwapMaxCommandMs = Engine.Rendering.Stats.Octree.OctreeSwapMaxCommandMs,
            OctreeSwapMaxCommandKind = Engine.Rendering.Stats.Octree.OctreeSwapMaxCommandKind,
            OctreeRaycastProcessedCommandCount = Engine.Rendering.Stats.Octree.OctreeRaycastProcessedCommandCount,
            OctreeRaycastDroppedCommandCount = Engine.Rendering.Stats.Octree.OctreeRaycastDroppedCommandCount,
            OctreeRaycastTraversalMs = Engine.Rendering.Stats.Octree.OctreeRaycastTraversalMs,
            OctreeRaycastCallbackMs = Engine.Rendering.Stats.Octree.OctreeRaycastCallbackMs,
            OctreeRaycastMaxTraversalMs = Engine.Rendering.Stats.Octree.OctreeRaycastMaxTraversalMs,
            OctreeRaycastMaxCallbackMs = Engine.Rendering.Stats.Octree.OctreeRaycastMaxCallbackMs,
            OctreeRaycastMaxCommandMs = Engine.Rendering.Stats.Octree.OctreeRaycastMaxCommandMs,
            CpuSpatialTreeMode = Engine.Rendering.Stats.Octree.CpuSpatialTreeMode,
            CpuSpatialTreeNodeCount = Engine.Rendering.Stats.Octree.CpuSpatialTreeNodeCount,
            CpuSpatialTreeItemCount = Engine.Rendering.Stats.Octree.CpuSpatialTreeItemCount,
            CpuSpatialTreeRootItemCount = Engine.Rendering.Stats.Octree.CpuSpatialTreeRootItemCount,
            CpuSpatialTreeMaxNodeItemCount = Engine.Rendering.Stats.Octree.CpuSpatialTreeMaxNodeItemCount,
            CpuSpatialTreeMaxDepth = Engine.Rendering.Stats.Octree.CpuSpatialTreeMaxDepth,
            CpuSpatialTreeUnboundedItemCount = Engine.Rendering.Stats.Octree.CpuSpatialTreeUnboundedItemCount,
            CpuSpatialTreeCollectMs = Engine.Rendering.Stats.Octree.CpuSpatialTreeCollectMs,
            CpuSpatialTreeMaxCollectMs = Engine.Rendering.Stats.Octree.CpuSpatialTreeMaxCollectMs,
            GpuRenderPipelineProfilingEnabled = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingEnabled,
            GpuRenderPipelineProfilingSupported = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingSupported,
            GpuRenderPipelineTimingsReady = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady,
            GpuRenderPipelineBackend = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineBackend,
            GpuRenderPipelineStatusMessage = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineStatusMessage,
            GpuRenderPipelineFrameMs = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs,
            GpuRenderPipelineTimingRoots = Engine.Rendering.Stats.GpuPipelineProfiler.GetGpuRenderPipelineTimingRoots(),
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
