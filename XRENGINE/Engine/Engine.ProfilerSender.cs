using XREngine.Data.Profiling;

namespace XREngine;

public static partial class Engine
{
#if !XRE_PUBLISHED
    /// <summary>
    /// Wires up the delegate-based collectors on <see cref="UdpProfilerSender"/>
    /// so it can read engine stats without a direct assembly reference.
    /// Safe to call multiple times — just overwrites the delegates.
    /// </summary>
    internal static void WireProfilerSenderCollectors()
    {
        UdpProfilerSender.CollectProfilerFrame = CollectProfilerFrame;
        UdpProfilerSender.CollectRenderStats = CollectRenderStats;
        UdpProfilerSender.CollectThreadAllocations = CollectThreadAllocations;
        UdpProfilerSender.CollectBvhMetrics = CollectBvhMetrics;
        UdpProfilerSender.CollectJobSystemStats = CollectJobSystemStats;
        UdpProfilerSender.CollectMainThreadInvokes = CollectMainThreadInvokes;
    }

    private static ProfilerFramePacket? CollectProfilerFrame()
    {
        if (!Profiler.TryGetSnapshot(out var snapshot, out var history) || snapshot is null)
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

    private static ProfilerNodeData[] ConvertNodes(IReadOnlyList<CodeProfiler.ProfilerNodeSnapshot> nodes)
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

    private static ProfilerComponentTimingData[] ConvertComponentTimings(IReadOnlyList<CodeProfiler.ProfilerComponentTimingSnapshot>? components)
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
        var listenerSnapshot = Rendering.Stats.RenderMatrix.GetRenderMatrixListenerSnapshot();
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
            DrawCalls = Rendering.Stats.Frame.DrawCalls,
            MultiDrawCalls = Rendering.Stats.Frame.MultiDrawCalls,
            TrianglesRendered = Rendering.Stats.Frame.TrianglesRendered,
            GpuCpuFallbackEvents = Rendering.Stats.GpuFallback.GpuCpuFallbackEvents,
            GpuCpuFallbackRecoveredCommands = Rendering.Stats.GpuFallback.GpuCpuFallbackRecoveredCommands,
            ForbiddenGpuFallbackEvents = Rendering.Stats.GpuFallback.ForbiddenGpuFallbackEvents,
            GpuMappedBuffers = Rendering.Stats.GpuReadback.GpuMappedBuffers,
            GpuReadbackBytes = Rendering.Stats.GpuReadback.GpuReadbackBytes,
            GpuTransparencyOpaqueOrOtherVisible = Rendering.Stats.GpuTransparency.GpuTransparencyOpaqueOrOtherVisible,
            GpuTransparencyMaskedVisible = Rendering.Stats.GpuTransparency.GpuTransparencyMaskedVisible,
            GpuTransparencyApproximateVisible = Rendering.Stats.GpuTransparency.GpuTransparencyApproximateVisible,
            GpuTransparencyExactVisible = Rendering.Stats.GpuTransparency.GpuTransparencyExactVisible,
            GpuMeshletRequestedFrames = Rendering.Stats.GpuMeshlets.GpuMeshletRequestedFrames,
            GpuMeshletProductionFrames = Rendering.Stats.GpuMeshlets.GpuMeshletProductionFrames,
            GpuMeshletFallbackFrames = Rendering.Stats.GpuMeshlets.GpuMeshletFallbackFrames,
            GpuMeshletDispatchSkipped = Rendering.Stats.GpuMeshlets.GpuMeshletDispatchSkipped,
            GpuMeshletTaskRecordsEmitted = Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsEmitted,
            GpuMeshletTaskRecordsFrustumCulled = Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsFrustumCulled,
            GpuMeshletTaskRecordsConeCulled = Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsConeCulled,
            GpuMeshletTaskRecordsHiZCulled = Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsHiZCulled,
            GpuMeshletExpansionOverflowCount = Rendering.Stats.GpuMeshlets.GpuMeshletExpansionOverflowCount,
            GpuMeshletBufferBytesResident = Rendering.Stats.GpuMeshlets.GpuMeshletBufferBytesResident,
            GpuMeshletCacheHits = Rendering.Stats.GpuMeshlets.GpuMeshletCacheHits,
            GpuMeshletCacheMisses = Rendering.Stats.GpuMeshlets.GpuMeshletCacheMisses,
            GpuMeshletCacheStale = Rendering.Stats.GpuMeshlets.GpuMeshletCacheStale,
            VulkanPipelineBinds = Rendering.Stats.Vulkan.VulkanPipelineBinds,
            VulkanDescriptorBinds = Rendering.Stats.Vulkan.VulkanDescriptorBinds,
            VulkanPushConstantWrites = Rendering.Stats.Vulkan.VulkanPushConstantWrites,
            VulkanVertexBufferBinds = Rendering.Stats.Vulkan.VulkanVertexBufferBinds,
            VulkanIndexBufferBinds = Rendering.Stats.Vulkan.VulkanIndexBufferBinds,
            VulkanPipelineBindSkips = Rendering.Stats.Vulkan.VulkanPipelineBindSkips,
            VulkanDescriptorBindSkips = Rendering.Stats.Vulkan.VulkanDescriptorBindSkips,
            VulkanVertexBufferBindSkips = Rendering.Stats.Vulkan.VulkanVertexBufferBindSkips,
            VulkanIndexBufferBindSkips = Rendering.Stats.Vulkan.VulkanIndexBufferBindSkips,
            VulkanPipelineCacheLookupHits = Rendering.Stats.Vulkan.VulkanPipelineCacheLookupHits,
            VulkanPipelineCacheLookupMisses = Rendering.Stats.Vulkan.VulkanPipelineCacheLookupMisses,
            VulkanPipelineCacheLookupHitRate = Rendering.Stats.Vulkan.VulkanPipelineCacheLookupHitRate,
            VulkanPipelineCacheMissSummary = Rendering.Stats.Vulkan.VulkanPipelineCacheMissSummary,
            VulkanFrameWaitFenceMs = Rendering.Stats.Vulkan.VulkanFrameWaitFenceMs,
            VulkanFrameAcquireImageMs = Rendering.Stats.Vulkan.VulkanFrameAcquireImageMs,
            VulkanFrameRecordCommandBufferMs = Rendering.Stats.Vulkan.VulkanFrameRecordCommandBufferMs,
            VulkanFrameSubmitMs = Rendering.Stats.Vulkan.VulkanFrameSubmitMs,
            VulkanFrameTrimMs = Rendering.Stats.Vulkan.VulkanFrameTrimMs,
            VulkanFramePresentMs = Rendering.Stats.Vulkan.VulkanFramePresentMs,
            VulkanFrameTotalMs = Rendering.Stats.Vulkan.VulkanFrameTotalMs,
            VulkanFrameGpuCommandBufferMs = Rendering.Stats.Vulkan.VulkanFrameGpuCommandBufferMs,
            VulkanDeviceLocalAllocationCount = Rendering.Stats.Vulkan.VulkanDeviceLocalAllocationCount,
            VulkanDeviceLocalAllocatedBytes = Rendering.Stats.Vulkan.VulkanDeviceLocalAllocatedBytes,
            VulkanUploadAllocationCount = Rendering.Stats.Vulkan.VulkanUploadAllocationCount,
            VulkanUploadAllocatedBytes = Rendering.Stats.Vulkan.VulkanUploadAllocatedBytes,
            VulkanReadbackAllocationCount = Rendering.Stats.Vulkan.VulkanReadbackAllocationCount,
            VulkanReadbackAllocatedBytes = Rendering.Stats.Vulkan.VulkanReadbackAllocatedBytes,
            VulkanDescriptorPoolCreateCount = Rendering.Stats.Vulkan.VulkanDescriptorPoolCreateCount,
            VulkanDescriptorPoolDestroyCount = Rendering.Stats.Vulkan.VulkanDescriptorPoolDestroyCount,
            VulkanDescriptorPoolResetCount = Rendering.Stats.Vulkan.VulkanDescriptorPoolResetCount,
            VulkanQueueSubmitCount = Rendering.Stats.Vulkan.VulkanQueueSubmitCount,
            VulkanDroppedFrameOps = Rendering.Stats.Vulkan.VulkanDroppedFrameOps,
            VulkanDroppedDrawOps = Rendering.Stats.Vulkan.VulkanDroppedDrawOps,
            VulkanDroppedComputeOps = Rendering.Stats.Vulkan.VulkanDroppedComputeOps,
            VulkanSceneSwapchainWriters = Rendering.Stats.Vulkan.VulkanSceneSwapchainWriters,
            VulkanOverlaySwapchainWriters = Rendering.Stats.Vulkan.VulkanOverlaySwapchainWriters,
            VulkanForcedDiagnosticSwapchainWriters = Rendering.Stats.Vulkan.VulkanForcedDiagnosticSwapchainWriters,
            VulkanFboOnlyDrawOps = Rendering.Stats.Vulkan.VulkanFboOnlyDrawOps,
            VulkanFboOnlyBlitOps = Rendering.Stats.Vulkan.VulkanFboOnlyBlitOps,
            VulkanMissingSceneSwapchainWriteFrames = Rendering.Stats.Vulkan.VulkanMissingSceneSwapchainWriteFrames,
            VulkanFirstFailedFrameOpPassIndex = Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpPassIndex,
            VulkanFirstFailedFrameOpPipelineIdentity = Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpPipelineIdentity,
            VulkanFirstFailedFrameOpViewportIdentity = Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpViewportIdentity,
            VulkanFirstFailedFrameOpType = Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpType,
            VulkanFirstFailedFrameOpTargetName = Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpTargetName,
            VulkanFirstFailedFrameOpMaterialName = Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpMaterialName,
            VulkanFirstFailedFrameOpShaderName = Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpShaderName,
            VulkanFirstFailedFrameOpMessage = Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpMessage,
            VulkanFrameDiagnosticSummary = Rendering.Stats.Vulkan.VulkanFrameDiagnosticSummary,
            VulkanValidationMessageCount = Rendering.Stats.Vulkan.VulkanValidationMessageCount,
            VulkanValidationErrorCount = Rendering.Stats.Vulkan.VulkanValidationErrorCount,
            VulkanLastValidationMessage = Rendering.Stats.Vulkan.VulkanLastValidationMessage,
            VulkanDescriptorFallbackSampledImages = Rendering.Stats.Vulkan.VulkanDescriptorFallbackSampledImages,
            VulkanDescriptorFallbackStorageImages = Rendering.Stats.Vulkan.VulkanDescriptorFallbackStorageImages,
            VulkanDescriptorFallbackUniformBuffers = Rendering.Stats.Vulkan.VulkanDescriptorFallbackUniformBuffers,
            VulkanDescriptorFallbackStorageBuffers = Rendering.Stats.Vulkan.VulkanDescriptorFallbackStorageBuffers,
            VulkanDescriptorFallbackTexelBuffers = Rendering.Stats.Vulkan.VulkanDescriptorFallbackTexelBuffers,
            VulkanDescriptorBindingFailures = Rendering.Stats.Vulkan.VulkanDescriptorBindingFailures,
            VulkanDescriptorSkippedDraws = Rendering.Stats.Vulkan.VulkanDescriptorSkippedDraws,
            VulkanDescriptorSkippedDispatches = Rendering.Stats.Vulkan.VulkanDescriptorSkippedDispatches,
            VulkanDescriptorFallbackSummary = Rendering.Stats.Vulkan.VulkanDescriptorFallbackSummary,
            VulkanDescriptorFailureSummary = Rendering.Stats.Vulkan.VulkanDescriptorFailureSummary,
            VulkanDynamicUniformAllocations = Rendering.Stats.Vulkan.VulkanDynamicUniformAllocations,
            VulkanDynamicUniformAllocatedBytes = Rendering.Stats.Vulkan.VulkanDynamicUniformAllocatedBytes,
            VulkanDynamicUniformExhaustions = Rendering.Stats.Vulkan.VulkanDynamicUniformExhaustions,
            VulkanRetiredResourcePlanReplacements = Rendering.Stats.Vulkan.VulkanRetiredResourcePlanReplacements,
            VulkanRetiredResourcePlanImages = Rendering.Stats.Vulkan.VulkanRetiredResourcePlanImages,
            VulkanRetiredResourcePlanBuffers = Rendering.Stats.Vulkan.VulkanRetiredResourcePlanBuffers,
            AllocatedVRAMBytes = Rendering.Stats.Vram.AllocatedVRAMBytes,
            AllocatedBufferBytes = Rendering.Stats.Vram.AllocatedBufferBytes,
            AllocatedTextureBytes = Rendering.Stats.Vram.AllocatedTextureBytes,
            AllocatedRenderBufferBytes = Rendering.Stats.Vram.AllocatedRenderBufferBytes,
            FBOBandwidthBytes = Rendering.Stats.Vram.FBOBandwidthBytes,
            FBOBindCount = Rendering.Stats.Vram.FBOBindCount,
            VrLeftEyeDraws = Rendering.Stats.Vr.VrLeftEyeDraws,
            VrRightEyeDraws = Rendering.Stats.Vr.VrRightEyeDraws,
            VrLeftEyeVisible = Rendering.Stats.Vr.VrLeftEyeVisible,
            VrRightEyeVisible = Rendering.Stats.Vr.VrRightEyeVisible,
            VrLeftWorkerBuildTimeMs = Rendering.Stats.Vr.VrLeftWorkerBuildTimeMs,
            VrRightWorkerBuildTimeMs = Rendering.Stats.Vr.VrRightWorkerBuildTimeMs,
            VrRenderSubmitTimeMs = Rendering.Stats.Vr.VrRenderSubmitTimeMs,
            VrXrWaitFrameBlockTimeMs = Rendering.Stats.Vr.VrXrWaitFrameBlockTimeMs,
            VrXrEndFrameSubmitTimeMs = Rendering.Stats.Vr.VrXrEndFrameSubmitTimeMs,
            VrXrPredictedToLatePoseDeltaMillimeters = Rendering.Stats.Vr.VrXrPredictedToLatePoseDeltaMillimeters,
            VrXrPredictedToLatePoseDeltaDegrees = Rendering.Stats.Vr.VrXrPredictedToLatePoseDeltaDegrees,
            VrXrPredictedDisplayLeadTimeMs = Rendering.Stats.Vr.VrXrPredictedDisplayLeadTimeMs,
            VrXrMissedDeadlineFrames = Rendering.Stats.Vr.VrXrMissedDeadlineFrames,
            VrXrTrackingLossFrames = Rendering.Stats.Vr.VrXrTrackingLossFrames,
            VrXrRelocatePredictedTimeMs = Rendering.Stats.Vr.VrXrRelocatePredictedTimeMs,
            VrXrCollectFrustumExpansionDegrees = Rendering.Stats.Vr.VrXrCollectFrustumExpansionDegrees,
            VrXrPacingThreadIdleTimeMs = Rendering.Stats.Vr.VrXrPacingThreadIdleTimeMs,
            VrXrPacingHandoffStalls = Rendering.Stats.Vr.VrXrPacingHandoffStalls,
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
            RenderMatrixStatsReady = Rendering.Stats.RenderMatrix.RenderMatrixStatsReady,
            RenderMatrixApplied = Rendering.Stats.RenderMatrix.RenderMatrixApplied,
            RenderMatrixBatchCount = Rendering.Stats.RenderMatrix.RenderMatrixBatchCount,
            RenderMatrixMaxBatchSize = Rendering.Stats.RenderMatrix.RenderMatrixMaxBatchSize,
            RenderMatrixSetCalls = Rendering.Stats.RenderMatrix.RenderMatrixSetCalls,
            RenderMatrixListenerInvocations = Rendering.Stats.RenderMatrix.RenderMatrixListenerInvocations,
            RenderMatrixListenerCounts = listenerEntries,
            SkinnedBoundsStatsReady = Rendering.Stats.SkinnedBounds.SkinnedBoundsStatsReady,
            SkinnedBoundsDeferredScheduledCount = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredScheduledCount,
            SkinnedBoundsDeferredCompletedCount = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCompletedCount,
            SkinnedBoundsDeferredFailedCount = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredFailedCount,
            SkinnedBoundsDeferredInFlightCount = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredInFlightCount,
            SkinnedBoundsDeferredMaxInFlightCount = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxInFlightCount,
            SkinnedBoundsDeferredQueueWaitMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredQueueWaitMs,
            SkinnedBoundsDeferredCpuJobMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCpuJobMs,
            SkinnedBoundsDeferredApplyMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredApplyMs,
            SkinnedBoundsDeferredMaxQueueWaitMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxQueueWaitMs,
            SkinnedBoundsDeferredMaxCpuJobMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxCpuJobMs,
            SkinnedBoundsDeferredMaxApplyMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxApplyMs,
            SkinnedBoundsGpuCompletedCount = Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuCompletedCount,
            SkinnedBoundsGpuComputeMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuComputeMs,
            SkinnedBoundsGpuApplyMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuApplyMs,
            SkinnedBoundsGpuMaxComputeMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuMaxComputeMs,
            SkinnedBoundsGpuMaxApplyMs = Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuMaxApplyMs,
            OctreeStatsReady = Rendering.Stats.Octree.OctreeStatsReady,
            OctreeCollectCallCount = Rendering.Stats.Octree.OctreeCollectCallCount,
            OctreeVisibleRenderableCount = Rendering.Stats.Octree.OctreeVisibleRenderableCount,
            OctreeEmittedCommandCount = Rendering.Stats.Octree.OctreeEmittedCommandCount,
            OctreeMaxVisibleRenderablesPerCollect = Rendering.Stats.Octree.OctreeMaxVisibleRenderablesPerCollect,
            OctreeMaxEmittedCommandsPerCollect = Rendering.Stats.Octree.OctreeMaxEmittedCommandsPerCollect,
            OctreeAddCount = Rendering.Stats.Octree.OctreeAddCount,
            OctreeMoveCount = Rendering.Stats.Octree.OctreeMoveCount,
            OctreeRemoveCount = Rendering.Stats.Octree.OctreeRemoveCount,
            OctreeSkippedMoveCount = Rendering.Stats.Octree.OctreeSkippedMoveCount,
            OctreeSwapDrainedCommandCount = Rendering.Stats.Octree.OctreeSwapDrainedCommandCount,
            OctreeSwapBufferedCommandCount = Rendering.Stats.Octree.OctreeSwapBufferedCommandCount,
            OctreeSwapExecutedCommandCount = Rendering.Stats.Octree.OctreeSwapExecutedCommandCount,
            OctreeSwapDrainMs = Rendering.Stats.Octree.OctreeSwapDrainMs,
            OctreeSwapExecuteMs = Rendering.Stats.Octree.OctreeSwapExecuteMs,
            OctreeSwapMaxCommandMs = Rendering.Stats.Octree.OctreeSwapMaxCommandMs,
            OctreeSwapMaxCommandKind = Rendering.Stats.Octree.OctreeSwapMaxCommandKind,
            OctreeRaycastProcessedCommandCount = Rendering.Stats.Octree.OctreeRaycastProcessedCommandCount,
            OctreeRaycastDroppedCommandCount = Rendering.Stats.Octree.OctreeRaycastDroppedCommandCount,
            OctreeRaycastTraversalMs = Rendering.Stats.Octree.OctreeRaycastTraversalMs,
            OctreeRaycastCallbackMs = Rendering.Stats.Octree.OctreeRaycastCallbackMs,
            OctreeRaycastMaxTraversalMs = Rendering.Stats.Octree.OctreeRaycastMaxTraversalMs,
            OctreeRaycastMaxCallbackMs = Rendering.Stats.Octree.OctreeRaycastMaxCallbackMs,
            OctreeRaycastMaxCommandMs = Rendering.Stats.Octree.OctreeRaycastMaxCommandMs,
            GpuRenderPipelineProfilingEnabled = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingEnabled,
            GpuRenderPipelineProfilingSupported = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingSupported,
            GpuRenderPipelineTimingsReady = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady,
            GpuRenderPipelineBackend = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineBackend,
            GpuRenderPipelineStatusMessage = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineStatusMessage,
            GpuRenderPipelineFrameMs = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs,
            GpuRenderPipelineTimingRoots = Rendering.Stats.GpuPipelineProfiler.GetGpuRenderPipelineTimingRoots(),
        };
    }

    private static ThreadAllocationsPacket? CollectThreadAllocations()
    {
        var snap = Allocations.GetSnapshot();
        return new ThreadAllocationsPacket
        {
            Render = ToSlice(snap.Render),
            CollectSwap = ToSlice(snap.CollectSwap),
            Update = ToSlice(snap.Update),
            FixedUpdate = ToSlice(snap.FixedUpdate),
        };
    }

    private static AllocationSlice ToSlice(AllocationRingSnapshot ring)
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
        var m = Rendering.BvhStats.Latest;
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
        var jobs = Jobs;
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
        var invokes = GetMainThreadInvokeLogSnapshot();
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
#else
    internal static void WireProfilerSenderCollectors()
    {
    }
#endif
}
