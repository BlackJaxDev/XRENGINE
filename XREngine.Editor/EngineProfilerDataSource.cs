using System.Diagnostics;
using XREngine;
using XREngine.Data.Profiling;
using XREngine.Profiler.UI;
using XREngine.Rendering.Shadows;

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
        => CollectFromEngine(ProfilerPanelRenderer.PanelVisibility.All);

    /// <summary>
    /// Collects only the telemetry needed by the visible in-editor profiler panels.
    /// </summary>
    public void CollectFromEngine(ProfilerPanelRenderer.PanelVisibility visibility)
    {
        _latestFrame = visibility.NeedsFrame
            ? CollectProfilerFrame(visibility.NeedsThreadTiming, visibility.ComponentTimings)
            : null;
        _latestRenderStats = visibility.RenderStats
            ? CollectRenderStats()
            : visibility.GpuPipeline
                ? CollectGpuPipelineStats()
                : null;
        _latestAllocations = visibility.ThreadAllocations ? CollectThreadAllocations() : null;
        _latestBvhMetrics = visibility.BvhMetrics ? CollectBvhMetrics() : null;
        _latestJobStats = visibility.JobSystem ? CollectJobSystemStats() : null;
        _latestMainThreadInvokes = visibility.MainThreadInvokes ? CollectMainThreadInvokes() : null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Collectors — same patterns as Engine.ProfilerSender.cs
    // ═══════════════════════════════════════════════════════════════

    private static ProfilerFramePacket? CollectProfilerFrame(bool includeThreadTimings, bool includeComponentTimings)
    {
        if (!Engine.Profiler.TryGetSnapshot(out var snapshot, out var history) || snapshot is null)
            return null;

        ProfilerThreadData[] threads = [];
        if (includeThreadTimings)
        {
            threads = new ProfilerThreadData[snapshot.Threads.Count];
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
        }

        return new ProfilerFramePacket
        {
            FrameTime = snapshot.FrameTime,
            Threads = threads,
            ThreadHistory = includeThreadTimings ? history ?? [] : [],
            ComponentTimings = includeComponentTimings
                ? ConvertComponentTimings(snapshot.ComponentTimings?.Components)
                : [],
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

        var assetRowsSnapshot = Engine.Rendering.Stats.SceneAssets.GetAssetCostRows();
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

        ShadowAtlasSolveDiagnostics shadowAtlasSolve = Engine.Rendering.Stats.ShadowAtlas.LastSolveDiagnostics;
        return new RenderStatsPacket
        {
            DrawCalls = Engine.Rendering.Stats.Frame.DrawCalls,
            MultiDrawCalls = Engine.Rendering.Stats.Frame.MultiDrawCalls,
            TrianglesRendered = Engine.Rendering.Stats.Frame.TrianglesRendered,
            GpuCpuFallbackEvents = Engine.Rendering.Stats.GpuFallback.GpuCpuFallbackEvents,
            GpuCpuFallbackRecoveredCommands = Engine.Rendering.Stats.GpuFallback.GpuCpuFallbackRecoveredCommands,
            ForbiddenGpuFallbackEvents = Engine.Rendering.Stats.GpuFallback.ForbiddenGpuFallbackEvents,
            GpuMappedBuffers = Engine.Rendering.Stats.GpuReadback.GpuMappedBuffers,
            GpuReadbackBytes = Engine.Rendering.Stats.GpuReadback.GpuReadbackBytes,
            ShadowAtlasSolve = ConvertShadowAtlasSolveDiagnostics(shadowAtlasSolve),
            RenderProfilerV2 = new RenderProfilerV2Data
            {
                RendererState = new RenderProfilerRendererStateData
                {
                    IndirectCountCalls = Engine.Rendering.Stats.RendererState.IndirectCountCalls,
                    ShaderProgramSwitches = Engine.Rendering.Stats.RendererState.ShaderProgramSwitches,
                    ProgramPipelineSwitches = Engine.Rendering.Stats.RendererState.ProgramPipelineSwitches,
                    VaoBinds = Engine.Rendering.Stats.RendererState.VaoBinds,
                    VaoBindSkips = Engine.Rendering.Stats.RendererState.VaoBindSkips,
                    ArrayBufferBinds = Engine.Rendering.Stats.RendererState.ArrayBufferBinds,
                    ElementArrayBufferBinds = Engine.Rendering.Stats.RendererState.ElementArrayBufferBinds,
                    DrawIndirectBufferBinds = Engine.Rendering.Stats.RendererState.DrawIndirectBufferBinds,
                    ParameterBufferBinds = Engine.Rendering.Stats.RendererState.ParameterBufferBinds,
                    SsboBinds = Engine.Rendering.Stats.RendererState.SsboBinds,
                    UboBinds = Engine.Rendering.Stats.RendererState.UboBinds,
                    TextureBinds = Engine.Rendering.Stats.RendererState.TextureBinds,
                    TextureBindSkips = Engine.Rendering.Stats.RendererState.TextureBindSkips,
                    TextureUnitSwitches = Engine.Rendering.Stats.RendererState.TextureUnitSwitches,
                    UniformCalls = Engine.Rendering.Stats.RendererState.UniformCalls,
                    SamplerUniformCalls = Engine.Rendering.Stats.RendererState.SamplerUniformCalls,
                    BufferUploadBytes = Engine.Rendering.Stats.RendererState.BufferUploadBytes,
                    BarrierCalls = Engine.Rendering.Stats.RendererState.BarrierCalls,
                    BarrierAll = Engine.Rendering.Stats.RendererState.BarrierAll,
                    BarrierCommand = Engine.Rendering.Stats.RendererState.BarrierCommand,
                    BarrierBufferUpdate = Engine.Rendering.Stats.RendererState.BarrierBufferUpdate,
                    BarrierShaderStorage = Engine.Rendering.Stats.RendererState.BarrierShaderStorage,
                    BarrierTextureFetch = Engine.Rendering.Stats.RendererState.BarrierTextureFetch,
                    BarrierTextureUpdate = Engine.Rendering.Stats.RendererState.BarrierTextureUpdate,
                    BarrierFramebuffer = Engine.Rendering.Stats.RendererState.BarrierFramebuffer,
                    TimestampQueryCount = Engine.Rendering.Stats.RendererState.TimestampQueryCount,
                    TimestampQueryReadbackBytes = Engine.Rendering.Stats.RendererState.TimestampQueryReadbackBytes,
                    TimestampDenseModeFrames = Engine.Rendering.Stats.RendererState.TimestampDenseModeFrames,
                    RedundantStateSkips = Engine.Rendering.Stats.RendererState.RedundantStateSkips,
                    CpuDirectDrawCalls = Engine.Rendering.Stats.RendererState.CpuDirectDrawCalls,
                    GpuIndirectDrawCalls = Engine.Rendering.Stats.RendererState.GpuIndirectDrawCalls,
                    GpuMeshletDrawCalls = Engine.Rendering.Stats.RendererState.GpuMeshletDrawCalls,
                    UnknownStrategyDrawCalls = Engine.Rendering.Stats.RendererState.UnknownStrategyDrawCalls,
                    ActiveTextureBindingRung = Engine.Rendering.Stats.RendererState.ActiveTextureBindingRung,
                    ActiveStereoMode = Engine.Rendering.Stats.RendererState.ActiveStereoMode,
                    ActiveSubmissionStrategy = Engine.Rendering.Stats.RendererState.ActiveSubmissionStrategy,
                    ActiveRenderBackend = Engine.Rendering.Stats.RendererState.ActiveRenderBackend,
                    ValidationLayersEnabled = Engine.Rendering.Stats.RendererState.ValidationLayersEnabled,
                    DebugOutputEnabled = Engine.Rendering.Stats.RendererState.DebugOutputEnabled,
                    GpuTimestampsDenseMode = Engine.Rendering.Stats.RendererState.GpuTimestampsDenseMode,
                },
                SceneAssets = new RenderProfilerSceneAssetData
                {
                    VisibleRendererCount = Engine.Rendering.Stats.SceneAssets.VisibleRendererCount,
                    VisibleSubmeshCount = Engine.Rendering.Stats.SceneAssets.VisibleSubmeshCount,
                    VisibleTriangleCount = Engine.Rendering.Stats.SceneAssets.VisibleTriangleCount,
                    MaterialSlotCount = Engine.Rendering.Stats.SceneAssets.MaterialSlotCount,
                    ActiveMaterialCount = Engine.Rendering.Stats.SceneAssets.ActiveMaterialCount,
                    TextureCount = Engine.Rendering.Stats.SceneAssets.TextureCount,
                    ResidentTextureMemoryBytes = Engine.Rendering.Stats.SceneAssets.ResidentTextureMemoryBytes,
                    TextureUploadJobs = Engine.Rendering.Stats.SceneAssets.TextureUploadJobs,
                    TextureUploadBytes = Engine.Rendering.Stats.SceneAssets.TextureUploadBytes,
                    TextureUploadMs = Engine.Rendering.Stats.SceneAssets.TextureUploadMs,
                    ShaderVariantsRequested = Engine.Rendering.Stats.SceneAssets.ShaderVariantsRequested,
                    ShaderVariantsWarming = Engine.Rendering.Stats.SceneAssets.ShaderVariantsWarming,
                    ShaderVariantsLinked = Engine.Rendering.Stats.SceneAssets.ShaderVariantsLinked,
                    ShaderVariantsFailed = Engine.Rendering.Stats.SceneAssets.ShaderVariantsFailed,
                    ShaderVariantsLoadedFromDiskCache = Engine.Rendering.Stats.SceneAssets.ShaderVariantsLoadedFromDiskCache,
                    ShaderVariantsGeneratedThisRun = Engine.Rendering.Stats.SceneAssets.ShaderVariantsGeneratedThisRun,
                    SkinnedRendererCount = Engine.Rendering.Stats.SceneAssets.SkinnedRendererCount,
                    BoneMatrixUploadBytes = Engine.Rendering.Stats.SceneAssets.BoneMatrixUploadBytes,
                    BlendshapeWeightUploadBytes = Engine.Rendering.Stats.SceneAssets.BlendshapeWeightUploadBytes,
                    BlendshapeActiveListUploadBytes = Engine.Rendering.Stats.SceneAssets.BlendshapeActiveListUploadBytes,
                    BlendshapeDeltaBytes = Engine.Rendering.Stats.SceneAssets.BlendshapeDeltaBytes,
                    SkinningCoreInfluenceBytes = Engine.Rendering.Stats.SceneAssets.SkinningCoreInfluenceBytes,
                    SkinningSpillHeaderBytes = Engine.Rendering.Stats.SceneAssets.SkinningSpillHeaderBytes,
                    SkinningSpillEntryBytes = Engine.Rendering.Stats.SceneAssets.SkinningSpillEntryBytes,
                    SkinPaletteUploadBytes = Engine.Rendering.Stats.SceneAssets.SkinPaletteUploadBytes,
                    SkinningComputeDispatchCount = Engine.Rendering.Stats.SceneAssets.SkinningComputeDispatchCount,
                    BlendshapeComputeDispatchCount = Engine.Rendering.Stats.SceneAssets.BlendshapeComputeDispatchCount,
                    SkippedSkinningComputeDispatchCount = Engine.Rendering.Stats.SceneAssets.SkippedSkinningComputeDispatchCount,
                    SkippedBlendshapeComputeDispatchCount = Engine.Rendering.Stats.SceneAssets.SkippedBlendshapeComputeDispatchCount,
                    ReusedSkinnedOutputBufferCount = Engine.Rendering.Stats.SceneAssets.ReusedSkinnedOutputBufferCount,
                    LiveSkinningShaderPermutationCount = Engine.Rendering.Stats.SceneAssets.LiveSkinningShaderPermutationCount,
                    BlendshapeAuthoredShapeCount = Engine.Rendering.Stats.SceneAssets.BlendshapeAuthoredShapeCount,
                    BlendshapeActiveShapeCount = Engine.Rendering.Stats.SceneAssets.BlendshapeActiveShapeCount,
                    BlendshapeAffectedVertexCount = Engine.Rendering.Stats.SceneAssets.BlendshapeAffectedVertexCount,
                    CompactedActiveBlendshapeCount = Engine.Rendering.Stats.SceneAssets.CompactedActiveBlendshapeCount,
                    LiveBlendshapeShaderPermutationCount = Engine.Rendering.Stats.SceneAssets.LiveBlendshapeShaderPermutationCount,
                    AvatarSourceMeshCount = Engine.Rendering.Stats.SceneAssets.AvatarSourceMeshCount,
                    AvatarOptimizedLodCount = Engine.Rendering.Stats.SceneAssets.AvatarOptimizedLodCount,
                    AvatarMeshletCount = Engine.Rendering.Stats.SceneAssets.AvatarMeshletCount,
                    AvatarVisibilityBufferCount = Engine.Rendering.Stats.SceneAssets.AvatarVisibilityBufferCount,
                    AvatarClusterVirtualizedCount = Engine.Rendering.Stats.SceneAssets.AvatarClusterVirtualizedCount,
                    AvatarOctahedralImpostorCount = Engine.Rendering.Stats.SceneAssets.AvatarOctahedralImpostorCount,
                    AvatarGaussianSplatCount = Engine.Rendering.Stats.SceneAssets.AvatarGaussianSplatCount,
                    RenderAssetCostRows = assetRows,
                },
                GpuDriven = new RenderProfilerGpuDrivenData
                {
                    GpuDrivenCulledCommandCount = Engine.Rendering.Stats.GpuDriven.CulledCommandCount,
                    GpuDrivenActiveBucketCount = Engine.Rendering.Stats.GpuDriven.ActiveBucketCount,
                    GpuDrivenEmptyBucketSkips = Engine.Rendering.Stats.GpuDriven.EmptyBucketSkips,
                    GpuDrivenFullBucketScans = Engine.Rendering.Stats.GpuDriven.FullBucketScans,
                    GpuDrivenMaterialScatterDispatches = Engine.Rendering.Stats.GpuDriven.MaterialScatterDispatches,
                    GpuDrivenIndirectCommandGenerationMs = Engine.Rendering.Stats.GpuDriven.IndirectCommandGenerationMs,
                    GpuDrivenGpuCullMs = Engine.Rendering.Stats.GpuDriven.GpuCullMs,
                    GpuDrivenGpuSortCompactMs = Engine.Rendering.Stats.GpuDriven.GpuSortCompactMs,
                    GpuDrivenDelayedDrawCountBufferValue = Engine.Rendering.Stats.GpuDriven.DelayedDrawCountBufferValue,
                    GpuDrivenDelayedDiagnosticReadbackBytes = Engine.Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackBytes,
                    GpuDrivenDelayedDiagnosticReadbackCount = Engine.Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackCount,
                    GpuCompactionOverflow = Engine.Rendering.Stats.GpuDriven.GpuCompactionOverflow,
                    GpuActiveListOverflow = Engine.Rendering.Stats.GpuDriven.ActiveListOverflow,
                    GpuBucketOverflow = Engine.Rendering.Stats.GpuDriven.BucketOverflow,
                    GpuMeshletOverflow = Engine.Rendering.Stats.GpuDriven.MeshletOverflow,
                    GpuHiZMode = Engine.Rendering.Stats.GpuDriven.HiZMode,
                    GpuHiZOnePhaseFrames = Engine.Rendering.Stats.GpuDriven.HiZOnePhaseFrames,
                    GpuHiZTwoPhaseFrames = Engine.Rendering.Stats.GpuDriven.HiZTwoPhaseFrames,
                    GpuHiZPhaseOneDraws = Engine.Rendering.Stats.GpuDriven.HiZPhaseOneDraws,
                    GpuHiZPhaseTwoDraws = Engine.Rendering.Stats.GpuDriven.HiZPhaseTwoDraws,
                    VisibilityPassDraws = Engine.Rendering.Stats.GpuDriven.VisibilityPassDraws,
                    VisibilityClassifiedPixels = Engine.Rendering.Stats.GpuDriven.VisibilityClassifiedPixels,
                    VisibilityActiveMaterialTiles = Engine.Rendering.Stats.GpuDriven.VisibilityActiveMaterialTiles,
                    VisibilityClassificationOverflow = Engine.Rendering.Stats.GpuDriven.VisibilityClassificationOverflow,
                    VisibilityReconstructionMs = Engine.Rendering.Stats.GpuDriven.VisibilityReconstructionMs,
                    VisibilityMaterialShadingMs = Engine.Rendering.Stats.GpuDriven.VisibilityMaterialShadingMs,
                },
            },
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

    private static RenderStatsPacket CollectGpuPipelineStats()
        => new()
        {
            GpuRenderPipelineProfilingEnabled = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingEnabled,
            GpuRenderPipelineProfilingSupported = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingSupported,
            GpuRenderPipelineTimingsReady = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady,
            GpuRenderPipelineBackend = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineBackend,
            GpuRenderPipelineStatusMessage = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineStatusMessage,
            GpuRenderPipelineFrameMs = Engine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs,
            GpuRenderPipelineTimingRoots = Engine.Rendering.Stats.GpuPipelineProfiler.GetGpuRenderPipelineTimingRoots(),
        };

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
            LastFailureReason = diagnostics.LastFailureReason.ToString(),
        };

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
