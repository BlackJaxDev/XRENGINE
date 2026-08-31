using System.Diagnostics;
using XREngine;
using XREngine.Data.Profiling;
using XREngine.Profiler.UI;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using OcclusionTelemetry = XREngine.Rendering.Occlusion.OcclusionTelemetry;

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
        long start = Stopwatch.GetTimestamp();
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
        ProfilerObserverTelemetry.RecordIngestion(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
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
                    WallTimeMs = t.WallTimeMs,
                    DownstreamRenderPressureMs = t.DownstreamRenderPressureMs,
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
        var listenerSnapshot = RuntimeEngine.Rendering.Stats.RenderMatrix.GetRenderMatrixListenerSnapshot();
        var listenerEntries = new RenderMatrixListenerEntry[listenerSnapshot.Length];
        for (int i = 0; i < listenerSnapshot.Length; i++)
        {
            listenerEntries[i] = new RenderMatrixListenerEntry
            {
                Name = listenerSnapshot[i].Key,
                Count = listenerSnapshot[i].Value,
            };
        }

        var assetRowsSnapshot = RuntimeEngine.Rendering.Stats.SceneAssets.GetAssetCostRows();
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

        ShadowAtlasSolveDiagnostics shadowAtlasSolve = RuntimeEngine.Rendering.Stats.ShadowAtlas.LastSolveDiagnostics;
        VulkanFrameTelemetryPublication vulkanFrame = RuntimeEngine.Rendering.Stats.Vulkan.LatestVulkanFrameTelemetry;
        return new RenderStatsPacket
        {
            DrawCalls = RuntimeEngine.Rendering.Stats.Frame.DrawCalls,
            MultiDrawCalls = RuntimeEngine.Rendering.Stats.Frame.MultiDrawCalls,
            TrianglesRendered = RuntimeEngine.Rendering.Stats.Frame.TrianglesRendered,
            GpuCpuFallbackEvents = RuntimeEngine.Rendering.Stats.GpuFallback.GpuCpuFallbackEvents,
            GpuCpuFallbackRecoveredCommands = RuntimeEngine.Rendering.Stats.GpuFallback.GpuCpuFallbackRecoveredCommands,
            ForbiddenGpuFallbackEvents = RuntimeEngine.Rendering.Stats.GpuFallback.ForbiddenGpuFallbackEvents,
            GpuMappedBuffers = RuntimeEngine.Rendering.Stats.GpuReadback.GpuMappedBuffers,
            GpuReadbackBytes = RuntimeEngine.Rendering.Stats.GpuReadback.GpuReadbackBytes,
            ShadowAtlasSolve = ConvertShadowAtlasSolveDiagnostics(shadowAtlasSolve),
            RenderProfilerV2 = new RenderProfilerV2Data
            {
                RendererState = new RenderProfilerRendererStateData
                {
                    IndirectCountCalls = RuntimeEngine.Rendering.Stats.RendererState.IndirectCountCalls,
                    ShaderProgramSwitches = RuntimeEngine.Rendering.Stats.RendererState.ShaderProgramSwitches,
                    ProgramPipelineSwitches = RuntimeEngine.Rendering.Stats.RendererState.ProgramPipelineSwitches,
                    VaoBinds = RuntimeEngine.Rendering.Stats.RendererState.VaoBinds,
                    VaoBindSkips = RuntimeEngine.Rendering.Stats.RendererState.VaoBindSkips,
                    ArrayBufferBinds = RuntimeEngine.Rendering.Stats.RendererState.ArrayBufferBinds,
                    ElementArrayBufferBinds = RuntimeEngine.Rendering.Stats.RendererState.ElementArrayBufferBinds,
                    DrawIndirectBufferBinds = RuntimeEngine.Rendering.Stats.RendererState.DrawIndirectBufferBinds,
                    ParameterBufferBinds = RuntimeEngine.Rendering.Stats.RendererState.ParameterBufferBinds,
                    SsboBinds = RuntimeEngine.Rendering.Stats.RendererState.SsboBinds,
                    UboBinds = RuntimeEngine.Rendering.Stats.RendererState.UboBinds,
                    TextureBinds = RuntimeEngine.Rendering.Stats.RendererState.TextureBinds,
                    TextureBindSkips = RuntimeEngine.Rendering.Stats.RendererState.TextureBindSkips,
                    TextureUnitSwitches = RuntimeEngine.Rendering.Stats.RendererState.TextureUnitSwitches,
                    UniformCalls = RuntimeEngine.Rendering.Stats.RendererState.UniformCalls,
                    SamplerUniformCalls = RuntimeEngine.Rendering.Stats.RendererState.SamplerUniformCalls,
                    BufferUploadBytes = RuntimeEngine.Rendering.Stats.RendererState.BufferUploadBytes,
                    BarrierCalls = RuntimeEngine.Rendering.Stats.RendererState.BarrierCalls,
                    BarrierAll = RuntimeEngine.Rendering.Stats.RendererState.BarrierAll,
                    BarrierCommand = RuntimeEngine.Rendering.Stats.RendererState.BarrierCommand,
                    BarrierBufferUpdate = RuntimeEngine.Rendering.Stats.RendererState.BarrierBufferUpdate,
                    BarrierShaderStorage = RuntimeEngine.Rendering.Stats.RendererState.BarrierShaderStorage,
                    BarrierTextureFetch = RuntimeEngine.Rendering.Stats.RendererState.BarrierTextureFetch,
                    BarrierTextureUpdate = RuntimeEngine.Rendering.Stats.RendererState.BarrierTextureUpdate,
                    BarrierFramebuffer = RuntimeEngine.Rendering.Stats.RendererState.BarrierFramebuffer,
                    TimestampQueryCount = RuntimeEngine.Rendering.Stats.RendererState.TimestampQueryCount,
                    TimestampQueryReadbackBytes = RuntimeEngine.Rendering.Stats.RendererState.TimestampQueryReadbackBytes,
                    TimestampDenseModeFrames = RuntimeEngine.Rendering.Stats.RendererState.TimestampDenseModeFrames,
                    RedundantStateSkips = RuntimeEngine.Rendering.Stats.RendererState.RedundantStateSkips,
                    CpuDirectDrawCalls = RuntimeEngine.Rendering.Stats.RendererState.CpuDirectDrawCalls,
                    GpuIndirectDrawCalls = RuntimeEngine.Rendering.Stats.RendererState.GpuIndirectDrawCalls,
                    GpuMeshletDrawCalls = RuntimeEngine.Rendering.Stats.RendererState.GpuMeshletDrawCalls,
                    UnknownStrategyDrawCalls = RuntimeEngine.Rendering.Stats.RendererState.UnknownStrategyDrawCalls,
                    ActiveTextureBindingRung = RuntimeEngine.Rendering.Stats.RendererState.ActiveTextureBindingRung,
                    ActiveStereoMode = RuntimeEngine.Rendering.Stats.RendererState.ActiveStereoMode,
                    ActiveSubmissionStrategy = RuntimeEngine.Rendering.Stats.RendererState.ActiveSubmissionStrategy,
                    ActiveRenderBackend = RuntimeEngine.Rendering.Stats.RendererState.ActiveRenderBackend,
                    AdvancedPipelineMode = RuntimeEngine.Rendering.Stats.RendererState.AdvancedPipelineMode,
                    AdvancedPipelineEffectiveKind = RuntimeEngine.Rendering.Stats.RendererState.AdvancedPipelineEffectiveKind,
                    AdvancedPipelineCapabilities = RuntimeEngine.Rendering.Stats.RendererState.AdvancedPipelineCapabilities,
                    AdvancedPipelineRejectionReason = RuntimeEngine.Rendering.Stats.RendererState.AdvancedPipelineRejectionReason,
                    AdvancedPipelineCapabilityEvaluated = RuntimeEngine.Rendering.Stats.RendererState.AdvancedPipelineCapabilityEvaluated,
                    AdvancedPipelineSupported = RuntimeEngine.Rendering.Stats.RendererState.AdvancedPipelineSupported,
                    ValidationLayersEnabled = RuntimeEngine.Rendering.Stats.RendererState.ValidationLayersEnabled,
                    DebugOutputEnabled = RuntimeEngine.Rendering.Stats.RendererState.DebugOutputEnabled,
                    GpuTimestampsDenseMode = RuntimeEngine.Rendering.Stats.RendererState.GpuTimestampsDenseMode,
                },
                SceneAssets = new RenderProfilerSceneAssetData
                {
                    VisibleRendererCount = RuntimeEngine.Rendering.Stats.SceneAssets.VisibleRendererCount,
                    VisibleSubmeshCount = RuntimeEngine.Rendering.Stats.SceneAssets.VisibleSubmeshCount,
                    VisibleTriangleCount = RuntimeEngine.Rendering.Stats.SceneAssets.VisibleTriangleCount,
                    MaterialSlotCount = RuntimeEngine.Rendering.Stats.SceneAssets.MaterialSlotCount,
                    ActiveMaterialCount = RuntimeEngine.Rendering.Stats.SceneAssets.ActiveMaterialCount,
                    TextureCount = RuntimeEngine.Rendering.Stats.SceneAssets.TextureCount,
                    ResidentTextureMemoryBytes = RuntimeEngine.Rendering.Stats.SceneAssets.ResidentTextureMemoryBytes,
                    TextureUploadJobs = RuntimeEngine.Rendering.Stats.SceneAssets.TextureUploadJobs,
                    TextureUploadBytes = RuntimeEngine.Rendering.Stats.SceneAssets.TextureUploadBytes,
                    TextureUploadMs = RuntimeEngine.Rendering.Stats.SceneAssets.TextureUploadMs,
                    ShaderVariantsRequested = RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsRequested,
                    ShaderVariantsWarming = RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsWarming,
                    ShaderVariantsLinked = RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsLinked,
                    ShaderVariantsFailed = RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsFailed,
                    ShaderVariantsLoadedFromDiskCache = RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsLoadedFromDiskCache,
                    ShaderVariantsGeneratedThisRun = RuntimeEngine.Rendering.Stats.SceneAssets.ShaderVariantsGeneratedThisRun,
                    SkinnedRendererCount = RuntimeEngine.Rendering.Stats.SceneAssets.SkinnedRendererCount,
                    BoneMatrixUploadBytes = RuntimeEngine.Rendering.Stats.SceneAssets.BoneMatrixUploadBytes,
                    BlendshapeWeightUploadBytes = RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeWeightUploadBytes,
                    BlendshapeActiveListUploadBytes = RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeActiveListUploadBytes,
                    BlendshapeDeltaBytes = RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeDeltaBytes,
                    SkinningCoreInfluenceBytes = RuntimeEngine.Rendering.Stats.SceneAssets.SkinningCoreInfluenceBytes,
                    SkinningSpillHeaderBytes = RuntimeEngine.Rendering.Stats.SceneAssets.SkinningSpillHeaderBytes,
                    SkinningSpillEntryBytes = RuntimeEngine.Rendering.Stats.SceneAssets.SkinningSpillEntryBytes,
                    SkinPaletteUploadBytes = RuntimeEngine.Rendering.Stats.SceneAssets.SkinPaletteUploadBytes,
                    SkinningComputeDispatchCount = RuntimeEngine.Rendering.Stats.SceneAssets.SkinningComputeDispatchCount,
                    BlendshapeComputeDispatchCount = RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeComputeDispatchCount,
                    SkippedSkinningComputeDispatchCount = RuntimeEngine.Rendering.Stats.SceneAssets.SkippedSkinningComputeDispatchCount,
                    SkippedBlendshapeComputeDispatchCount = RuntimeEngine.Rendering.Stats.SceneAssets.SkippedBlendshapeComputeDispatchCount,
                    ReusedSkinnedOutputBufferCount = RuntimeEngine.Rendering.Stats.SceneAssets.ReusedSkinnedOutputBufferCount,
                    LiveSkinningShaderPermutationCount = RuntimeEngine.Rendering.Stats.SceneAssets.LiveSkinningShaderPermutationCount,
                    BlendshapeAuthoredShapeCount = RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeAuthoredShapeCount,
                    BlendshapeActiveShapeCount = RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeActiveShapeCount,
                    BlendshapeAffectedVertexCount = RuntimeEngine.Rendering.Stats.SceneAssets.BlendshapeAffectedVertexCount,
                    CompactedActiveBlendshapeCount = RuntimeEngine.Rendering.Stats.SceneAssets.CompactedActiveBlendshapeCount,
                    LiveBlendshapeShaderPermutationCount = RuntimeEngine.Rendering.Stats.SceneAssets.LiveBlendshapeShaderPermutationCount,
                    AvatarSourceMeshCount = RuntimeEngine.Rendering.Stats.SceneAssets.AvatarSourceMeshCount,
                    AvatarOptimizedLodCount = RuntimeEngine.Rendering.Stats.SceneAssets.AvatarOptimizedLodCount,
                    AvatarMeshletCount = RuntimeEngine.Rendering.Stats.SceneAssets.AvatarMeshletCount,
                    AvatarVisibilityBufferCount = RuntimeEngine.Rendering.Stats.SceneAssets.AvatarVisibilityBufferCount,
                    AvatarClusterVirtualizedCount = RuntimeEngine.Rendering.Stats.SceneAssets.AvatarClusterVirtualizedCount,
                    AvatarOctahedralImpostorCount = RuntimeEngine.Rendering.Stats.SceneAssets.AvatarOctahedralImpostorCount,
                    AvatarGaussianSplatCount = RuntimeEngine.Rendering.Stats.SceneAssets.AvatarGaussianSplatCount,
                    RenderAssetCostRows = assetRows,
                },
                GpuDriven = new RenderProfilerGpuDrivenData
                {
                    GpuDrivenCulledCommandCount = RuntimeEngine.Rendering.Stats.GpuDriven.CulledCommandCount,
                    GpuDrivenActiveBucketCount = RuntimeEngine.Rendering.Stats.GpuDriven.ActiveBucketCount,
                    GpuDrivenEmptyBucketSkips = RuntimeEngine.Rendering.Stats.GpuDriven.EmptyBucketSkips,
                    GpuDrivenFullBucketScans = RuntimeEngine.Rendering.Stats.GpuDriven.FullBucketScans,
                    GpuDrivenMaterialScatterDispatches = RuntimeEngine.Rendering.Stats.GpuDriven.MaterialScatterDispatches,
                    GpuDrivenIndirectCommandGenerationMs = RuntimeEngine.Rendering.Stats.GpuDriven.IndirectCommandGenerationMs,
                    GpuDrivenGpuCullMs = RuntimeEngine.Rendering.Stats.GpuDriven.GpuCullMs,
                    GpuDrivenGpuSortCompactMs = RuntimeEngine.Rendering.Stats.GpuDriven.GpuSortCompactMs,
                    GpuDrivenDelayedDrawCountBufferValue = RuntimeEngine.Rendering.Stats.GpuDriven.DelayedDrawCountBufferValue,
                    GpuDrivenDelayedDiagnosticReadbackBytes = RuntimeEngine.Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackBytes,
                    GpuDrivenDelayedDiagnosticReadbackCount = RuntimeEngine.Rendering.Stats.GpuDriven.DelayedDiagnosticReadbackCount,
                    GpuCompactionOverflow = RuntimeEngine.Rendering.Stats.GpuDriven.GpuCompactionOverflow,
                    GpuActiveListOverflow = RuntimeEngine.Rendering.Stats.GpuDriven.ActiveListOverflow,
                    GpuBucketOverflow = RuntimeEngine.Rendering.Stats.GpuDriven.BucketOverflow,
                    GpuMeshletOverflow = RuntimeEngine.Rendering.Stats.GpuDriven.MeshletOverflow,
                    GpuHiZMode = RuntimeEngine.Rendering.Stats.GpuDriven.HiZMode,
                    GpuHiZOnePhaseFrames = RuntimeEngine.Rendering.Stats.GpuDriven.HiZOnePhaseFrames,
                    GpuHiZTwoPhaseFrames = RuntimeEngine.Rendering.Stats.GpuDriven.HiZTwoPhaseFrames,
                    GpuHiZPhaseOneDraws = RuntimeEngine.Rendering.Stats.GpuDriven.HiZPhaseOneDraws,
                    GpuHiZPhaseTwoDraws = RuntimeEngine.Rendering.Stats.GpuDriven.HiZPhaseTwoDraws,
                    VisibilityPassDraws = RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityPassDraws,
                    VisibilityClassifiedPixels = RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityClassifiedPixels,
                    VisibilityActiveMaterialTiles = RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityActiveMaterialTiles,
                    VisibilityClassificationOverflow = RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityClassificationOverflow,
                    VisibilityReconstructionMs = RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityReconstructionMs,
                    VisibilityMaterialShadingMs = RuntimeEngine.Rendering.Stats.GpuDriven.VisibilityMaterialShadingMs,
                },
                Occlusion = CollectOcclusionProfilerData(),
            },
            GpuTransparencyOpaqueOrOtherVisible = RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyOpaqueOrOtherVisible,
            GpuTransparencyMaskedVisible = RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyMaskedVisible,
            GpuTransparencyApproximateVisible = RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyApproximateVisible,
            GpuTransparencyExactVisible = RuntimeEngine.Rendering.Stats.GpuTransparency.GpuTransparencyExactVisible,
            GpuMeshletRequestedFrames = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletRequestedFrames,
            GpuMeshletProductionFrames = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletProductionFrames,
            GpuMeshletFallbackFrames = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletFallbackFrames,
            GpuMeshletDispatchSkipped = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletDispatchSkipped,
            GpuMeshletTaskRecordsEmitted = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsEmitted,
            GpuMeshletTaskRecordsFrustumCulled = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsFrustumCulled,
            GpuMeshletTaskRecordsConeCulled = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsConeCulled,
            GpuMeshletTaskRecordsHiZCulled = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletTaskRecordsHiZCulled,
            GpuMeshletExpansionOverflowCount = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletExpansionOverflowCount,
            GpuMeshletBufferBytesResident = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletBufferBytesResident,
            GpuMeshletLastVisibleMeshletCount = RuntimeEngine.Rendering.Stats.GpuMeshlets.LastVisibleMeshletCount,
            GpuMeshletLastDispatchedMeshletCount = RuntimeEngine.Rendering.Stats.GpuMeshlets.LastDispatchedMeshletCount,
            GpuMeshletLastTaskRecordOverflowCount = RuntimeEngine.Rendering.Stats.GpuMeshlets.LastTaskRecordOverflowCount,
            GpuMeshletLastDispatchMs = RuntimeEngine.Rendering.Stats.GpuMeshlets.LastDispatchTime.TotalMilliseconds,
            GpuMeshletLastReadbackBytes = RuntimeEngine.Rendering.Stats.GpuMeshlets.LastReadbackBytes,
            GpuMeshletCacheHits = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheHits,
            GpuMeshletCacheMisses = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheMisses,
            GpuMeshletCacheStale = RuntimeEngine.Rendering.Stats.GpuMeshlets.GpuMeshletCacheStale,
            VulkanPipelineBinds = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineBinds,
            VulkanDescriptorBinds = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorBinds,
            VulkanPushConstantWrites = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPushConstantWrites,
            VulkanVertexBufferBinds = RuntimeEngine.Rendering.Stats.Vulkan.VulkanVertexBufferBinds,
            VulkanIndexBufferBinds = RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndexBufferBinds,
            VulkanPipelineBindSkips = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineBindSkips,
            VulkanDescriptorBindSkips = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorBindSkips,
            VulkanVertexBufferBindSkips = RuntimeEngine.Rendering.Stats.Vulkan.VulkanVertexBufferBindSkips,
            VulkanIndexBufferBindSkips = RuntimeEngine.Rendering.Stats.Vulkan.VulkanIndexBufferBindSkips,
            VulkanPipelineCacheLookupHits = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupHits,
            VulkanPipelineCacheLookupMisses = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupMisses,
            VulkanPipelineCacheLookupHitRate = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCacheLookupHitRate,
            VulkanPipelineCacheMissSummary = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPipelineCacheMissSummary,
            VulkanFrameWaitFenceMs = vulkanFrame.Detail.WaitFrameSlot.TotalMilliseconds,
            VulkanFrameAcquireImageMs = vulkanFrame.Detail.AcquireImage.TotalMilliseconds,
            VulkanFrameRecordCommandBufferMs = vulkanFrame.Detail.RecordCommandBuffer.TotalMilliseconds,
            VulkanFrameSubmitMs = vulkanFrame.Detail.SubmitQueue.TotalMilliseconds,
            VulkanFrameTrimMs = vulkanFrame.Detail.TrimStaging.TotalMilliseconds,
            VulkanFramePresentMs = vulkanFrame.Detail.PresentQueue.TotalMilliseconds,
            VulkanFrameTotalMs = vulkanFrame.TotalElapsed.TotalMilliseconds,
            VulkanFrameGpuCommandBufferMs = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameGpuCommandBufferMs,
            VulkanDeviceLocalAllocationCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDeviceLocalAllocationCount,
            VulkanDeviceLocalAllocatedBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDeviceLocalAllocatedBytes,
            VulkanUploadAllocationCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanUploadAllocationCount,
            VulkanUploadAllocatedBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanUploadAllocatedBytes,
            VulkanReadbackAllocationCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanReadbackAllocationCount,
            VulkanReadbackAllocatedBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanReadbackAllocatedBytes,
            VulkanDescriptorPoolCreateCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorPoolCreateCount,
            VulkanDescriptorPoolDestroyCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorPoolDestroyCount,
            VulkanDescriptorPoolResetCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorPoolResetCount,
            VulkanLifetimeLiveResourceCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanLifetimeLiveResourceCount,
            VulkanTrackedDescriptorSetCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanTrackedDescriptorSetCount,
            VulkanLifetimePendingRetirementCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanLifetimePendingRetirementCount,
            VulkanLifetimeOldestPendingRetirementAgeMilliseconds = RuntimeEngine.Rendering.Stats.Vulkan.VulkanLifetimeOldestPendingRetirementAgeMilliseconds,
            VulkanQueueSubmitCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanQueueSubmitCount,
            VulkanDroppedFrameOps = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDroppedFrameOps,
            VulkanDroppedDrawOps = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDroppedDrawOps,
            VulkanDroppedComputeOps = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDroppedComputeOps,
            VulkanSceneSwapchainWriters = RuntimeEngine.Rendering.Stats.Vulkan.VulkanSceneSwapchainWriters,
            VulkanOverlaySwapchainWriters = RuntimeEngine.Rendering.Stats.Vulkan.VulkanOverlaySwapchainWriters,
            VulkanForcedDiagnosticSwapchainWriters = RuntimeEngine.Rendering.Stats.Vulkan.VulkanForcedDiagnosticSwapchainWriters,
            VulkanFboOnlyDrawOps = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFboOnlyDrawOps,
            VulkanFboOnlyBlitOps = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFboOnlyBlitOps,
            VulkanMissingSceneSwapchainWriteFrames = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMissingSceneSwapchainWriteFrames,
            VulkanFirstFailedFrameOpPassIndex = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpPassIndex,
            VulkanFirstFailedFrameOpPipelineIdentity = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpPipelineIdentity,
            VulkanFirstFailedFrameOpViewportIdentity = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpViewportIdentity,
            VulkanFirstFailedFrameOpType = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpType,
            VulkanFirstFailedFrameOpTargetName = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpTargetName,
            VulkanFirstFailedFrameOpMaterialName = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpMaterialName,
            VulkanFirstFailedFrameOpShaderName = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpShaderName,
            VulkanFirstFailedFrameOpMessage = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstFailedFrameOpMessage,
            VulkanFrameDiagnosticSummary = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameDiagnosticSummary,
            VulkanValidationMessageCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationMessageCount,
            VulkanValidationErrorCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanValidationErrorCount,
            VulkanLastValidationMessage = RuntimeEngine.Rendering.Stats.Vulkan.VulkanLastValidationMessage,
            VulkanDescriptorFallbackSampledImages = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackSampledImages,
            VulkanDescriptorFallbackStorageImages = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackStorageImages,
            VulkanDescriptorFallbackUniformBuffers = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackUniformBuffers,
            VulkanDescriptorFallbackStorageBuffers = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackStorageBuffers,
            VulkanDescriptorFallbackTexelBuffers = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackTexelBuffers,
            VulkanDescriptorBindingFailures = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorBindingFailures,
            VulkanDescriptorSkippedDraws = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorSkippedDraws,
            VulkanDescriptorSkippedDispatches = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorSkippedDispatches,
            VulkanDescriptorFallbackSummary = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFallbackSummary,
            VulkanDescriptorFailureSummary = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorFailureSummary,
            VulkanDynamicUniformAllocations = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDynamicUniformAllocations,
            VulkanDynamicUniformAllocatedBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDynamicUniformAllocatedBytes,
            VulkanDynamicUniformExhaustions = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDynamicUniformExhaustions,
            VulkanMeshFrameDataArenaChunkCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkCount,
            VulkanMeshFrameDataMappedBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytes,
            VulkanMeshFrameDataReservedBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytes,
            VulkanMeshFrameDataReservationCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationCount,
            VulkanMeshFrameDataGeneration = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataGeneration,
            VulkanMeshFrameDataRecordingLeases = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataRecordingLeases,
            VulkanMeshFrameDataCachedLeases = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataCachedLeases,
            VulkanMeshFrameDataSubmittedLeases = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataSubmittedLeases,
            VulkanMeshFrameDataActiveGenerationCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataActiveGenerationCount,
            VulkanMeshFrameDataLeaseRetainedGenerationCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseRetainedGenerationCount,
            VulkanMeshDescriptorAllocationVariants = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariants,
            VulkanMeshDescriptorPools = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorPools,
            VulkanMeshDescriptorAllocatedSets = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocatedSets,
            VulkanMeshDescriptorReservedSets = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorReservedSets,
            VulkanMeshFrameDataArenaChunkHighWater = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataArenaChunkHighWater,
            VulkanMeshFrameDataMappedBytesHighWater = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataMappedBytesHighWater,
            VulkanMeshFrameDataReservedBytesHighWater = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservedBytesHighWater,
            VulkanMeshFrameDataReservationHighWater = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataReservationHighWater,
            VulkanMeshFrameDataLeaseHighWater = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshFrameDataLeaseHighWater,
            VulkanMeshDescriptorAllocationVariantHighWater = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorAllocationVariantHighWater,
            VulkanMeshDescriptorPoolHighWater = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorPoolHighWater,
            VulkanMeshDescriptorSetHighWater = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMeshDescriptorSetHighWater,
            VulkanRetiredResourcePlanReplacements = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanReplacements,
            VulkanRetiredResourcePlanImages = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanImages,
            VulkanRetiredResourcePlanBuffers = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredResourcePlanBuffers,
            VulkanFrameLoop = new VulkanFrameLoopTelemetryData
            {
                CorrelatedFrameTree = VulkanFrameTelemetryProfilerDataConverter
                    .CreateProfilerFrameTree(in vulkanFrame),
                FrameSampleTimingQueriesMs = vulkanFrame.Detail.SampleTimingQueries.TotalMilliseconds,
                FrameDrainRetiredResourcesMs = vulkanFrame.Detail.DrainRetiredResources.TotalMilliseconds,
                FrameAcquireBridgeSubmitMs = vulkanFrame.Detail.AcquireBridgeSubmit.TotalMilliseconds,
                FrameWaitSwapchainImageMs = vulkanFrame.Detail.WaitSwapchainImage.TotalMilliseconds,
                FrameResetDynamicUniformRingMs = vulkanFrame.Detail.ResetDynamicUniformRing.TotalMilliseconds,
                RecordCommandBufferAllocatedBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRecordCommandBufferAllocatedBytes,
                ResetCommandBufferCalls = RuntimeEngine.Rendering.Stats.Vulkan.VulkanResetCommandBufferCalls,
                ResetCommandPoolCalls = RuntimeEngine.Rendering.Stats.Vulkan.VulkanResetCommandPoolCalls,
                AllocateCommandBufferCalls = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAllocateCommandBufferCalls,
                CommandBuffersAllocated = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBuffersAllocated,
                ExecuteSecondaryCommandBufferCalls = RuntimeEngine.Rendering.Stats.Vulkan.VulkanExecuteSecondaryCommandBufferCalls,
                SecondaryCommandBuffersInvoked = RuntimeEngine.Rendering.Stats.Vulkan.VulkanSecondaryCommandBuffersInvoked,
                PreparedMeshDrawCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPreparedMeshDraws,
                FrameOpTotalCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpTotalCount,
                FrameOpClearCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpClearCount,
                FrameOpMeshDrawCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpMeshDrawCount,
                FrameOpIndirectDrawCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpIndirectDrawCount,
                FrameOpMeshTaskDispatchCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpMeshTaskDispatchCount,
                FrameOpBlitCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpBlitCount,
                FrameOpComputeCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpComputeCount,
                FrameOpSwapchainWriteCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpSwapchainWriteCount,
                FrameOpFboWriteCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpFboWriteCount,
                FrameOpUniquePassCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpUniquePassCount,
                FrameOpUniqueContextCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpUniqueContextCount,
                FrameOpUniqueTargetCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameOpUniqueTargetCount,
                MaterialPayloadCacheHits = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialPayloadCacheHits,
                MaterialPayloadCacheMisses = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialPayloadCacheMisses,
                MaterialPayloadsPacked = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialPayloadsPacked,
                MaterialUniformsPacked = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialUniformsPacked,
                MaterialParameterEmissions = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialParameterEmissions,
                MaterialDictionaryWrites = RuntimeEngine.Rendering.Stats.Vulkan.VulkanMaterialDictionaryWrites,
                FrameMaterialSnapshotCacheHits = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameMaterialSnapshotCacheHits,
                FrameMaterialSnapshotCacheMisses = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameMaterialSnapshotCacheMisses,
                BindingSnapshotsCaptured = RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSnapshotsCaptured,
                BindingSnapshotEntries = RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSnapshotEntries,
                FastPathBindingSnapshots = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFastPathBindingSnapshots,
                LegacyBindingSnapshots = RuntimeEngine.Rendering.Stats.Vulkan.VulkanLegacyBindingSnapshots,
                AutoUniformPlanCacheHits = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformPlanCacheHits,
                AutoUniformPlanCacheMisses = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformPlanCacheMisses,
                AutoUniformStaticBytesCopied = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformStaticBytesCopied,
                AutoUniformDynamicBytesCleared = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformDynamicBytesCleared,
                AutoUniformDynamicMembersPatched = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformDynamicMembersPatched,
                AutoUniformReflectedMembersScanned = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformReflectedMembersScanned,
                AutoUniformLegacyFullBlockBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformLegacyFullBlockBytes,
                AutoUniformFastPathDraws = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformFastPathDraws,
                AutoUniformLegacyFallbackDraws = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformLegacyFallbackDraws,
                FrameDataDrawsVisited = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFrameDataDrawsVisited,
                DescriptorRecordsValidated = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorRecordsValidated,
                DescriptorRecordsWritten = RuntimeEngine.Rendering.Stats.Vulkan.VulkanDescriptorRecordsWritten,
                BindingSchemasCompiled = RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSchemasCompiled,
                BindingSchemaValueOperations = RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSchemaValueOperations,
                BindingSchemaDescriptorEntries = RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSchemaDescriptorEntries,
                BindingSchemaFallbackOperations = RuntimeEngine.Rendering.Stats.Vulkan.VulkanBindingSchemaFallbackOperations,
                AutoUniformTypedOperationsExecuted = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformTypedOperationsExecuted,
                AutoUniformReflectedNameLookups = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformReflectedNameLookups,
                AutoUniformGenericConversions = RuntimeEngine.Rendering.Stats.Vulkan.VulkanAutoUniformGenericConversions,
                AutoUniformFallbackReasonCounts =
                [
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.None),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSnapshotIneligible),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.ProgramUnavailable),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidBufferSize),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSchemaUnavailable),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.BindingSchemaMismatch),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidMemberName),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.UnsupportedShaderType),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidDestinationRange),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.InvalidArrayLayout),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.StructSnapshotRequired),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.EngineSourceTypeMismatch),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.MeshStateSourceTypeMismatch),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedEngineSourceUnavailable),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedEngineWriteFailed),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedTemporalWriteFailed),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMeshStateSourceUnavailable),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMeshStateWriteFailed),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanAutoUniformFallbackReasonCount(EVulkanAutoUniformFallbackReason.TypedMaterialOrRuntimeWriteFailed),
                ],
                CommandBufferCleanReuseCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferCleanReuseCount,
                CommandBufferRecordCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferRecordCount,
                CommandBufferForcedDirtyCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferForcedDirtyCount,
                CommandBufferFrameOpSignatureDirtyCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferFrameOpSignatureDirtyCount,
                CommandBufferPlannerDirtyCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferPlannerDirtyCount,
                CommandBufferProfilerDirtyCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferProfilerDirtyCount,
                CommandBufferDirtySummary = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandBufferDirtySummary,
                CommandChainsScheduled = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainsScheduled,
                CommandChainsRecorded = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainsRecorded,
                CommandChainsReused = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainsReused,
                CommandChainsFrameDataRefreshed = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainsFrameDataRefreshed,
                VolatileCommandChainsRecorded = RuntimeEngine.Rendering.Stats.Vulkan.VulkanVolatileCommandChainsRecorded,
                PrimaryCommandBuffersReused = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersReused,
                PrimaryCommandBuffersRecorded = RuntimeEngine.Rendering.Stats.Vulkan.VulkanPrimaryCommandBuffersRecorded,
                VisibilityPacketCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanVisibilityPacketCount,
                RenderPacketCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRenderPacketCount,
                SecondaryCommandBufferCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanSecondaryCommandBufferCount,
                LastCommandChainWorkerEligibility = RuntimeEngine.Rendering.Stats.Vulkan.VulkanLastCommandChainWorkerEligibility,
                CommandChainWorkerEligibilityCounts =
                [
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.NotEvaluated),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.Eligible),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.TooLittleIndependentWork),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.MutableRendererConflict),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.UnsupportedOperation),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.UnsupportedInheritance),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.PrimaryOwnedIndirectStream),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.WorkerQuarantined),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanCommandChainWorkerEligibilityCount(EVulkanCommandChainWorkerEligibility.ResourcePreparationFailed),
                ],
                LastIndirectSecondaryEligibility = RuntimeEngine.Rendering.Stats.Vulkan.VulkanLastIndirectSecondaryEligibility,
                IndirectSecondaryEligibilityCounts =
                [
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.NotEvaluated),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.EligibleProducerComplete),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.MutableCurrentFrame),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.ProducerIncomplete),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.BufferIdentityChanged),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.InvalidRange),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.CommandChainsDisabled),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.UnsupportedInheritance),
                    RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanIndirectSecondaryEligibilityCount(EVulkanIndirectSecondaryEligibility.ResourcePreparationFailed),
                ],
                LastComputeSecondaryEligibility = RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanLastSecondaryRecordingEligibility(EVulkanSecondaryCommandFamily.Compute),
                ComputeSecondaryEligibilityCounts = CollectSecondaryRecordingEligibilityCounts(EVulkanSecondaryCommandFamily.Compute),
                LastTransferSecondaryEligibility = RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanLastSecondaryRecordingEligibility(EVulkanSecondaryCommandFamily.Transfer),
                TransferSecondaryEligibilityCounts = CollectSecondaryRecordingEligibilityCounts(EVulkanSecondaryCommandFamily.Transfer),
                LastQuerySecondaryEligibility = RuntimeEngine.Rendering.Stats.Vulkan.GetVulkanLastSecondaryRecordingEligibility(EVulkanSecondaryCommandFamily.Query),
                QuerySecondaryEligibilityCounts = CollectSecondaryRecordingEligibilityCounts(EVulkanSecondaryCommandFamily.Query),
                CommandChainWorkerRecordMs = RuntimeEngine.Rendering.Stats.Vulkan.VulkanCommandChainWorkerRecordMs,
                RenderThreadWaitForChainWorkersMs = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRenderThreadWaitForChainWorkersMs,
                FirstCommandChainStructuralDirtyReason = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstCommandChainStructuralDirtyReason,
                FirstCommandChainDescriptorGenerationMismatch = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstCommandChainDescriptorGenerationMismatch,
                FirstCommandChainResourcePlanRevisionMismatch = RuntimeEngine.Rendering.Stats.Vulkan.VulkanFirstCommandChainResourcePlanRevisionMismatch,
                RetiredDescriptorPoolCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredDescriptorPoolCount,
                RetiredPipelineCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredPipelineCount,
                RetiredFramebufferCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredFramebufferCount,
                RetiredBufferCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredBufferCount,
                RetiredBufferMemoryCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredBufferMemoryCount,
                RetiredImageCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredImageCount,
                RetiredImageViewCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredImageViewCount,
                RetiredSamplerCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredSamplerCount,
                RetiredImageMemoryCount = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredImageMemoryCount,
                RetiredImageBytes = RuntimeEngine.Rendering.Stats.Vulkan.VulkanRetiredImageBytes,
            },
            FrameLifecycle = new FrameLifecycleTelemetryData
            {
                UpdateFrameId = RuntimeEngine.Rendering.Stats.FrameLifecycle.UpdateFrameId,
                CollectFrameId = RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectFrameId,
                SwapFrameId = RuntimeEngine.Rendering.Stats.FrameLifecycle.SwapFrameId,
                RenderFrameId = RuntimeEngine.Rendering.Stats.FrameLifecycle.RenderFrameId,
                PresentFrameId = RuntimeEngine.Rendering.Stats.FrameLifecycle.PresentFrameId,
                CollectVisibleLatePolicy = RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectVisibleLatePolicy,
                CollectWaitForRenderMs = RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectWaitForRenderMs,
                CollectWaitReason = RuntimeEngine.Rendering.Stats.FrameLifecycle.CollectWaitReason,
                RenderWaitForCollectMs = RuntimeEngine.Rendering.Stats.FrameLifecycle.RenderWaitForCollectMs,
                RenderWaitReason = RuntimeEngine.Rendering.Stats.FrameLifecycle.RenderWaitReason,
                SkippedCollectFrames = RuntimeEngine.Rendering.Stats.FrameLifecycle.SkippedCollectFrames,
                StaleCollectReuseFrames = RuntimeEngine.Rendering.Stats.FrameLifecycle.StaleCollectReuseFrames,
            },
            FrameOutputs = ConvertFrameOutputManifest(RuntimeEngine.Rendering.Stats.FrameOutputs.LastManifest),
            AllocatedVRAMBytes = RuntimeEngine.Rendering.Stats.Vram.AllocatedVRAMBytes,
            AllocatedBufferBytes = RuntimeEngine.Rendering.Stats.Vram.AllocatedBufferBytes,
            AllocatedTextureBytes = RuntimeEngine.Rendering.Stats.Vram.AllocatedTextureBytes,
            AllocatedRenderBufferBytes = RuntimeEngine.Rendering.Stats.Vram.AllocatedRenderBufferBytes,
            FBOBandwidthBytes = RuntimeEngine.Rendering.Stats.Vram.FBOBandwidthBytes,
            FBOBindCount = RuntimeEngine.Rendering.Stats.Vram.FBOBindCount,
            VrLeftEyeDraws = RuntimeEngine.Rendering.Stats.Vr.VrLeftEyeDraws,
            VrRightEyeDraws = RuntimeEngine.Rendering.Stats.Vr.VrRightEyeDraws,
            VrLeftEyeVisible = RuntimeEngine.Rendering.Stats.Vr.VrLeftEyeVisible,
            VrRightEyeVisible = RuntimeEngine.Rendering.Stats.Vr.VrRightEyeVisible,
            VrLeftWorkerBuildTimeMs = RuntimeEngine.Rendering.Stats.Vr.VrLeftWorkerBuildTimeMs,
            VrRightWorkerBuildTimeMs = RuntimeEngine.Rendering.Stats.Vr.VrRightWorkerBuildTimeMs,
            VrRenderSubmitTimeMs = RuntimeEngine.Rendering.Stats.Vr.VrRenderSubmitTimeMs,
            VrXrWaitFrameBlockTimeMs = RuntimeEngine.Rendering.Stats.Vr.VrXrWaitFrameBlockTimeMs,
            VrXrEndFrameSubmitTimeMs = RuntimeEngine.Rendering.Stats.Vr.VrXrEndFrameSubmitTimeMs,
            VrXrPredictedToLatePoseDeltaMillimeters = RuntimeEngine.Rendering.Stats.Vr.VrXrPredictedToLatePoseDeltaMillimeters,
            VrXrPredictedToLatePoseDeltaDegrees = RuntimeEngine.Rendering.Stats.Vr.VrXrPredictedToLatePoseDeltaDegrees,
            VrXrPredictedDisplayLeadTimeMs = RuntimeEngine.Rendering.Stats.Vr.VrXrPredictedDisplayLeadTimeMs,
            VrXrMissedDeadlineFrames = RuntimeEngine.Rendering.Stats.Vr.VrXrMissedDeadlineFrames,
            VrXrTrackingLossFrames = RuntimeEngine.Rendering.Stats.Vr.VrXrTrackingLossFrames,
            VrXrRelocatePredictedTimeMs = RuntimeEngine.Rendering.Stats.Vr.VrXrRelocatePredictedTimeMs,
            VrXrCollectFrustumExpansionDegrees = RuntimeEngine.Rendering.Stats.Vr.VrXrCollectFrustumExpansionDegrees,
            VrXrPacingThreadIdleTimeMs = RuntimeEngine.Rendering.Stats.Vr.VrXrPacingThreadIdleTimeMs,
            VrXrPacingHandoffStalls = RuntimeEngine.Rendering.Stats.Vr.VrXrPacingHandoffStalls,
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
            RenderMatrixStatsReady = RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixStatsReady,
            RenderMatrixApplied = RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixApplied,
            RenderMatrixBatchCount = RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixBatchCount,
            RenderMatrixMaxBatchSize = RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixMaxBatchSize,
            RenderMatrixSetCalls = RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixSetCalls,
            RenderMatrixListenerInvocations = RuntimeEngine.Rendering.Stats.RenderMatrix.RenderMatrixListenerInvocations,
            RenderMatrixListenerCounts = listenerEntries,
            SkinnedBoundsStatsReady = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsStatsReady,
            SkinnedBoundsDeferredScheduledCount = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredScheduledCount,
            SkinnedBoundsDeferredCompletedCount = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCompletedCount,
            SkinnedBoundsDeferredFailedCount = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredFailedCount,
            SkinnedBoundsDeferredInFlightCount = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredInFlightCount,
            SkinnedBoundsDeferredMaxInFlightCount = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxInFlightCount,
            SkinnedBoundsDeferredQueueWaitMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredQueueWaitMs,
            SkinnedBoundsDeferredCpuJobMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredCpuJobMs,
            SkinnedBoundsDeferredApplyMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredApplyMs,
            SkinnedBoundsDeferredMaxQueueWaitMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxQueueWaitMs,
            SkinnedBoundsDeferredMaxCpuJobMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxCpuJobMs,
            SkinnedBoundsDeferredMaxApplyMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsDeferredMaxApplyMs,
            SkinnedBoundsGpuCompletedCount = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuCompletedCount,
            SkinnedBoundsGpuComputeMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuComputeMs,
            SkinnedBoundsGpuApplyMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuApplyMs,
            SkinnedBoundsGpuMaxComputeMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuMaxComputeMs,
            SkinnedBoundsGpuMaxApplyMs = RuntimeEngine.Rendering.Stats.SkinnedBounds.SkinnedBoundsGpuMaxApplyMs,
            OctreeStatsReady = RuntimeEngine.Rendering.Stats.Octree.OctreeStatsReady,
            OctreeCollectCallCount = RuntimeEngine.Rendering.Stats.Octree.OctreeCollectCallCount,
            OctreeVisibleRenderableCount = RuntimeEngine.Rendering.Stats.Octree.OctreeVisibleRenderableCount,
            OctreeEmittedCommandCount = RuntimeEngine.Rendering.Stats.Octree.OctreeEmittedCommandCount,
            OctreeMaxVisibleRenderablesPerCollect = RuntimeEngine.Rendering.Stats.Octree.OctreeMaxVisibleRenderablesPerCollect,
            OctreeMaxEmittedCommandsPerCollect = RuntimeEngine.Rendering.Stats.Octree.OctreeMaxEmittedCommandsPerCollect,
            OctreeAddCount = RuntimeEngine.Rendering.Stats.Octree.OctreeAddCount,
            OctreeMoveCount = RuntimeEngine.Rendering.Stats.Octree.OctreeMoveCount,
            OctreeRemoveCount = RuntimeEngine.Rendering.Stats.Octree.OctreeRemoveCount,
            OctreeSkippedMoveCount = RuntimeEngine.Rendering.Stats.Octree.OctreeSkippedMoveCount,
            OctreeSwapDrainedCommandCount = RuntimeEngine.Rendering.Stats.Octree.OctreeSwapDrainedCommandCount,
            OctreeSwapBufferedCommandCount = RuntimeEngine.Rendering.Stats.Octree.OctreeSwapBufferedCommandCount,
            OctreeSwapExecutedCommandCount = RuntimeEngine.Rendering.Stats.Octree.OctreeSwapExecutedCommandCount,
            OctreeSwapDrainMs = RuntimeEngine.Rendering.Stats.Octree.OctreeSwapDrainMs,
            OctreeSwapExecuteMs = RuntimeEngine.Rendering.Stats.Octree.OctreeSwapExecuteMs,
            OctreeSwapMaxCommandMs = RuntimeEngine.Rendering.Stats.Octree.OctreeSwapMaxCommandMs,
            OctreeSwapMaxCommandKind = RuntimeEngine.Rendering.Stats.Octree.OctreeSwapMaxCommandKind,
            OctreeRaycastProcessedCommandCount = RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastProcessedCommandCount,
            OctreeRaycastDroppedCommandCount = RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastDroppedCommandCount,
            OctreeRaycastTraversalMs = RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastTraversalMs,
            OctreeRaycastCallbackMs = RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastCallbackMs,
            OctreeRaycastMaxTraversalMs = RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxTraversalMs,
            OctreeRaycastMaxCallbackMs = RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxCallbackMs,
            OctreeRaycastMaxCommandMs = RuntimeEngine.Rendering.Stats.Octree.OctreeRaycastMaxCommandMs,
            CpuSpatialTreeMode = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMode,
            CpuSpatialTreeNodeCount = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeNodeCount,
            CpuSpatialTreeItemCount = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeItemCount,
            CpuSpatialTreeRootItemCount = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeRootItemCount,
            CpuSpatialTreeMaxNodeItemCount = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxNodeItemCount,
            CpuSpatialTreeMaxDepth = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxDepth,
            CpuSpatialTreeUnboundedItemCount = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeUnboundedItemCount,
            CpuSpatialTreeCollectMs = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeCollectMs,
            CpuSpatialTreeMaxCollectMs = RuntimeEngine.Rendering.Stats.Octree.CpuSpatialTreeMaxCollectMs,
            GpuRenderPipelineProfilingEnabled = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingEnabled,
            GpuRenderPipelineProfilingSupported = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingSupported,
            GpuRenderPipelineTimingsReady = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady,
            GpuRenderPipelineBackend = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineBackend,
            GpuRenderPipelineStatusMessage = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineStatusMessage,
            GpuRenderPipelineFrameMs = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs,
            GpuRenderPipelineTimingRoots = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GetGpuRenderPipelineTimingRoots(),
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
            HiZBuildGpuMs = OcclusionTelemetry.HiZBuildGpuMs,
            HiZTestGpuMs = OcclusionTelemetry.HiZTestGpuMs,
            HiZBuildGpuSourceFrame = OcclusionTelemetry.HiZBuildGpuSourceFrame,
            HiZTestGpuSourceFrame = OcclusionTelemetry.HiZTestGpuSourceFrame,
            HiZBuildGpuAgeFrames = OcclusionTelemetry.HiZBuildGpuAgeFrames,
            HiZTestGpuAgeFrames = OcclusionTelemetry.HiZTestGpuAgeFrames,
            HiZBuildGpuAvailability = OcclusionTelemetry.HiZBuildGpuAvailability.ToString(),
            HiZTestGpuAvailability = OcclusionTelemetry.HiZTestGpuAvailability.ToString(),
        };

    private static FrameOutputManifestData ConvertFrameOutputManifest(RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot snapshot)
    {
        var outputs = snapshot.Outputs ?? [];
        FrameOutputEntryData[] outputData = new FrameOutputEntryData[outputs.Length];
        for (int i = 0; i < outputs.Length; i++)
        {
            RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
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
            PhysicalPlanCacheHits = snapshot.Work.PhysicalPlanCacheHits,
            PhysicalPlanCacheMisses = snapshot.Work.PhysicalPlanCacheMisses,
            PhysicalPlanGenerations = snapshot.Work.PhysicalPlanGenerations,
            PhysicalPlanAliasReuses = snapshot.Work.PhysicalPlanAliasReuses,
            PlannerArenaHighWater = snapshot.Work.PlannerArenaHighWater,
            RenderGraphPlanGeneration = snapshot.Work.RenderGraphPlanGeneration,
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
            PlannerEvictionDeferralCount = snapshot.Work.PlannerEvictionDeferralCount,
            GlobalInFlightWaitCount = snapshot.Work.GlobalInFlightWaitCount,
            ForceFlushCount = snapshot.Work.ForceFlushCount,
            Outputs = outputData,
        };
    }

    private static RenderStatsPacket CollectGpuPipelineStats()
        => new()
        {
            GpuRenderPipelineProfilingEnabled = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingEnabled,
            GpuRenderPipelineProfilingSupported = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineProfilingSupported,
            GpuRenderPipelineTimingsReady = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady,
            GpuRenderPipelineBackend = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineBackend,
            GpuRenderPipelineStatusMessage = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineStatusMessage,
            GpuRenderPipelineFrameMs = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs,
            GpuRenderPipelineTimingRoots = RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GetGpuRenderPipelineTimingRoots(),
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
            Scopes = ToScopeSlices(snap.Scopes),
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

    private static AllocationScopeSlice[] ToScopeSlices(Engine.AllocationScopeSnapshot[] scopes)
    {
        if (scopes.Length == 0)
            return [];

        AllocationScopeSlice[] slices = new AllocationScopeSlice[scopes.Length];
        for (int i = 0; i < scopes.Length; i++)
        {
            Engine.AllocationScopeSnapshot scope = scopes[i];
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
        var m = RuntimeEngine.Rendering.BvhStats.Latest;
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
            TraversalMilliseconds = m.TraversalMilliseconds,
            CommandEmissionMilliseconds = m.CommandEmissionMilliseconds,
            CommandEmissionSubmissionMilliseconds = m.CommandEmissionSubmissionMilliseconds,
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

    private static long[] CollectSecondaryRecordingEligibilityCounts(
        EVulkanSecondaryCommandFamily family)
    {
        long[] counts =
            new long[(int)EVulkanSecondaryRecordingEligibility.Count];
        for (int reasonIndex = 0;
             reasonIndex < counts.Length;
             reasonIndex++)
        {
            counts[reasonIndex] = RuntimeEngine.Rendering.Stats.Vulkan
                .GetVulkanSecondaryRecordingEligibilityCount(
                    family,
                    (EVulkanSecondaryRecordingEligibility)reasonIndex);
        }

        return counts;
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
