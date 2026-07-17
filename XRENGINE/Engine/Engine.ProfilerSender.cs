using XREngine.Data.Profiling;
using XREngine.Rendering.Shadows;
using OcclusionTelemetry = XREngine.Rendering.Occlusion.OcclusionTelemetry;

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
                WallTimeMs = t.WallTimeMs,
                DownstreamRenderPressureMs = t.DownstreamRenderPressureMs,
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

        var assetRowsSnapshot = Rendering.Stats.SceneAssets.GetAssetCostRows();
        var assetRows = new RenderAssetCostRowData[assetRowsSnapshot.Length];
        for (int i = 0; i < assetRowsSnapshot.Length; i++)
        {
            var row = assetRowsSnapshot[i];
            assetRows[i] = new RenderAssetCostRowData
            {
                SourceAssetIdentity = row.SourceAssetIdentity,
                CookedVariantIdentity = row.CookedVariantIdentity,
                MeshName = row.MeshName,
                MaterialName = row.MaterialName,
                Representation = row.Representation,
                DrawCalls = row.DrawCalls,
                Triangles = row.Triangles,
                MaterialSlots = row.MaterialSlots,
                TextureCount = row.TextureCount,
                SkinnedDraws = row.SkinnedDraws,
            };
        }

        var churnRowsSnapshot = Rendering.Stats.ResourceChurn.GetLastFrameRows();
        var churnRows = new RenderResourceChurnRowData[churnRowsSnapshot.Length];
        for (int i = 0; i < churnRowsSnapshot.Length; i++)
        {
            var row = churnRowsSnapshot[i];
            churnRows[i] = new RenderResourceChurnRowData
            {
                ResourceKind = row.ResourceKind,
                ResourceName = row.ResourceName,
                EventName = row.EventName,
                Reason = row.Reason,
                Count = row.Count,
            };
        }

        ShadowAtlasSolveDiagnostics shadowAtlasSolve = Rendering.Stats.ShadowAtlas.LastSolveDiagnostics;
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
            ShadowAtlasSolve = ConvertShadowAtlasSolveDiagnostics(shadowAtlasSolve),
            RenderProfilerV2 = new RenderProfilerV2Data
            {
                RendererState = new RenderProfilerRendererStateData
                {
                    IndirectCountCalls = Rendering.Stats.RendererState.IndirectCountCalls,
                    ShaderProgramSwitches = Rendering.Stats.RendererState.ShaderProgramSwitches,
                    ProgramPipelineSwitches = Rendering.Stats.RendererState.ProgramPipelineSwitches,
                    VaoBinds = Rendering.Stats.RendererState.VaoBinds,
                    VaoBindSkips = Rendering.Stats.RendererState.VaoBindSkips,
                    ArrayBufferBinds = Rendering.Stats.RendererState.ArrayBufferBinds,
                    ElementArrayBufferBinds = Rendering.Stats.RendererState.ElementArrayBufferBinds,
                    DrawIndirectBufferBinds = Rendering.Stats.RendererState.DrawIndirectBufferBinds,
                    ParameterBufferBinds = Rendering.Stats.RendererState.ParameterBufferBinds,
                    SsboBinds = Rendering.Stats.RendererState.SsboBinds,
                    UboBinds = Rendering.Stats.RendererState.UboBinds,
                    TextureBinds = Rendering.Stats.RendererState.TextureBinds,
                    TextureBindSkips = Rendering.Stats.RendererState.TextureBindSkips,
                    TextureUnitSwitches = Rendering.Stats.RendererState.TextureUnitSwitches,
                    UniformCalls = Rendering.Stats.RendererState.UniformCalls,
                    SamplerUniformCalls = Rendering.Stats.RendererState.SamplerUniformCalls,
                    BufferUploadBytes = Rendering.Stats.RendererState.BufferUploadBytes,
                    BarrierCalls = Rendering.Stats.RendererState.BarrierCalls,
                    BarrierAll = Rendering.Stats.RendererState.BarrierAll,
                    BarrierCommand = Rendering.Stats.RendererState.BarrierCommand,
                    BarrierBufferUpdate = Rendering.Stats.RendererState.BarrierBufferUpdate,
                    BarrierShaderStorage = Rendering.Stats.RendererState.BarrierShaderStorage,
                    BarrierTextureFetch = Rendering.Stats.RendererState.BarrierTextureFetch,
                    BarrierTextureUpdate = Rendering.Stats.RendererState.BarrierTextureUpdate,
                    BarrierFramebuffer = Rendering.Stats.RendererState.BarrierFramebuffer,
                    TimestampQueryCount = Rendering.Stats.RendererState.TimestampQueryCount,
                    TimestampQueryReadbackBytes = Rendering.Stats.RendererState.TimestampQueryReadbackBytes,
                    TimestampDenseModeFrames = Rendering.Stats.RendererState.TimestampDenseModeFrames,
                    RedundantStateSkips = Rendering.Stats.RendererState.RedundantStateSkips,
                    CpuDirectDrawCalls = Rendering.Stats.RendererState.CpuDirectDrawCalls,
                    GpuIndirectDrawCalls = Rendering.Stats.RendererState.GpuIndirectDrawCalls,
                    GpuMeshletDrawCalls = Rendering.Stats.RendererState.GpuMeshletDrawCalls,
                    UnknownStrategyDrawCalls = Rendering.Stats.RendererState.UnknownStrategyDrawCalls,
                    DirectionalCascadeStaleSampled = Rendering.Stats.RendererState.DirectionalCascadeStaleSampled,
                    DirectionalCascadeMixedGenerationPrevented = Rendering.Stats.RendererState.DirectionalCascadeMixedGenerationPrevented,
                    DirectionalCascadePhysicalReprojected = Rendering.Stats.RendererState.DirectionalCascadePhysicalReprojected,
                    DirectionalCascadeForcedFreshRender = Rendering.Stats.RendererState.DirectionalCascadeForcedFreshRender,
                    ActiveTextureBindingRung = Rendering.Stats.RendererState.ActiveTextureBindingRung,
                    ActiveStereoMode = Rendering.Stats.RendererState.ActiveStereoMode,
                    ActiveSubmissionStrategy = Rendering.Stats.RendererState.ActiveSubmissionStrategy,
                    ActiveRenderBackend = Rendering.Stats.RendererState.ActiveRenderBackend,
                    ValidationLayersEnabled = Rendering.Stats.RendererState.ValidationLayersEnabled,
                    DebugOutputEnabled = Rendering.Stats.RendererState.DebugOutputEnabled,
                    GpuTimestampsDenseMode = Rendering.Stats.RendererState.GpuTimestampsDenseMode,
                },
                SceneAssets = new RenderProfilerSceneAssetData
                {
                    VisibleRendererCount = Rendering.Stats.SceneAssets.VisibleRendererCount,
                    VisibleSubmeshCount = Rendering.Stats.SceneAssets.VisibleSubmeshCount,
                    VisibleTriangleCount = Rendering.Stats.SceneAssets.VisibleTriangleCount,
                    MaterialSlotCount = Rendering.Stats.SceneAssets.MaterialSlotCount,
                    ActiveMaterialCount = Rendering.Stats.SceneAssets.ActiveMaterialCount,
                    TextureCount = Rendering.Stats.SceneAssets.TextureCount,
                    ResidentTextureMemoryBytes = Rendering.Stats.SceneAssets.ResidentTextureMemoryBytes,
                    TextureUploadJobs = Rendering.Stats.SceneAssets.TextureUploadJobs,
                    TextureUploadBytes = Rendering.Stats.SceneAssets.TextureUploadBytes,
                    TextureUploadMs = Rendering.Stats.SceneAssets.TextureUploadMs,
                    ShaderVariantsRequested = Rendering.Stats.SceneAssets.ShaderVariantsRequested,
                    ShaderVariantsWarming = Rendering.Stats.SceneAssets.ShaderVariantsWarming,
                    ShaderVariantsLinked = Rendering.Stats.SceneAssets.ShaderVariantsLinked,
                    ShaderVariantsFailed = Rendering.Stats.SceneAssets.ShaderVariantsFailed,
                    ShaderVariantsLoadedFromDiskCache = Rendering.Stats.SceneAssets.ShaderVariantsLoadedFromDiskCache,
                    ShaderVariantsGeneratedThisRun = Rendering.Stats.SceneAssets.ShaderVariantsGeneratedThisRun,
                    SkinnedRendererCount = Rendering.Stats.SceneAssets.SkinnedRendererCount,
                    BoneMatrixUploadBytes = Rendering.Stats.SceneAssets.BoneMatrixUploadBytes,
                    BlendshapeWeightUploadBytes = Rendering.Stats.SceneAssets.BlendshapeWeightUploadBytes,
                    BlendshapeActiveListUploadBytes = Rendering.Stats.SceneAssets.BlendshapeActiveListUploadBytes,
                    BlendshapeDeltaBytes = Rendering.Stats.SceneAssets.BlendshapeDeltaBytes,
                    SkinningCoreInfluenceBytes = Rendering.Stats.SceneAssets.SkinningCoreInfluenceBytes,
                    SkinningSpillHeaderBytes = Rendering.Stats.SceneAssets.SkinningSpillHeaderBytes,
                    SkinningSpillEntryBytes = Rendering.Stats.SceneAssets.SkinningSpillEntryBytes,
                    SkinPaletteUploadBytes = Rendering.Stats.SceneAssets.SkinPaletteUploadBytes,
                    SkinningComputeDispatchCount = Rendering.Stats.SceneAssets.SkinningComputeDispatchCount,
                    BlendshapeComputeDispatchCount = Rendering.Stats.SceneAssets.BlendshapeComputeDispatchCount,
                    SkippedSkinningComputeDispatchCount = Rendering.Stats.SceneAssets.SkippedSkinningComputeDispatchCount,
                    SkippedBlendshapeComputeDispatchCount = Rendering.Stats.SceneAssets.SkippedBlendshapeComputeDispatchCount,
                    ReusedSkinnedOutputBufferCount = Rendering.Stats.SceneAssets.ReusedSkinnedOutputBufferCount,
                    LiveSkinningShaderPermutationCount = Rendering.Stats.SceneAssets.LiveSkinningShaderPermutationCount,
                    BlendshapeAuthoredShapeCount = Rendering.Stats.SceneAssets.BlendshapeAuthoredShapeCount,
                    BlendshapeActiveShapeCount = Rendering.Stats.SceneAssets.BlendshapeActiveShapeCount,
                    BlendshapeAffectedVertexCount = Rendering.Stats.SceneAssets.BlendshapeAffectedVertexCount,
                    CompactedActiveBlendshapeCount = Rendering.Stats.SceneAssets.CompactedActiveBlendshapeCount,
                    LiveBlendshapeShaderPermutationCount = Rendering.Stats.SceneAssets.LiveBlendshapeShaderPermutationCount,
                    AvatarSourceMeshCount = Rendering.Stats.SceneAssets.AvatarSourceMeshCount,
                    AvatarOptimizedLodCount = Rendering.Stats.SceneAssets.AvatarOptimizedLodCount,
                    AvatarMeshletCount = Rendering.Stats.SceneAssets.AvatarMeshletCount,
                    AvatarVisibilityBufferCount = Rendering.Stats.SceneAssets.AvatarVisibilityBufferCount,
                    AvatarClusterVirtualizedCount = Rendering.Stats.SceneAssets.AvatarClusterVirtualizedCount,
                    AvatarOctahedralImpostorCount = Rendering.Stats.SceneAssets.AvatarOctahedralImpostorCount,
                    AvatarGaussianSplatCount = Rendering.Stats.SceneAssets.AvatarGaussianSplatCount,
                    RenderAssetCostRows = assetRows,
                },
                GpuDriven = new RenderProfilerGpuDrivenData
                {
                    GpuDrivenCulledCommandCount = Rendering.Stats.GpuDriven.CulledCommandCount,
                    GpuDrivenActiveBucketCount = Rendering.Stats.GpuDriven.ActiveBucketCount,
                    GpuDrivenEmptyBucketSkips = Rendering.Stats.GpuDriven.EmptyBucketSkips,
                    GpuDrivenFullBucketScans = Rendering.Stats.GpuDriven.FullBucketScans,
                    GpuDrivenMaterialScatterDispatches = Rendering.Stats.GpuDriven.MaterialScatterDispatches,
                    GpuDrivenIndirectCommandGenerationMs = Rendering.Stats.GpuDriven.IndirectCommandGenerationMs,
                    GpuDrivenGpuCullMs = Rendering.Stats.GpuDriven.GpuCullMs,
                    GpuDrivenGpuSortCompactMs = Rendering.Stats.GpuDriven.GpuSortCompactMs,
                    GpuDrivenDelayedDrawCountBufferValue = Rendering.Stats.GpuDriven.DelayedDrawCountBufferValue,
                    GpuDrivenDelayedDiagnosticReadbackBytes = Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackBytes,
                    GpuDrivenDelayedDiagnosticReadbackCount = Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackCount,
                    GpuCompactionOverflow = Rendering.Stats.GpuDriven.GpuCompactionOverflow,
                    GpuActiveListOverflow = Rendering.Stats.GpuDriven.ActiveListOverflow,
                    GpuBucketOverflow = Rendering.Stats.GpuDriven.BucketOverflow,
                    GpuMeshletOverflow = Rendering.Stats.GpuDriven.MeshletOverflow,
                    GpuHiZMode = Rendering.Stats.GpuDriven.HiZMode,
                    GpuHiZOnePhaseFrames = Rendering.Stats.GpuDriven.HiZOnePhaseFrames,
                    GpuHiZTwoPhaseFrames = Rendering.Stats.GpuDriven.HiZTwoPhaseFrames,
                    GpuHiZPhaseOneDraws = Rendering.Stats.GpuDriven.HiZPhaseOneDraws,
                    GpuHiZPhaseTwoDraws = Rendering.Stats.GpuDriven.HiZPhaseTwoDraws,
                    VisibilityPassDraws = Rendering.Stats.GpuDriven.VisibilityPassDraws,
                    VisibilityClassifiedPixels = Rendering.Stats.GpuDriven.VisibilityClassifiedPixels,
                    VisibilityActiveMaterialTiles = Rendering.Stats.GpuDriven.VisibilityActiveMaterialTiles,
                    VisibilityClassificationOverflow = Rendering.Stats.GpuDriven.VisibilityClassificationOverflow,
                    VisibilityReconstructionMs = Rendering.Stats.GpuDriven.VisibilityReconstructionMs,
                    VisibilityMaterialShadingMs = Rendering.Stats.GpuDriven.VisibilityMaterialShadingMs,
                },
                Occlusion = CollectOcclusionProfilerData(),
            },
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
            GpuMeshletLastVisibleMeshletCount = Rendering.Stats.GpuMeshlets.LastVisibleMeshletCount,
            GpuMeshletLastDispatchedMeshletCount = Rendering.Stats.GpuMeshlets.LastDispatchedMeshletCount,
            GpuMeshletLastTaskRecordOverflowCount = Rendering.Stats.GpuMeshlets.LastTaskRecordOverflowCount,
            GpuMeshletLastDispatchMs = Rendering.Stats.GpuMeshlets.LastDispatchTime.TotalMilliseconds,
            GpuMeshletLastReadbackBytes = Rendering.Stats.GpuMeshlets.LastReadbackBytes,
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
            VulkanDriverPipelineCachePersistedHits = Rendering.Stats.Vulkan.VulkanDriverPipelineCachePersistedHits,
            VulkanDriverPipelineCacheRuntimeHits = Rendering.Stats.Vulkan.VulkanDriverPipelineCacheRuntimeHits,
            VulkanDriverPipelineCacheMisses = Rendering.Stats.Vulkan.VulkanDriverPipelineCacheMisses,
            VulkanDriverPipelineCacheUnknown = Rendering.Stats.Vulkan.VulkanDriverPipelineCacheUnknown,
            VulkanPipelineCompileRequiredCount = Rendering.Stats.Vulkan.VulkanPipelineCompileRequiredCount,
            VulkanPipelineCompileCompletedCount = Rendering.Stats.Vulkan.VulkanPipelineCompileCompletedCount,
            VulkanPipelineBackgroundCompileCompletedCount = Rendering.Stats.Vulkan.VulkanPipelineBackgroundCompileCompletedCount,
            VulkanPipelineCompileTotalMs = Rendering.Stats.Vulkan.VulkanPipelineCompileTotalMs,
            VulkanPipelineCompileMaxMs = Rendering.Stats.Vulkan.VulkanPipelineCompileMaxMs,
            VulkanPipelineAsyncQueuedCount = Rendering.Stats.Vulkan.VulkanPipelineAsyncQueuedCount,
            VulkanPipelineQueueRejectedCount = Rendering.Stats.Vulkan.VulkanPipelineQueueRejectedCount,
            VulkanPipelineDrawNotReadyCount = Rendering.Stats.Vulkan.VulkanPipelineDrawNotReadyCount,
            VulkanPipelineQueueDepthHighWater = Rendering.Stats.Vulkan.VulkanPipelineQueueDepthHighWater,
            VulkanPipelineQueueCapacity = Rendering.Stats.Vulkan.VulkanPipelineQueueCapacity,
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
            VulkanLifetimeLiveResourceCount = Rendering.Stats.Vulkan.VulkanLifetimeLiveResourceCount,
            VulkanTrackedDescriptorSetCount = Rendering.Stats.Vulkan.VulkanTrackedDescriptorSetCount,
            VulkanLifetimePendingRetirementCount = Rendering.Stats.Vulkan.VulkanLifetimePendingRetirementCount,
            VulkanLifetimeOldestPendingRetirementAgeMilliseconds = Rendering.Stats.Vulkan.VulkanLifetimeOldestPendingRetirementAgeMilliseconds,
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
            VulkanMeshFrameDataArenaChunkCount = Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkCount,
            VulkanMeshFrameDataMappedBytes = Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytes,
            VulkanMeshFrameDataReservedBytes = Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytes,
            VulkanMeshFrameDataReservationCount = Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationCount,
            VulkanMeshFrameDataGeneration = Rendering.Stats.Vulkan.VulkanMeshFrameDataGeneration,
            VulkanMeshFrameDataRecordingLeases = Rendering.Stats.Vulkan.VulkanMeshFrameDataRecordingLeases,
            VulkanMeshFrameDataCachedLeases = Rendering.Stats.Vulkan.VulkanMeshFrameDataCachedLeases,
            VulkanMeshFrameDataSubmittedLeases = Rendering.Stats.Vulkan.VulkanMeshFrameDataSubmittedLeases,
            VulkanMeshFrameDataActiveGenerationCount = Rendering.Stats.Vulkan.VulkanMeshFrameDataActiveGenerationCount,
            VulkanMeshFrameDataLeaseRetainedGenerationCount = Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseRetainedGenerationCount,
            VulkanMeshDescriptorAllocationVariants = Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariants,
            VulkanMeshDescriptorPools = Rendering.Stats.Vulkan.VulkanMeshDescriptorPools,
            VulkanMeshDescriptorAllocatedSets = Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocatedSets,
            VulkanMeshDescriptorReservedSets = Rendering.Stats.Vulkan.VulkanMeshDescriptorReservedSets,
            VulkanMeshFrameDataArenaChunkHighWater = Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkHighWater,
            VulkanMeshFrameDataMappedBytesHighWater = Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytesHighWater,
            VulkanMeshFrameDataReservedBytesHighWater = Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytesHighWater,
            VulkanMeshFrameDataReservationHighWater = Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationHighWater,
            VulkanMeshFrameDataLeaseHighWater = Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseHighWater,
            VulkanMeshDescriptorAllocationVariantHighWater = Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariantHighWater,
            VulkanMeshDescriptorPoolHighWater = Rendering.Stats.Vulkan.VulkanMeshDescriptorPoolHighWater,
            VulkanMeshDescriptorSetHighWater = Rendering.Stats.Vulkan.VulkanMeshDescriptorSetHighWater,
            VulkanRetiredResourcePlanReplacements = Rendering.Stats.Vulkan.VulkanRetiredResourcePlanReplacements,
            VulkanRetiredResourcePlanImages = Rendering.Stats.Vulkan.VulkanRetiredResourcePlanImages,
            VulkanRetiredResourcePlanBuffers = Rendering.Stats.Vulkan.VulkanRetiredResourcePlanBuffers,
            VulkanFrameLoop = new VulkanFrameLoopTelemetryData
            {
                FrameSampleTimingQueriesMs = Rendering.Stats.Vulkan.VulkanFrameSampleTimingQueriesMs,
                FrameDrainRetiredResourcesMs = Rendering.Stats.Vulkan.VulkanFrameDrainRetiredResourcesMs,
                FrameAcquireBridgeSubmitMs = Rendering.Stats.Vulkan.VulkanFrameAcquireBridgeSubmitMs,
                FrameWaitSwapchainImageMs = Rendering.Stats.Vulkan.VulkanFrameWaitSwapchainImageMs,
                FrameResetDynamicUniformRingMs = Rendering.Stats.Vulkan.VulkanFrameResetDynamicUniformRingMs,
                RecordCommandBufferAllocatedBytes = Rendering.Stats.Vulkan.VulkanRecordCommandBufferAllocatedBytes,
                FrameOpTotalCount = Rendering.Stats.Vulkan.VulkanFrameOpTotalCount,
                FrameOpClearCount = Rendering.Stats.Vulkan.VulkanFrameOpClearCount,
                FrameOpMeshDrawCount = Rendering.Stats.Vulkan.VulkanFrameOpMeshDrawCount,
                FrameOpIndirectDrawCount = Rendering.Stats.Vulkan.VulkanFrameOpIndirectDrawCount,
                FrameOpMeshTaskDispatchCount = Rendering.Stats.Vulkan.VulkanFrameOpMeshTaskDispatchCount,
                FrameOpBlitCount = Rendering.Stats.Vulkan.VulkanFrameOpBlitCount,
                FrameOpComputeCount = Rendering.Stats.Vulkan.VulkanFrameOpComputeCount,
                FrameOpSwapchainWriteCount = Rendering.Stats.Vulkan.VulkanFrameOpSwapchainWriteCount,
                FrameOpFboWriteCount = Rendering.Stats.Vulkan.VulkanFrameOpFboWriteCount,
                FrameOpUniquePassCount = Rendering.Stats.Vulkan.VulkanFrameOpUniquePassCount,
                FrameOpUniqueContextCount = Rendering.Stats.Vulkan.VulkanFrameOpUniqueContextCount,
                FrameOpUniqueTargetCount = Rendering.Stats.Vulkan.VulkanFrameOpUniqueTargetCount,
                CommandBufferCleanReuseCount = Rendering.Stats.Vulkan.VulkanCommandBufferCleanReuseCount,
                CommandBufferRecordCount = Rendering.Stats.Vulkan.VulkanCommandBufferRecordCount,
                CommandBufferForcedDirtyCount = Rendering.Stats.Vulkan.VulkanCommandBufferForcedDirtyCount,
                CommandBufferFrameOpSignatureDirtyCount = Rendering.Stats.Vulkan.VulkanCommandBufferFrameOpSignatureDirtyCount,
                CommandBufferPlannerDirtyCount = Rendering.Stats.Vulkan.VulkanCommandBufferPlannerDirtyCount,
                CommandBufferProfilerDirtyCount = Rendering.Stats.Vulkan.VulkanCommandBufferProfilerDirtyCount,
                CommandBufferDirtySummary = Rendering.Stats.Vulkan.VulkanCommandBufferDirtySummary,
                CommandChainsScheduled = Rendering.Stats.Vulkan.VulkanCommandChainsScheduled,
                CommandChainsRecorded = Rendering.Stats.Vulkan.VulkanCommandChainsRecorded,
                CommandChainsReused = Rendering.Stats.Vulkan.VulkanCommandChainsReused,
                CommandChainsFrameDataRefreshed = Rendering.Stats.Vulkan.VulkanCommandChainsFrameDataRefreshed,
                VolatileCommandChainsRecorded = Rendering.Stats.Vulkan.VulkanVolatileCommandChainsRecorded,
                PrimaryCommandBuffersReused = Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersReused,
                PrimaryCommandBuffersRecorded = Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersRecorded,
                VisibilityPacketCount = Rendering.Stats.Vulkan.VulkanVisibilityPacketCount,
                RenderPacketCount = Rendering.Stats.Vulkan.VulkanRenderPacketCount,
                SecondaryCommandBufferCount = Rendering.Stats.Vulkan.VulkanSecondaryCommandBufferCount,
                CommandChainWorkerRecordMs = Rendering.Stats.Vulkan.VulkanCommandChainWorkerRecordMs,
                RenderThreadWaitForChainWorkersMs = Rendering.Stats.Vulkan.VulkanRenderThreadWaitForChainWorkersMs,
                FirstCommandChainStructuralDirtyReason = Rendering.Stats.Vulkan.VulkanFirstCommandChainStructuralDirtyReason,
                FirstCommandChainDescriptorGenerationMismatch = Rendering.Stats.Vulkan.VulkanFirstCommandChainDescriptorGenerationMismatch,
                FirstCommandChainResourcePlanRevisionMismatch = Rendering.Stats.Vulkan.VulkanFirstCommandChainResourcePlanRevisionMismatch,
                RetiredDescriptorPoolCount = Rendering.Stats.Vulkan.VulkanRetiredDescriptorPoolCount,
                RetiredPipelineCount = Rendering.Stats.Vulkan.VulkanRetiredPipelineCount,
                RetiredFramebufferCount = Rendering.Stats.Vulkan.VulkanRetiredFramebufferCount,
                RetiredBufferCount = Rendering.Stats.Vulkan.VulkanRetiredBufferCount,
                RetiredBufferMemoryCount = Rendering.Stats.Vulkan.VulkanRetiredBufferMemoryCount,
                RetiredImageCount = Rendering.Stats.Vulkan.VulkanRetiredImageCount,
                RetiredImageViewCount = Rendering.Stats.Vulkan.VulkanRetiredImageViewCount,
                RetiredSamplerCount = Rendering.Stats.Vulkan.VulkanRetiredSamplerCount,
                RetiredImageMemoryCount = Rendering.Stats.Vulkan.VulkanRetiredImageMemoryCount,
                RetiredImageBytes = Rendering.Stats.Vulkan.VulkanRetiredImageBytes,
            },
            FrameLifecycle = new FrameLifecycleTelemetryData
            {
                UpdateFrameId = Rendering.Stats.FrameLifecycle.UpdateFrameId,
                CollectFrameId = Rendering.Stats.FrameLifecycle.CollectFrameId,
                SwapFrameId = Rendering.Stats.FrameLifecycle.SwapFrameId,
                RenderFrameId = Rendering.Stats.FrameLifecycle.RenderFrameId,
                PresentFrameId = Rendering.Stats.FrameLifecycle.PresentFrameId,
                RequestedCollectGeneration = Rendering.Stats.FrameLifecycle.RequestedCollectGeneration,
                CompletedCollectGeneration = Rendering.Stats.FrameLifecycle.CompletedCollectGeneration,
                PublishedCollectGeneration = Rendering.Stats.FrameLifecycle.PublishedCollectGeneration,
                ConsumedCollectGeneration = Rendering.Stats.FrameLifecycle.ConsumedCollectGeneration,
                RequiredCollectGeneration = Rendering.Stats.FrameLifecycle.RequiredCollectGeneration,
                CollectVisibleLatePolicy = Rendering.Stats.FrameLifecycle.CollectVisibleLatePolicy,
                CollectWaitForRenderMs = Rendering.Stats.FrameLifecycle.CollectWaitForRenderMs,
                CollectWaitReason = Rendering.Stats.FrameLifecycle.CollectWaitReason,
                RenderWaitForCollectMs = Rendering.Stats.FrameLifecycle.RenderWaitForCollectMs,
                RenderWaitReason = Rendering.Stats.FrameLifecycle.RenderWaitReason,
                SkippedCollectFrames = Rendering.Stats.FrameLifecycle.SkippedCollectFrames,
                StaleCollectReuseFrames = Rendering.Stats.FrameLifecycle.StaleCollectReuseFrames,
            },
            AllocatedVRAMBytes = Rendering.Stats.Vram.AllocatedVRAMBytes,
            AllocatedBufferBytes = Rendering.Stats.Vram.AllocatedBufferBytes,
            AllocatedTextureBytes = Rendering.Stats.Vram.AllocatedTextureBytes,
            AllocatedRenderBufferBytes = Rendering.Stats.Vram.AllocatedRenderBufferBytes,
            FBOBandwidthBytes = Rendering.Stats.Vram.FBOBandwidthBytes,
            FBOBindCount = Rendering.Stats.Vram.FBOBindCount,
            RenderResourceCreatedCount = Rendering.Stats.ResourceChurn.CreatedCount,
            RenderResourceRecreatedCount = Rendering.Stats.ResourceChurn.RecreatedCount,
            RenderResourceResizedCount = Rendering.Stats.ResourceChurn.ResizedCount,
            RenderResourceDestroyedCount = Rendering.Stats.ResourceChurn.DestroyedCount,
            RenderResourceChurnRows = churnRows,
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
            CpuSpatialTreeMode = Rendering.Stats.Octree.CpuSpatialTreeMode,
            CpuSpatialTreeNodeCount = Rendering.Stats.Octree.CpuSpatialTreeNodeCount,
            CpuSpatialTreeItemCount = Rendering.Stats.Octree.CpuSpatialTreeItemCount,
            CpuSpatialTreeRootItemCount = Rendering.Stats.Octree.CpuSpatialTreeRootItemCount,
            CpuSpatialTreeMaxNodeItemCount = Rendering.Stats.Octree.CpuSpatialTreeMaxNodeItemCount,
            CpuSpatialTreeMaxDepth = Rendering.Stats.Octree.CpuSpatialTreeMaxDepth,
            CpuSpatialTreeUnboundedItemCount = Rendering.Stats.Octree.CpuSpatialTreeUnboundedItemCount,
            CpuSpatialTreeCollectMs = Rendering.Stats.Octree.CpuSpatialTreeCollectMs,
            CpuSpatialTreeMaxCollectMs = Rendering.Stats.Octree.CpuSpatialTreeMaxCollectMs,
            GpuRenderPipelineProfilingEnabled = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingEnabled,
            GpuRenderPipelineProfilingSupported = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingSupported,
            GpuRenderPipelineTimingsReady = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady,
            GpuRenderPipelineBackend = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineBackend,
            GpuRenderPipelineStatusMessage = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineStatusMessage,
            GpuRenderPipelineFrameMs = Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs,
            GpuRenderPipelineTimingRoots = Rendering.Stats.GpuPipelineProfiler.GetGpuRenderPipelineTimingRoots(),
        };
    }

    private static FrameOutputManifestData ConvertFrameOutputManifest(Rendering.Stats.FrameOutputManifestSnapshot snapshot)
    {
        var outputs = snapshot.Outputs ?? [];
        FrameOutputEntryData[] outputData = new FrameOutputEntryData[outputs.Length];
        for (int i = 0; i < outputs.Length; i++)
        {
            Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
            outputData[i] = new FrameOutputEntryData
            {
                FrameId = output.FrameId,
                OutputKind = output.OutputKind.ToString(),
                ViewKind = output.ViewKind.ToString(),
                OutputId = output.Request.OutputId,
                ViewFamilyId = output.Request.ViewFamilyId,
                OutputClass = output.Request.OutputClass.ToString(),
                Priority = output.Request.Schedule.Priority.ToString(),
                TargetClass = output.Request.Target.TargetClass.ToString(),
                StableTargetId = output.Request.Target.StableTargetId,
                TargetGeneration = output.Request.Target.TargetGeneration,
                DisplayWidth = output.Request.Target.DisplayWidth,
                DisplayHeight = output.Request.Target.DisplayHeight,
                InternalWidth = output.Request.Target.InternalWidth,
                InternalHeight = output.Request.Target.InternalHeight,
                TargetCompatibilityKey = output.Request.Target.CompatibilityKey,
                SampleCount = output.Request.Target.SampleCount,
                ViewMask = output.Request.Target.ViewMask,
                ExternalImageSlot = output.Request.Target.ExternalImageSlot,
                DesiredRateHz = output.Request.Schedule.DesiredRateHz,
                DeadlineMs = output.Request.Schedule.DeadlineMs,
                MaxCpuBudgetMs = output.Request.Schedule.MaxCpuBudgetMs,
                MaxGpuBudgetMs = output.Request.Schedule.MaxGpuBudgetMs,
                MaxContentAgeFrames = output.Request.Schedule.MaxContentAgeFrames,
                HardDeadline = output.Request.Schedule.HardDeadline,
                QualityRequirements = output.Request.QualityRequirements.ToString(),
                FallbackPolicy = output.Request.FallbackPolicy.ToString(),
                CompletionRequirement = output.Request.CompletionRequirement.ToString(),
                ProducerDependencySetId = output.Request.ProducerDependencySetId,
                ConsumerDependencySetId = output.Request.ConsumerDependencySetId,
                WorkDisposition = output.WorkDisposition.ToString(),
                ContentAgeFrames = output.ContentAgeFrames,
                DeadlineMissed = output.DeadlineMissed,
                PolicyAuthorized = output.PolicyAuthorized,
                PolicyReason = output.PolicyReason.ToString(),
                Name = output.Name,
                PipelineName = output.PipelineName,
                Active = output.Active,
                Rendered = output.Rendered,
                SceneRendered = output.SceneRendered,
                Mirror = output.Mirror,
                SeparateSceneRender = output.SeparateSceneRender,
                SharedVisibility = output.SharedVisibility,
                Due = output.Due,
                Skipped = output.Skipped,
                CadenceSkipped = output.CadenceSkipped,
                AutoSkipped = output.AutoSkipped,
                SkipReason = output.SkipReason.ToString(),
                ConfiguredTargetRateHz = output.ConfiguredTargetRateHz,
                SourceRateHz = output.SourceRateHz,
                AchievedRateHz = output.AchievedRateHz,
                TotalRenderCount = output.TotalRenderCount,
                TotalSkipCount = output.TotalSkipCount,
                CommandCount = output.CommandCount,
                DrawCalls = output.DrawCalls,
                MultiDrawCalls = output.MultiDrawCalls,
                Triangles = output.Triangles,
                CollectCpuMs = output.CollectCpuMs,
                SwapCpuMs = output.SwapCpuMs,
                RenderCpuMs = output.RenderCpuMs,
                SubmitCpuMs = output.SubmitCpuMs,
                OverlayCpuMs = output.OverlayCpuMs,
                PresentCpuMs = output.PresentCpuMs,
                GpuMs = output.GpuMs,
            };
        }

        return new FrameOutputManifestData
        {
            FrameId = snapshot.FrameId,
            VrActive = snapshot.VrActive,
            MirrorMode = snapshot.MirrorMode.ToString(),
            VisibilityPolicy = snapshot.VisibilityPolicy.ToString(),
            BudgetBand = snapshot.BudgetBand,
            BudgetMs = snapshot.BudgetMs,
            WholeFrameMs = snapshot.WholeFrameMs,
            WholeFrameP50Ms = snapshot.WholeFrameP50Ms,
            WholeFrameP90Ms = snapshot.WholeFrameP90Ms,
            WholeFrameP95Ms = snapshot.WholeFrameP95Ms,
            WholeFrameP99Ms = snapshot.WholeFrameP99Ms,
            WholeFrameWorstMs = snapshot.WholeFrameWorstMs,
            WorkloadIdentityHash = snapshot.WorkloadIdentityHash,
            OutputRequestCount = snapshot.Work.OutputRequestCount,
            OutputEventCount = snapshot.Work.OutputEventCount,
            CollectEventCount = snapshot.Work.CollectEventCount,
            SwapEventCount = snapshot.Work.SwapEventCount,
            RenderEventCount = snapshot.Work.RenderEventCount,
            SubmitEventCount = snapshot.Work.SubmitEventCount,
            OverlayEventCount = snapshot.Work.OverlayEventCount,
            PresentEventCount = snapshot.Work.PresentEventCount,
            UniqueViewFamilyCount = snapshot.Work.UniqueViewFamilyCount,
            TargetVariantCount = snapshot.Work.TargetVariantCount,
            SceneSnapshotCount = snapshot.Work.SceneSnapshotCount,
            VisibilityBuildCount = snapshot.Work.VisibilityBuildCount,
            CompiledPlanCacheHits = snapshot.Work.CompiledPlanCacheHits,
            CompiledPlanCacheMisses = snapshot.Work.CompiledPlanCacheMisses,
            SharedPassReuseCount = snapshot.Work.SharedPassReuseCount,
            RecordedWorkItemCount = snapshot.Work.RecordedWorkItemCount,
            ReusedWorkItemCount = snapshot.Work.ReusedWorkItemCount,
            DuplicatedWorkItemCount = snapshot.Work.DuplicatedWorkItemCount,
            CpuBudgetDeferralCount = snapshot.Work.CpuBudgetDeferralCount,
            GpuBudgetDeferralCount = snapshot.Work.GpuBudgetDeferralCount,
            StaleResultReuseCount = snapshot.Work.StaleResultReuseCount,
            MissedDeadlineCount = snapshot.Work.MissedDeadlineCount,
            UnapprovedPolicyEventCount = snapshot.Work.UnapprovedPolicyEventCount,
            SubmissionRejectionCount = snapshot.Work.SubmissionRejectionCount,
            PlannerPruneCount = snapshot.Work.PlannerPruneCount,
            GlobalInFlightWaitCount = snapshot.Work.GlobalInFlightWaitCount,
            ForceFlushCount = snapshot.Work.ForceFlushCount,
            Outputs = outputData,
        };
    }

    private static RenderProfilerOcclusionData CollectOcclusionProfilerData()
        => new()
        {
            EffectiveMode = OcclusionTelemetry.LastEffectiveMode.ToString(),
            SubmissionStrategy = OcclusionTelemetry.LastSubmissionStrategy.ToString(),
            CpuPassesActive = OcclusionTelemetry.CpuPassesActive,
            CpuPassesSkippedNoCamera = OcclusionTelemetry.CpuPassesSkippedNoCamera,
            CpuPassesSkippedShadow = OcclusionTelemetry.CpuPassesSkippedShadow,
            CpuPassesSkippedDepthNormalPrePass = OcclusionTelemetry.CpuPassesSkippedDepthNormalPrePass,
            CpuPassesSkippedModeOff = OcclusionTelemetry.CpuPassesSkippedModeOff,
            CpuTested = OcclusionTelemetry.CpuTested,
            CpuCulled = OcclusionTelemetry.CpuCulled,
            CpuRendered = OcclusionTelemetry.CpuRendered,
            CpuDecisionSeed = OcclusionTelemetry.CpuDecisionSeed,
            CpuDecisionCached = OcclusionTelemetry.CpuDecisionCached,
            CpuDecisionVisibleQuery = OcclusionTelemetry.CpuDecisionVisibleQuery,
            CpuDecisionVisibleHysteresis = OcclusionTelemetry.CpuDecisionVisibleHysteresis,
            CpuDecisionProbe = OcclusionTelemetry.CpuDecisionProbe,
            CpuDecisionSkip = OcclusionTelemetry.CpuDecisionSkip,
            CpuDecisionForcedVisible = OcclusionTelemetry.CpuDecisionForcedVisible,
            CpuMotionTier = OcclusionTelemetry.CpuMotionTier.ToString(),
            CpuActiveViewScope = OcclusionTelemetry.CpuActiveViewScope.ToString(),
            CpuGlobalConservativeFrames = OcclusionTelemetry.CpuGlobalConservativeFrames,
            CpuPendingQueries = OcclusionTelemetry.CpuPendingQueries,
            CpuQuerySubmittedTotal = OcclusionTelemetry.CpuQuerySubmittedTotal,
            CpuQueryResolvedTotal = OcclusionTelemetry.CpuQueryResolvedTotal,
            CpuQueryLatencySamples = OcclusionTelemetry.CpuQueryLatencySamples,
            CpuQueryLatencyAverageFrames = OcclusionTelemetry.CpuQueryLatencyAverageFrames,
            CpuQueryLatencyMaxFrames = OcclusionTelemetry.CpuQueryLatencyMaxFrames,
            CpuBudgetSkippedTotal = OcclusionTelemetry.CpuBudgetSkippedTotal,
            CpuForcedVisibleTotal = OcclusionTelemetry.CpuForcedVisibleTotal,
            CpuUnsupportedStereoQueryMode = OcclusionTelemetry.CpuUnsupportedStereoQueryMode,
            CpuQueryAsyncSubmitted = OcclusionTelemetry.CpuQueryAsyncSubmitted,
            CpuQueryAsyncResolved = OcclusionTelemetry.CpuQueryAsyncResolved,
            CpuQueryAsyncOccluded = OcclusionTelemetry.CpuQueryAsyncOccluded,
            CpuSocTested = OcclusionTelemetry.CpuSocTested,
            CpuSocCulled = OcclusionTelemetry.CpuSocCulled,
        };

    private static ThreadAllocationsPacket? CollectThreadAllocations()
    {
        var snap = Allocations.GetSnapshot();
        return new ThreadAllocationsPacket
        {
            Render = ToSlice(snap.Render),
            CollectSwap = ToSlice(snap.CollectSwap),
            Update = ToSlice(snap.Update),
            FixedUpdate = ToSlice(snap.FixedUpdate),
            Scopes = ToScopeSlices(snap.Scopes),
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

    private static AllocationScopeSlice[] ToScopeSlices(AllocationScopeSnapshot[] scopes)
    {
        if (scopes.Length == 0)
            return [];

        AllocationScopeSlice[] slices = new AllocationScopeSlice[scopes.Length];
        for (int i = 0; i < scopes.Length; i++)
        {
            AllocationScopeSnapshot scope = scopes[i];
            slices[i] = new AllocationScopeSlice
            {
                Name = scope.Name,
                Category = scope.Category,
                BudgetBytes = scope.BudgetBytes,
                LastBytes = scope.LastBytes,
                AverageBytes = scope.AverageBytes,
                MaxBytes = scope.MaxBytes,
                Samples = scope.Samples,
                Capacity = scope.Capacity,
                OverBudgetCount = scope.OverBudgetCount,
            };
        }

        return slices;
    }

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

    private static ShadowAtlasSolveDiagnosticsData ConvertShadowAtlasSolveDiagnostics(ShadowAtlasSolveDiagnostics diagnostics)
        => new()
        {
            ElapsedMilliseconds = diagnostics.ElapsedMilliseconds,
            ClassifiedRequestCount = diagnostics.ClassifiedRequestCount,
            DirectionalRequestCount = diagnostics.DirectionalRequestCount,
            SpotRequestCount = diagnostics.SpotRequestCount,
            PointRequestCount = diagnostics.PointRequestCount,
            DepthRequestCount = diagnostics.DepthRequestCount,
            Variance2RequestCount = diagnostics.Variance2RequestCount,
            ExponentialVariance2RequestCount = diagnostics.ExponentialVariance2RequestCount,
            ExponentialVariance4RequestCount = diagnostics.ExponentialVariance4RequestCount,
            BalancedSolveAttemptCount = diagnostics.BalancedSolveAttemptCount,
            FailedCandidateCount = diagnostics.FailedCandidateCount,
            DemotionCount = diagnostics.DemotionCount,
            StickyDemotionCount = diagnostics.StickyDemotionCount,
            DirectionalGroupDemotionCount = diagnostics.DirectionalGroupDemotionCount,
            DeterministicFallbackDemotionCount = diagnostics.DeterministicFallbackDemotionCount,
            PriorReserveHitCount = diagnostics.PriorReserveHitCount,
            PriorReserveMissCount = diagnostics.PriorReserveMissCount,
            PriorSubBlockHitCount = diagnostics.PriorSubBlockHitCount,
            PriorSubBlockMissCount = diagnostics.PriorSubBlockMissCount,
            PageAllocationAttemptCount = diagnostics.PageAllocationAttemptCount,
            PageAllocationSuccessCount = diagnostics.PageAllocationSuccessCount,
            PageCreateAttemptCount = diagnostics.PageCreateAttemptCount,
            PageCreateSuccessCount = diagnostics.PageCreateSuccessCount,
            PageClearCount = diagnostics.PageClearCount,
            DirectionalGroupSeedCount = diagnostics.DirectionalGroupSeedCount,
            DirectionalGroupMemberCount = diagnostics.DirectionalGroupMemberCount,
            DirectionalGroupCoLocationFailureCount = diagnostics.DirectionalGroupCoLocationFailureCount,
            PointGroupSeedCount = diagnostics.PointGroupSeedCount,
            PointGroupMemberCount = diagnostics.PointGroupMemberCount,
            PointGroupCoLocationFailureCount = diagnostics.PointGroupCoLocationFailureCount,
            IncrementalReuseCount = diagnostics.IncrementalReuseCount,
            WaterlineDemotionCount = diagnostics.WaterlineDemotionCount,
            LastFailureReason = diagnostics.LastFailureReason.ToString(),
        };

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
