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
        var listenerSnapshot = Rendering.Stats.GetRenderMatrixListenerSnapshot();
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
            DrawCalls = Rendering.Stats.DrawCalls,
            MultiDrawCalls = Rendering.Stats.MultiDrawCalls,
            TrianglesRendered = Rendering.Stats.TrianglesRendered,
            GpuCpuFallbackEvents = Rendering.Stats.GpuCpuFallbackEvents,
            GpuCpuFallbackRecoveredCommands = Rendering.Stats.GpuCpuFallbackRecoveredCommands,
            GpuTransparencyOpaqueOrOtherVisible = Rendering.Stats.GpuTransparencyOpaqueOrOtherVisible,
            GpuTransparencyMaskedVisible = Rendering.Stats.GpuTransparencyMaskedVisible,
            GpuTransparencyApproximateVisible = Rendering.Stats.GpuTransparencyApproximateVisible,
            GpuTransparencyExactVisible = Rendering.Stats.GpuTransparencyExactVisible,
            VulkanPipelineBinds = Rendering.Stats.VulkanPipelineBinds,
            VulkanDescriptorBinds = Rendering.Stats.VulkanDescriptorBinds,
            VulkanPushConstantWrites = Rendering.Stats.VulkanPushConstantWrites,
            VulkanVertexBufferBinds = Rendering.Stats.VulkanVertexBufferBinds,
            VulkanIndexBufferBinds = Rendering.Stats.VulkanIndexBufferBinds,
            VulkanPipelineBindSkips = Rendering.Stats.VulkanPipelineBindSkips,
            VulkanDescriptorBindSkips = Rendering.Stats.VulkanDescriptorBindSkips,
            VulkanVertexBufferBindSkips = Rendering.Stats.VulkanVertexBufferBindSkips,
            VulkanIndexBufferBindSkips = Rendering.Stats.VulkanIndexBufferBindSkips,
            VulkanPipelineCacheLookupHits = Rendering.Stats.VulkanPipelineCacheLookupHits,
            VulkanPipelineCacheLookupMisses = Rendering.Stats.VulkanPipelineCacheLookupMisses,
            VulkanPipelineCacheLookupHitRate = Rendering.Stats.VulkanPipelineCacheLookupHitRate,
            VulkanPipelineCacheMissSummary = Rendering.Stats.VulkanPipelineCacheMissSummary,
            VulkanFrameWaitFenceMs = Rendering.Stats.VulkanFrameWaitFenceMs,
            VulkanFrameAcquireImageMs = Rendering.Stats.VulkanFrameAcquireImageMs,
            VulkanFrameRecordCommandBufferMs = Rendering.Stats.VulkanFrameRecordCommandBufferMs,
            VulkanFrameSubmitMs = Rendering.Stats.VulkanFrameSubmitMs,
            VulkanFrameTrimMs = Rendering.Stats.VulkanFrameTrimMs,
            VulkanFramePresentMs = Rendering.Stats.VulkanFramePresentMs,
            VulkanFrameTotalMs = Rendering.Stats.VulkanFrameTotalMs,
            VulkanFrameGpuCommandBufferMs = Rendering.Stats.VulkanFrameGpuCommandBufferMs,
            VulkanDeviceLocalAllocationCount = Rendering.Stats.VulkanDeviceLocalAllocationCount,
            VulkanDeviceLocalAllocatedBytes = Rendering.Stats.VulkanDeviceLocalAllocatedBytes,
            VulkanUploadAllocationCount = Rendering.Stats.VulkanUploadAllocationCount,
            VulkanUploadAllocatedBytes = Rendering.Stats.VulkanUploadAllocatedBytes,
            VulkanReadbackAllocationCount = Rendering.Stats.VulkanReadbackAllocationCount,
            VulkanReadbackAllocatedBytes = Rendering.Stats.VulkanReadbackAllocatedBytes,
            VulkanDescriptorPoolCreateCount = Rendering.Stats.VulkanDescriptorPoolCreateCount,
            VulkanDescriptorPoolDestroyCount = Rendering.Stats.VulkanDescriptorPoolDestroyCount,
            VulkanDescriptorPoolResetCount = Rendering.Stats.VulkanDescriptorPoolResetCount,
            VulkanQueueSubmitCount = Rendering.Stats.VulkanQueueSubmitCount,
            VulkanDroppedFrameOps = Rendering.Stats.VulkanDroppedFrameOps,
            VulkanDroppedDrawOps = Rendering.Stats.VulkanDroppedDrawOps,
            VulkanDroppedComputeOps = Rendering.Stats.VulkanDroppedComputeOps,
            VulkanSceneSwapchainWriters = Rendering.Stats.VulkanSceneSwapchainWriters,
            VulkanOverlaySwapchainWriters = Rendering.Stats.VulkanOverlaySwapchainWriters,
            VulkanForcedDiagnosticSwapchainWriters = Rendering.Stats.VulkanForcedDiagnosticSwapchainWriters,
            VulkanFboOnlyDrawOps = Rendering.Stats.VulkanFboOnlyDrawOps,
            VulkanFboOnlyBlitOps = Rendering.Stats.VulkanFboOnlyBlitOps,
            VulkanMissingSceneSwapchainWriteFrames = Rendering.Stats.VulkanMissingSceneSwapchainWriteFrames,
            VulkanFirstFailedFrameOpPassIndex = Rendering.Stats.VulkanFirstFailedFrameOpPassIndex,
            VulkanFirstFailedFrameOpPipelineIdentity = Rendering.Stats.VulkanFirstFailedFrameOpPipelineIdentity,
            VulkanFirstFailedFrameOpViewportIdentity = Rendering.Stats.VulkanFirstFailedFrameOpViewportIdentity,
            VulkanFirstFailedFrameOpType = Rendering.Stats.VulkanFirstFailedFrameOpType,
            VulkanFirstFailedFrameOpTargetName = Rendering.Stats.VulkanFirstFailedFrameOpTargetName,
            VulkanFirstFailedFrameOpMaterialName = Rendering.Stats.VulkanFirstFailedFrameOpMaterialName,
            VulkanFirstFailedFrameOpShaderName = Rendering.Stats.VulkanFirstFailedFrameOpShaderName,
            VulkanFirstFailedFrameOpMessage = Rendering.Stats.VulkanFirstFailedFrameOpMessage,
            VulkanFrameDiagnosticSummary = Rendering.Stats.VulkanFrameDiagnosticSummary,
            VulkanValidationMessageCount = Rendering.Stats.VulkanValidationMessageCount,
            VulkanValidationErrorCount = Rendering.Stats.VulkanValidationErrorCount,
            VulkanLastValidationMessage = Rendering.Stats.VulkanLastValidationMessage,
            VulkanDescriptorFallbackSampledImages = Rendering.Stats.VulkanDescriptorFallbackSampledImages,
            VulkanDescriptorFallbackStorageImages = Rendering.Stats.VulkanDescriptorFallbackStorageImages,
            VulkanDescriptorFallbackUniformBuffers = Rendering.Stats.VulkanDescriptorFallbackUniformBuffers,
            VulkanDescriptorFallbackStorageBuffers = Rendering.Stats.VulkanDescriptorFallbackStorageBuffers,
            VulkanDescriptorFallbackTexelBuffers = Rendering.Stats.VulkanDescriptorFallbackTexelBuffers,
            VulkanDescriptorBindingFailures = Rendering.Stats.VulkanDescriptorBindingFailures,
            VulkanDescriptorSkippedDraws = Rendering.Stats.VulkanDescriptorSkippedDraws,
            VulkanDescriptorSkippedDispatches = Rendering.Stats.VulkanDescriptorSkippedDispatches,
            VulkanDescriptorFallbackSummary = Rendering.Stats.VulkanDescriptorFallbackSummary,
            VulkanDescriptorFailureSummary = Rendering.Stats.VulkanDescriptorFailureSummary,
            VulkanDynamicUniformAllocations = Rendering.Stats.VulkanDynamicUniformAllocations,
            VulkanDynamicUniformAllocatedBytes = Rendering.Stats.VulkanDynamicUniformAllocatedBytes,
            VulkanDynamicUniformExhaustions = Rendering.Stats.VulkanDynamicUniformExhaustions,
            VulkanRetiredResourcePlanReplacements = Rendering.Stats.VulkanRetiredResourcePlanReplacements,
            VulkanRetiredResourcePlanImages = Rendering.Stats.VulkanRetiredResourcePlanImages,
            VulkanRetiredResourcePlanBuffers = Rendering.Stats.VulkanRetiredResourcePlanBuffers,
            AllocatedVRAMBytes = Rendering.Stats.AllocatedVRAMBytes,
            AllocatedBufferBytes = Rendering.Stats.AllocatedBufferBytes,
            AllocatedTextureBytes = Rendering.Stats.AllocatedTextureBytes,
            AllocatedRenderBufferBytes = Rendering.Stats.AllocatedRenderBufferBytes,
            FBOBandwidthBytes = Rendering.Stats.FBOBandwidthBytes,
            FBOBindCount = Rendering.Stats.FBOBindCount,
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
            RenderMatrixStatsReady = Rendering.Stats.RenderMatrixStatsReady,
            RenderMatrixApplied = Rendering.Stats.RenderMatrixApplied,
            RenderMatrixBatchCount = Rendering.Stats.RenderMatrixBatchCount,
            RenderMatrixMaxBatchSize = Rendering.Stats.RenderMatrixMaxBatchSize,
            RenderMatrixSetCalls = Rendering.Stats.RenderMatrixSetCalls,
            RenderMatrixListenerInvocations = Rendering.Stats.RenderMatrixListenerInvocations,
            RenderMatrixListenerCounts = listenerEntries,
            SkinnedBoundsStatsReady = Rendering.Stats.SkinnedBoundsStatsReady,
            SkinnedBoundsDeferredScheduledCount = Rendering.Stats.SkinnedBoundsDeferredScheduledCount,
            SkinnedBoundsDeferredCompletedCount = Rendering.Stats.SkinnedBoundsDeferredCompletedCount,
            SkinnedBoundsDeferredFailedCount = Rendering.Stats.SkinnedBoundsDeferredFailedCount,
            SkinnedBoundsDeferredInFlightCount = Rendering.Stats.SkinnedBoundsDeferredInFlightCount,
            SkinnedBoundsDeferredMaxInFlightCount = Rendering.Stats.SkinnedBoundsDeferredMaxInFlightCount,
            SkinnedBoundsDeferredQueueWaitMs = Rendering.Stats.SkinnedBoundsDeferredQueueWaitMs,
            SkinnedBoundsDeferredCpuJobMs = Rendering.Stats.SkinnedBoundsDeferredCpuJobMs,
            SkinnedBoundsDeferredApplyMs = Rendering.Stats.SkinnedBoundsDeferredApplyMs,
            SkinnedBoundsDeferredMaxQueueWaitMs = Rendering.Stats.SkinnedBoundsDeferredMaxQueueWaitMs,
            SkinnedBoundsDeferredMaxCpuJobMs = Rendering.Stats.SkinnedBoundsDeferredMaxCpuJobMs,
            SkinnedBoundsDeferredMaxApplyMs = Rendering.Stats.SkinnedBoundsDeferredMaxApplyMs,
            SkinnedBoundsGpuCompletedCount = Rendering.Stats.SkinnedBoundsGpuCompletedCount,
            SkinnedBoundsGpuComputeMs = Rendering.Stats.SkinnedBoundsGpuComputeMs,
            SkinnedBoundsGpuApplyMs = Rendering.Stats.SkinnedBoundsGpuApplyMs,
            SkinnedBoundsGpuMaxComputeMs = Rendering.Stats.SkinnedBoundsGpuMaxComputeMs,
            SkinnedBoundsGpuMaxApplyMs = Rendering.Stats.SkinnedBoundsGpuMaxApplyMs,
            OctreeStatsReady = Rendering.Stats.OctreeStatsReady,
            OctreeCollectCallCount = Rendering.Stats.OctreeCollectCallCount,
            OctreeVisibleRenderableCount = Rendering.Stats.OctreeVisibleRenderableCount,
            OctreeEmittedCommandCount = Rendering.Stats.OctreeEmittedCommandCount,
            OctreeMaxVisibleRenderablesPerCollect = Rendering.Stats.OctreeMaxVisibleRenderablesPerCollect,
            OctreeMaxEmittedCommandsPerCollect = Rendering.Stats.OctreeMaxEmittedCommandsPerCollect,
            OctreeAddCount = Rendering.Stats.OctreeAddCount,
            OctreeMoveCount = Rendering.Stats.OctreeMoveCount,
            OctreeRemoveCount = Rendering.Stats.OctreeRemoveCount,
            OctreeSkippedMoveCount = Rendering.Stats.OctreeSkippedMoveCount,
            OctreeSwapDrainedCommandCount = Rendering.Stats.OctreeSwapDrainedCommandCount,
            OctreeSwapBufferedCommandCount = Rendering.Stats.OctreeSwapBufferedCommandCount,
            OctreeSwapExecutedCommandCount = Rendering.Stats.OctreeSwapExecutedCommandCount,
            OctreeSwapDrainMs = Rendering.Stats.OctreeSwapDrainMs,
            OctreeSwapExecuteMs = Rendering.Stats.OctreeSwapExecuteMs,
            OctreeSwapMaxCommandMs = Rendering.Stats.OctreeSwapMaxCommandMs,
            OctreeSwapMaxCommandKind = Rendering.Stats.OctreeSwapMaxCommandKind,
            OctreeRaycastProcessedCommandCount = Rendering.Stats.OctreeRaycastProcessedCommandCount,
            OctreeRaycastDroppedCommandCount = Rendering.Stats.OctreeRaycastDroppedCommandCount,
            OctreeRaycastTraversalMs = Rendering.Stats.OctreeRaycastTraversalMs,
            OctreeRaycastCallbackMs = Rendering.Stats.OctreeRaycastCallbackMs,
            OctreeRaycastMaxTraversalMs = Rendering.Stats.OctreeRaycastMaxTraversalMs,
            OctreeRaycastMaxCallbackMs = Rendering.Stats.OctreeRaycastMaxCallbackMs,
            OctreeRaycastMaxCommandMs = Rendering.Stats.OctreeRaycastMaxCommandMs,
            GpuRenderPipelineProfilingEnabled = Rendering.Stats.GpuRenderPipelineProfilingEnabled,
            GpuRenderPipelineProfilingSupported = Rendering.Stats.GpuRenderPipelineProfilingSupported,
            GpuRenderPipelineTimingsReady = Rendering.Stats.GpuRenderPipelineTimingsReady,
            GpuRenderPipelineBackend = Rendering.Stats.GpuRenderPipelineBackend,
            GpuRenderPipelineStatusMessage = Rendering.Stats.GpuRenderPipelineStatusMessage,
            GpuRenderPipelineFrameMs = Rendering.Stats.GpuRenderPipelineFrameMs,
            GpuRenderPipelineTimingRoots = Rendering.Stats.GetGpuRenderPipelineTimingRoots(),
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
