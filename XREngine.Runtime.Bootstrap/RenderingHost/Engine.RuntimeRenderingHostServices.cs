using System.Collections;
using System.IO;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Diagnostics;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Scene.Physics;
using XREngine.Scene.Physics.Jitter2;
using XREngine.Scene.Physics.Jolt;
using XREngine.Scene.Physics.Physx;

namespace XREngine;

internal sealed class EngineRuntimeRenderingHostServices :
    IRuntimeRenderingHostServices,
    IDisposable
{
    private const int MaxConsecutiveVrDesktopBudgetSkips = 2;

    private readonly RendererBackendCatalog _rendererBackends = new();
    private IDisposable? _rendererBackendRegistrations;
    private readonly object _vrDesktopPressureLock = new();
    private readonly object _renderOutputGraphLock = new();
    private readonly RenderOutputGraphPlanner _renderOutputGraphPlanner = new();
    private int _vrDesktopPressureHoldFramesRemaining;
    private int _vrDesktopPressureConsecutiveSkips;
    private ulong _vrDesktopPressureFrameId;
    private bool _vrDesktopPressureHoldCurrentFrame;

    public EngineRuntimeRenderingHostServices(bool registerRendererBackends)
    {
        if (registerRendererBackends)
            _rendererBackendRegistrations = BuiltInRendererBackendModules.RegisterAll(_rendererBackends);
    }

    public IRendererBackendCatalog RendererBackends => _rendererBackends;

    public void Dispose()
    {
        Interlocked.Exchange(ref _rendererBackendRegistrations, null)?.Dispose();
        _rendererBackends.Dispose();
    }

    public IDisposable? StartProfileScope(string? scopeName)
    {
        // Fast path when profiling is off: avoid the ProfilerScope -> IDisposable box entirely.
        if (!Engine.Profiler.EnableFrameLogging)
            return null;

        // Always pass an explicit name. The [CallerMemberName] attribute on the interface
        // captures the actual caller; if a caller invokes StartProfileScope() without a name,
        // that caller's method name arrives here. If it did not (e.g. explicit null), fall
        // back to a generic placeholder rather than letting CodeProfiler.Start()'s own
        // [CallerMemberName] resolve to our wrapper name ("StartProfileScope").
        return Engine.StartPooledProfilerScope(
            string.IsNullOrWhiteSpace(scopeName) ? "<unnamed>" : scopeName);
    }

    public bool AllowShaderPipelines => RuntimeEngine.Rendering.Settings.AllowShaderPipelines;
    public bool EnableExactTransparencyTechniques => Engine.EditorPreferences.Debug.EnableExactTransparencyTechniques;
    public bool UseInterleavedMeshBuffer => RuntimeEngine.Rendering.Settings.UseInterleavedMeshBuffer;
    public bool UseIntegerUniformsInShaders => RuntimeEngine.Rendering.Settings.UseIntegerUniformsInShaders;
    public bool RemapBlendshapeDeltas => RuntimeEngine.Rendering.Settings.RemapBlendshapeDeltas;
    public bool AllowBlendshapes => RuntimeEngine.Rendering.Settings.AllowBlendshapes;
    public bool PopulateVertexDataInParallel => RuntimeEngine.Rendering.Settings.PopulateVertexDataInParallel;
    public bool ProcessMeshImportsAsynchronously => RuntimeEngine.Rendering.Settings.ProcessMeshImportsAsynchronously;
    public bool AllowSkinning => RuntimeEngine.Rendering.Settings.AllowSkinning;
    public bool CalculateSkinningInComputeShader => RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader;
    public bool CalculateBlendshapesInComputeShader => RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader;
    public bool CalculateSkinnedBoundsInComputeShader => RuntimeEngine.Rendering.Settings.CalculateSkinnedBoundsInComputeShader;
    public bool SkinnedBoundsGpuDirectAabbWrite => RuntimeEngine.Rendering.Settings.SkinnedBoundsGpuDirectAabbWrite;
    public bool EnableBlendshapePrecombinePass => RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass;
    public bool EnableBlendshapePrecombineForDirectVertexPath => RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombineForDirectVertexPath;
    public bool EnableBlendshapePcaBasisCompression => RuntimeEngine.Rendering.Settings.EnableBlendshapePcaBasisCompression;
    public int BlendshapePrecombineComputeMinActiveShapes => RuntimeEngine.Rendering.Settings.BlendshapePrecombineComputeMinActiveShapes;
    public int BlendshapePrecombineDirectMinActiveShapes => RuntimeEngine.Rendering.Settings.BlendshapePrecombineDirectMinActiveShapes;
    public int BlendshapePrecombineMinAffectedVertices => RuntimeEngine.Rendering.Settings.BlendshapePrecombineMinAffectedVertices;
    public bool StreamMeshLodsOnDemand => RuntimeEngine.Rendering.Settings.StreamMeshLodsOnDemand;
    public int MeshLodStreamingDrainIntervalFrames => RuntimeEngine.Rendering.Settings.MeshLodStreamingDrainIntervalFrames;
    public int MeshLodStreamingMaxLoadsPerDrain => RuntimeEngine.Rendering.Settings.MeshLodStreamingMaxLoadsPerDrain;
    public int ShaderConfigVersion => RuntimeEngine.Rendering.Settings.ShaderConfigVersion;
    public ERenderClipSpaceYDirection ClipSpaceYDirection => RuntimeEngine.Rendering.Settings.ClipSpaceYDirection;
    public ERenderClipDepthRange ClipDepthRange => RuntimeEngine.Rendering.Settings.ClipDepthRange;
    public bool AllowBinaryProgramCaching => RuntimeEngine.Rendering.Settings.AllowBinaryProgramCaching;
    public bool AsyncProgramBinaryUpload => RuntimeEngine.Rendering.Settings.AsyncProgramBinaryUpload;
    public bool AsyncProgramCompilation => RuntimeEngine.Rendering.Settings.AsyncProgramCompilation;
    public int OpenGLProgramCompileLinkWorkerCount => RuntimeEngine.Rendering.Settings.OpenGLProgramCompileLinkWorkerCount;
    public int MaxAsyncShaderProgramsPerFrame => RuntimeEngine.Rendering.Settings.MaxAsyncShaderProgramsPerFrame;
    public EOpenGLShaderLinkStrategy OpenGLShaderLinkStrategy => RuntimeEngine.Rendering.Settings.OpenGLShaderLinkStrategy;
    public int OpenGLShaderCompilerThreadCount => RuntimeEngine.Rendering.Settings.OpenGLShaderCompilerThreadCount;
    public bool OpenGLParallelShaderCompileProbeEnabled => RuntimeEngine.Rendering.Settings.OpenGLParallelShaderCompileProbeEnabled;
    public int OpenGLParallelShaderCompileProbeTimeoutMs => RuntimeEngine.Rendering.Settings.OpenGLParallelShaderCompileProbeTimeoutMs;
    public EVulkanAllocatorBackend VulkanAllocatorBackend => RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend;
    public EVulkanSynchronizationBackend VulkanSynchronizationBackend => RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.SyncBackend;
    public EVulkanDescriptorUpdateBackend VulkanDescriptorUpdateBackend => RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DescriptorUpdateBackend;
    public bool VulkanDynamicUniformBufferEnabled => RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.DynamicUniformBufferEnabled;
    public bool EnableVulkanBindlessMaterialTable => Engine.EffectiveSettings.EnableVulkanBindlessMaterialTable;
    public bool EnableVulkanDescriptorIndexing => Engine.EffectiveSettings.EnableVulkanDescriptorIndexing;
    public bool ValidateVulkanDescriptorContracts => Engine.EffectiveSettings.ValidateVulkanDescriptorContracts;
    public EVulkanBindlessMaterialMode VulkanBindlessMaterialMode => Engine.EffectiveSettings.VulkanBindlessMaterialMode;
    public EVulkanGeometryFetchMode VulkanGeometryFetchMode => Engine.EffectiveSettings.VulkanGeometryFetchMode;
    public EVulkanRenderTargetMode VulkanRenderTargetMode => Engine.EffectiveSettings.VulkanRenderTargetMode;
    public EVulkanGpuDrivenProfile VulkanGpuDrivenProfile => Engine.EffectiveSettings.VulkanGpuDrivenProfile;
    public EVulkanQueueOverlapMode VulkanQueueOverlapMode => Engine.EffectiveSettings.VulkanQueueOverlapMode;
    public EVulkanCommandRecordingMode VulkanCommandRecordingMode => Engine.EffectiveSettings.VulkanCommandRecordingMode;
    public bool EnableVulkanPrimaryCommandBufferReuse => RuntimeEngine.Rendering.Settings.EnableVulkanPrimaryCommandBufferReuse;
    public EVulkanDiagnosticPreset VulkanDiagnosticPreset => Engine.EffectiveSettings.VulkanDiagnosticPreset;
    public EVulkanDiagnosticFlags VulkanDiagnosticFlags => Engine.EffectiveSettings.VulkanDiagnosticFlags;

    public void SubscribeRenderingSettingsChanged(Action callback)
        => RuntimeEngine.Rendering.SettingsChanged += callback;

    public void UnsubscribeRenderingSettingsChanged(Action callback)
        => RuntimeEngine.Rendering.SettingsChanged -= callback;

    public void SubscribeAntiAliasingSettingsChanged(Action callback)
        => RuntimeEngine.Rendering.AntiAliasingSettingsChanged += callback;

    public void UnsubscribeAntiAliasingSettingsChanged(Action callback)
        => RuntimeEngine.Rendering.AntiAliasingSettingsChanged -= callback;

    public bool IsRenderThread => Engine.IsRenderThread;
    public bool IsRendererActive => AbstractRenderer.Current?.Active ?? false;
    public bool IsShadowPass => RuntimeEngine.Rendering.State.IsShadowPass;
    public bool IsStereoPass => RuntimeEngine.Rendering.State.IsStereoPass;
    public bool IsSceneCapturePass => RuntimeEngine.Rendering.State.IsSceneCapturePass;
    public bool RenderCullingVolumesEnabled => Engine.EditorPreferences.Diagnostics.Visualization.RenderCullingVolumes;
    public bool Preview3DWorldOctree => Engine.EditorPreferences.Diagnostics.Visualization.Preview3DWorldOctree;
    public bool Preview2DWorldQuadtree => Engine.EditorPreferences.Diagnostics.Visualization.Preview2DWorldQuadtree;
    public bool HoverOutlineEnabled => Engine.EditorPreferences.Selection.HoverOutlineEnabled;
    public bool SelectionOutlineEnabled => Engine.EditorPreferences.Selection.SelectionOutlineEnabled;
    public ColorF4 OctreeIntersectedBoundsColor => Engine.EditorPreferences.Theme.OctreeIntersectedBoundsColor;
    public ColorF4 OctreeContainedBoundsColor => Engine.EditorPreferences.Theme.OctreeContainedBoundsColor;
    public ColorF4 QuadtreeIntersectedBoundsColor => Engine.EditorPreferences.Theme.QuadtreeIntersectedBoundsColor;
    public ColorF4 QuadtreeContainedBoundsColor => Engine.EditorPreferences.Theme.QuadtreeContainedBoundsColor;
    public bool IsNvidia => RuntimeEngine.Rendering.State.IsNVIDIA;
    public string AssetFileExtension => AssetManager.AssetExtension;
    public string? TextureFallbackPath => Path.Combine(Engine.GameSettings.TexturesFolder, "Filler.png");
    public XRMaterial? InvalidMaterial => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.InvalidMaterial;
    public Vector3 DefaultLuminance => RuntimeEngine.Rendering.Settings.DefaultLuminance;
    public long ElapsedTicks => Engine.ElapsedTicks;
    public float ElapsedTime => Engine.ElapsedTime;
    public string CollectVisibleLatePolicy => Engine.Time.Timer.CollectVisibleLatePolicy.ToString();
    public ulong UpdateFrameId => Engine.Time.Timer.UpdateFrameId;
    public ulong CollectFrameId => Engine.Time.Timer.CollectFrameId;
    public ulong SwapFrameId => Engine.Time.Timer.SwapFrameId;
    public ulong PresentFrameId => Engine.Time.Timer.PresentFrameId;
    public long RequestedCollectGeneration => Engine.Time.Timer.RequestedCollectGeneration;
    public long CompletedCollectGeneration => Engine.Time.Timer.CompletedCollectGeneration;
    public long PublishedCollectGeneration => Engine.Time.Timer.PublishedCollectGeneration;
    public long ConsumedCollectGeneration => Engine.Time.Timer.ConsumedCollectGeneration;
    public long RequiredCollectGeneration => Engine.Time.Timer.RequiredCollectGeneration;
    public float TargetRenderFrequency => Engine.Time.Timer.TargetRenderFrequency;
    public bool IsShuttingDown => Engine.ShuttingDown;
    public RuntimeDebugShapePopulationMode DebugShapePopulationMode
        => Engine.EditorPreferences.Debug.DebugShapePopulationMode switch
        {
            EDebugShapePopulationMode.ParallelInvoke => RuntimeDebugShapePopulationMode.ParallelInvoke,
            EDebugShapePopulationMode.Sequential => RuntimeDebugShapePopulationMode.Sequential,
            _ => RuntimeDebugShapePopulationMode.Tasks,
        };
    public float DebugPointSize => Engine.EditorPreferences.Debug.DebugPointSize;
    public float DebugLineWidth => Engine.EditorPreferences.Debug.DebugLineWidth;
    public float DebugTextMaxLifespan => Engine.EditorPreferences.Debug.DebugTextMaxLifespan;
    public bool IsAppThread => Engine.IsAppThread;
    public bool IsStartingUp => Engine.StartingUp;
    public double UpdateDeltaSeconds => Engine.Time.Timer.Update.Delta;
    public double SmoothedUpdateDeltaSeconds => Engine.SmoothedDelta;
    public long LastUpdateTimestampTicks => Engine.Time.Timer.Update.LastTimestampTicks;
    public double RenderDeltaSeconds => Engine.Time.Timer.Render.Delta;
    public long LastRenderTimestampTicks => Engine.Time.Timer.Render.LastTimestampTicks;
    public string DefaultFontFolder => RuntimeEngine.Rendering.Settings.DefaultFontFolder;
    public string DefaultFontFileName => RuntimeEngine.Rendering.Settings.DefaultFontFileName;
    public bool RenderMesh2DBounds => Engine.EditorPreferences.Debug.RenderMesh2DBounds;
    public bool RenderUITransformCoordinate => Engine.EditorPreferences.Debug.RenderUITransformCoordinate;
    public ColorF4 Bounds2DColor => Engine.EditorPreferences.Theme.Bounds2DColor;
    public long TrackedVramBytes => RuntimeEngine.Rendering.Stats.Vram.AllocatedVRAMBytes;
    public long TrackedVramBudgetBytes => RuntimeEngine.Rendering.Stats.Vram.VramBudgetBytes;
    public bool EnableGpuIndirectDebugLogging => Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;
    public EOcclusionCullingMode GpuOcclusionCullingMode => Engine.EffectiveSettings.GpuOcclusionCullingMode;
    public int CpuQueryOcclusionRetestPeriodFrames => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionRetestPeriodFrames;
    public int CpuQueryOcclusionMaxQueriesPerFrame => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionMaxQueriesPerFrame;
    public float CpuQueryOcclusionVisibleDemotionBudgetFraction => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionVisibleDemotionBudgetFraction;
    public int CpuQueryOcclusionRecoveryMinCadenceFrames => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionRecoveryMinCadenceFrames;
    public float CpuQueryOcclusionSmallMotionMeters => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionSmallMotionMeters;
    public float CpuQueryOcclusionMediumMotionMeters => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionMediumMotionMeters;
    public float CpuQueryOcclusionLargeMotionMeters => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionLargeMotionMeters;
    public float CpuQueryOcclusionCameraCutMeters => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionCameraCutMeters;
    public float CpuQueryOcclusionSmallRotationDegrees => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionSmallRotationDegrees;
    public float CpuQueryOcclusionMediumRotationDegrees => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionMediumRotationDegrees;
    public float CpuQueryOcclusionLargeRotationDegrees => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionLargeRotationDegrees;
    public float CpuQueryOcclusionCameraCutRotationDegrees => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionCameraCutRotationDegrees;
    public float CpuQueryOcclusionVrHeadMotionMeters => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionVrHeadMotionMeters;
    public float CpuQueryOcclusionVrHeadRotationDegrees => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionVrHeadRotationDegrees;
    public ECpuQueryStereoMode CpuQueryOcclusionStereoMode => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionStereoMode;
    public int CpuQueryOcclusionMaxPendingFrames => RuntimeEngine.Rendering.Settings.CpuQueryOcclusionMaxPendingFrames;
    public bool EnableCpuSoftwareOcclusionCulling => Engine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling;
    public int CpuSocBufferWidth => Engine.EffectiveSettings.CpuSocBufferWidth;
    public int CpuSocBufferHeight => Engine.EffectiveSettings.CpuSocBufferHeight;
    public int CpuSocOccluderTriangleBudget => Engine.EffectiveSettings.CpuSocOccluderTriangleBudget;
    public int CpuSocMaxOccluders => Engine.EffectiveSettings.CpuSocMaxOccluders;
    public float CpuSocMinOccluderScreenArea => Engine.EffectiveSettings.CpuSocMinOccluderScreenArea;
    public bool CpuSocUseAvx2 => Engine.EffectiveSettings.CpuSocUseAvx2;
    public bool CpuSocDebugVisualization => Engine.EffectiveSettings.CpuSocDebugVisualization;
    public bool CpuSocDebugForceVisible => Engine.EffectiveSettings.CpuSocDebugForceVisible;
    public TextureRuntimeLogMode TextureLogMode => RuntimeEngine.Rendering.Settings.TextureLogMode;
    public double TextureSlowCpuDecodeResizeMilliseconds => RuntimeEngine.Rendering.Settings.TextureSlowCpuDecodeResizeMilliseconds;
    public double TextureSlowMipBuildMilliseconds => RuntimeEngine.Rendering.Settings.TextureSlowMipBuildMilliseconds;
    public double TextureSlowUploadChunkMilliseconds => RuntimeEngine.Rendering.Settings.TextureSlowUploadChunkMilliseconds;
    public double TextureSlowTransitionMilliseconds => RuntimeEngine.Rendering.Settings.TextureSlowTransitionMilliseconds;
    public double TextureSlowQueueWaitMilliseconds => RuntimeEngine.Rendering.Settings.TextureSlowQueueWaitMilliseconds;
    public double TextureUploadFrameBudgetMilliseconds => RuntimeEngine.Rendering.Settings.TextureUploadFrameBudgetMilliseconds;
    public ETwoPlayerPreference TwoPlayerViewportPreference => Engine.GameSettings.TwoPlayerViewportPreference;
    public EThreePlayerPreference ThreePlayerViewportPreference => Engine.GameSettings.ThreePlayerViewportPreference;
    public RuntimeGraphicsApiKind CurrentRenderBackend
    {
        get
        {
            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (renderer is null)
                renderer = RuntimeEngine.Windows.FirstOrDefault()?.Renderer;

            return GetRendererBackend(renderer);
        }
    }

    public IRuntimeRendererHost? CurrentRenderer
        => AbstractRenderer.Current ?? RuntimeEngine.Windows.FirstOrDefault()?.Renderer;

    public IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState
        => RuntimeEngine.Rendering.State.ActiveRenderCommandExecutionState;

    public IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext
        => RuntimeEngine.Rendering.State.CurrentRenderingPipeline;

    public bool IsPlayModeTransitioning => Engine.PlayMode.IsTransitioning;
    public string PlayModeStateName => Engine.PlayMode.State.ToString();
    public EAntiAliasingMode DefaultAntiAliasingMode => Engine.EffectiveSettings.AntiAliasingMode;
    public bool EnableNvidiaDlss => Engine.EffectiveSettings.EnableNvidiaDlss;
    public EDlssQualityMode DlssQuality => Engine.EffectiveSettings.DlssQuality;
    public float DlssCustomScale => RuntimeEngine.Rendering.Settings.DlssCustomScale;
    public float DlssSharpness => RuntimeEngine.Rendering.Settings.DlssSharpness;
    public bool EnableNvidiaDlssFrameGeneration => Engine.EffectiveSettings.EnableNvidiaDlssFrameGeneration;
    public ENvidiaDlssFrameGenerationMode NvidiaDlssFrameGenerationMode => Engine.EffectiveSettings.NvidiaDlssFrameGenerationMode;
    public uint DefaultMsaaSampleCount => Engine.EffectiveSettings.MsaaSampleCount;
    public bool DefaultOutputHDR => RuntimeEngine.Rendering.Settings.OutputHDR;
    public float DefaultTsrRenderScale => RuntimeEngine.Rendering.Settings.TsrRenderScale;
    public bool EnableRenderStatisticsTracking => RuntimeEngine.Rendering.Stats.EnableTracking;
    public bool EnableGpuRenderPipelineProfiling => Engine.EditorPreferences.Diagnostics.Profiler.EnableGpuRenderPipelineProfiling;
    public bool GpuRenderPipelineTimingsReady => RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineTimingsReady;
    public double GpuRenderPipelineFrameMs => RuntimeEngine.Rendering.Stats.GpuPipelineProfiler.GpuRenderPipelineFrameMs;
    public ulong CurrentRenderFrameId => RuntimeEngine.Rendering.State.RenderFrameId;
    public bool ProvidesShadowAtlasSettings => true;
    public bool UseSpotShadowAtlas => RuntimeEngine.Rendering.Settings.UseSpotShadowAtlas;
    public bool UseDirectionalShadowAtlas => RuntimeEngine.Rendering.Settings.UseDirectionalShadowAtlas;
    public bool UsePointShadowAtlas => RuntimeEngine.Rendering.Settings.UsePointShadowAtlas;
    public uint ShadowAtlasPageSize => RuntimeEngine.Rendering.Settings.ShadowAtlasPageSize;
    public int MaxShadowAtlasPages => RuntimeEngine.Rendering.Settings.MaxShadowAtlasPages;
    public long MaxShadowAtlasMemoryBytes => RuntimeEngine.Rendering.Settings.MaxShadowAtlasMemoryBytes;
    public int MaxShadowTilesRenderedPerFrame => RuntimeEngine.Rendering.Settings.MaxShadowTilesRenderedPerFrame;
    public float MaxShadowRenderMilliseconds => RuntimeEngine.Rendering.Settings.MaxShadowRenderMilliseconds;
    public int MaxDirectionalCascadeAtlasStaleFrames => RuntimeEngine.Rendering.Settings.MaxDirectionalCascadeAtlasStaleFrames;
    public uint MinShadowAtlasTileResolution => RuntimeEngine.Rendering.Settings.MinShadowAtlasTileResolution;
    public uint MaxShadowAtlasTileResolution => RuntimeEngine.Rendering.Settings.MaxShadowAtlasTileResolution;

    public void LogOutput(string message)
        => Debug.Out(message);

    public IDisposable? PushRenderingPipeline(IRuntimeRenderPipelineFrameContext pipeline)
        => pipeline is XRRenderPipelineInstance instance
            ? RuntimeEngine.Rendering.State.PushRenderingPipeline(instance)
            : null;

    public void LogWarning(string message)
        => Debug.LogWarning(message);

    public void LogException(Exception ex, string? context = null)
        => Debug.LogException(ex, context);

    public void RecordMissingAsset(string assetPath, string category, string? context = null)
        => AssetDiagnostics.RecordMissingAsset(assetPath, category, context);

    public byte[] ReadAllBytes(string filePath)
        => DirectStorageIO.ReadAllBytes(filePath);

    public string ResolveTextureStreamingAuthorityPath(string filePath)
        => Engine.Assets?.ResolveTextureStreamingAuthorityPath(filePath) ?? Path.GetFullPath(filePath);

    public SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format)
    {
        ISparseTextureStreamingBackendCapability? capability =
            GetPrimaryRendererCapability<ISparseTextureStreamingBackendCapability>();
        if (capability is not null)
            return capability.GetSparseTextureStreamingSupport(format);

        return SparseTextureStreamingSupport.Unsupported("Sparse texture streaming is unavailable because no renderer-specific sparse handler is active.");
    }

    public bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null)
    {
        ISparseTextureStreamingBackendCapability? capability =
            GetPrimaryRendererCapability<ISparseTextureStreamingBackendCapability>();
        return capability?.TryScheduleSparseTextureStreamingTransitionAsync(
            texture,
            request,
            cancellationToken,
            onCompleted,
            onError) ?? false;
    }

    public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult)
    {
        ISparseTextureStreamingBackendCapability? capability =
            GetPrimaryRendererCapability<ISparseTextureStreamingBackendCapability>();
        return capability is null
            ? SparseTextureStreamingFinalizeResult.Failed(
                "No renderer sparse texture streaming capability is active.")
            : capability.FinalizeSparseTextureStreamingTransition(texture, request, transitionResult);
    }

    public EnumeratorJob ScheduleEnumeratorJob(
        Func<IEnumerable> routineFactory,
        JobPriority priority = JobPriority.Normal,
        Action? completed = null,
        Action<Exception>? error = null,
        CancellationToken cancellationToken = default)
    {
        EnumeratorJob job = new(routineFactory, onCompleted: completed, onError: error);
        Engine.Jobs.Schedule(job, priority, JobAffinity.Any, cancellationToken);
        return job;
    }

    public void SubscribeViewportSwapBuffers(Action swapBuffers)
    {
        Engine.Time.Timer.SwapBuffers += swapBuffers;
    }

    public void UnsubscribeViewportSwapBuffers(Action swapBuffers)
    {
        Engine.Time.Timer.SwapBuffers -= swapBuffers;
    }

    public void SubscribeViewportCollectVisible(Action collectVisible)
    {
        Engine.Time.Timer.CollectVisible += collectVisible;
    }

    public void UnsubscribeViewportCollectVisible(Action collectVisible)
    {
        Engine.Time.Timer.CollectVisible -= collectVisible;
    }

    public void SubscribeViewportPostCollectVisible(Action postCollectVisible)
    {
        Engine.Time.Timer.PostCollectVisible += postCollectVisible;
    }

    public void UnsubscribeViewportPostCollectVisible(Action postCollectVisible)
    {
        Engine.Time.Timer.PostCollectVisible -= postCollectVisible;
    }

    public void SubscribeUpdateFrame(Action callback)
        => Engine.Time.Timer.UpdateFrame += callback;

    public void UnsubscribeUpdateFrame(Action callback)
        => Engine.Time.Timer.UpdateFrame -= callback;

    public void SubscribePostUpdateFrame(Action callback)
        => Engine.Time.Timer.PostUpdateFrame += callback;

    public void UnsubscribePostUpdateFrame(Action callback)
        => Engine.Time.Timer.PostUpdateFrame -= callback;

    public void SubscribeRenderFrame(Action callback)
        => Engine.Time.Timer.RenderFrame += callback;

    public void UnsubscribeRenderFrame(Action callback)
        => Engine.Time.Timer.RenderFrame -= callback;

    public void SubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame)
    {
        Engine.Time.Timer.SwapBuffers += swapBuffers;
        Engine.Time.Timer.RenderFrame += renderFrame;
    }

    public void UnsubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame)
    {
        Engine.Time.Timer.SwapBuffers -= swapBuffers;
        Engine.Time.Timer.RenderFrame -= renderFrame;
    }

    public bool TryDispatchInteractiveResizeFrame()
        => Engine.Time.Timer.TryDispatchInteractiveResizeFrame();

    public void SubscribePlayModeTransitions(Action callback)
    {
        Engine.PlayMode.PreEnterPlay += callback;
        Engine.PlayMode.PostExitPlay += callback;
    }

    public void UnsubscribePlayModeTransitions(Action callback)
    {
        Engine.PlayMode.PreEnterPlay -= callback;
        Engine.PlayMode.PostExitPlay -= callback;
    }

    public void EnqueueRenderThreadTask(Action task)
        => Engine.EnqueueRenderThreadTask(task);

    public void EnqueueRenderThreadTask(Action task, RenderThreadJobKind renderThreadKind)
        => Engine.EnqueueRenderThreadTask(task, renderThreadKind);

    public void EnqueueRenderThreadTask(Action task, string reason)
        => Engine.EnqueueRenderThreadTask(task, reason);

    public void EnqueueRenderThreadTask(Action task, string reason, RenderThreadJobKind renderThreadKind)
        => Engine.EnqueueRenderThreadTask(task, reason, renderThreadKind);

    public bool IsFrameSwapThread => Engine.IsFrameSwapThread;

    public void EnqueueFrameSwapTask(Action task, string reason)
        => Engine.EnqueueSwapTask(task);

    public T InvokeRenderThreadTask<T>(
        Func<T> task,
        string reason,
        RenderThreadJobKind renderThreadKind = RenderThreadJobKind.Unknown)
    {
        if (Engine.IsRenderThread)
            return task();

        T? result = default;
        ExceptionDispatchInfo? exception = null;
        using ManualResetEventSlim completed = new(false);

        Engine.EnqueueRenderThreadTask(
            () =>
            {
                try
                {
                    result = task();
                }
                catch (Exception ex)
                {
                    exception = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    completed.Set();
                }
            },
            reason,
            renderThreadKind);

        completed.Wait();
        exception?.Throw();
        return result!;
    }

    public void EnqueueAppThreadTask(Action task)
        => Engine.EnqueueAppThreadTask(task);

    public void EnqueueAppThreadTask(Action task, string reason)
        => Engine.EnqueueAppThreadTask(task, reason);

    public void EnqueueWindowThreadTask(IRuntimeRenderWindowHost window, Action task, string reason)
        => Engine.WindowPumpHost.EnqueueWindowTask(window, task, reason);

    public T InvokeWindowThreadTask<T>(IRuntimeRenderWindowHost window, Func<T> task, string reason)
        => Engine.WindowPumpHost.InvokeWindowTask(window, task, reason);

    public void EnqueueRenderThreadCoroutine(Func<bool> task)
        => Engine.AddRenderThreadCoroutine(task);

    public void EnqueueRenderThreadCoroutine(Func<bool> task, RenderThreadJobKind renderThreadKind)
        => Engine.AddRenderThreadCoroutine(task, renderThreadKind);

    public void EnqueueRenderThreadCoroutine(Func<bool> task, string reason)
        => Engine.AddRenderThreadCoroutine(task, reason);

    public void EnqueueRenderThreadCoroutine(Func<bool> task, string reason, RenderThreadJobKind renderThreadKind)
        => Engine.AddRenderThreadCoroutine(task, reason, renderThreadKind);

    public void ProcessRenderThreadTasks()
        => Engine.ProcessMainThreadTasks();

    public void MarkRenderFrameReadyForCollect(IRuntimeRenderWindowHost window)
    {
        if (window is not XRWindow currentWindow || !currentWindow.IsTickLinked)
            return;

        int tickLinkedWindowCount = 0;
        for (int i = 0; i < RuntimeEngine.Windows.Count; i++)
        {
            if (!RuntimeEngine.Windows[i].IsTickLinked)
                continue;

            tickLinkedWindowCount++;
            if (tickLinkedWindowCount > 1)
                return;
        }

        if (tickLinkedWindowCount == 1)
            Engine.Time.Timer.MarkRenderFrameReadyForCollect();
    }

    public IDisposable? PushTransformId(uint transformId)
        => RuntimeEngine.Rendering.State.PushTransformId(transformId);

    public void RecordOctreeSkippedMove()
        => RuntimeEngine.Rendering.Stats.Octree.RecordOctreeSkippedMove();

    public ECpuSceneCullingStructure CpuSceneCullingStructure
        => Engine.EffectiveSettings.CpuSceneCullingStructure;

    public void ProcessGpuPhysicsChainDispatches()
        => GPUPhysicsChainDispatcher.Instance.ProcessDispatches();

    public void ProcessGpuPhysicsChainCompletions()
        => GPUPhysicsChainDispatcher.Instance.ProcessCompletions();

    public void RecordDebugDrawComponentCallback()
        => RuntimeEngine.Rendering.Debug.RecordDebugDrawComponentCallback();

    public void RenderDebugRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderRect2D(rectangle, solid, color);

    public void RenderDebugLine(Vector3 start, Vector3 end, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderLine(start, end, color);

    public void RenderDebugSphere(Vector3 center, float radius, bool solid, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderSphere(center, radius, solid, color);

    public void RenderDebugCone(Vector3 center, Vector3 up, float radius, float height, bool solid, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderCone(center, up, radius, height, solid, color);

    public void RenderDebugAABB(Vector3 halfExtents, Vector3 center, bool solid, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderAABB(halfExtents, center, solid, color);

    public void RenderDebugBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderBox(halfExtents, center, transform, solid, color);

    public void RenderDebugBox(
        Vector3 halfExtents,
        Vector3 center,
        Matrix4x4 transform,
        bool solid,
        ColorF4 color,
        bool depthTested)
        => RuntimeEngine.Rendering.Debug.RenderBox(halfExtents, center, transform, solid, color, depthTested);

    public void RenderDebugCapsule(Capsule capsule, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderCapsule(capsule, color);

    public void RenderDebugCapsule(Vector3 start, Vector3 end, float radius, bool solid, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderCapsule(start, end, radius, solid, color);

    public void RenderDebugCircle(
        Vector3 center,
        Quaternion rotation,
        float radius,
        bool solid,
        ColorF4 color,
        bool depthTested = false)
        => RuntimeEngine.Rendering.Debug.RenderCircle(center, rotation, radius, solid, color, depthTested);

    public void RenderDebugCylinder(
        Matrix4x4 transform,
        Vector3 localUpAxis,
        float radius,
        float halfHeight,
        bool solid,
        ColorF4 color,
        bool depthTested = false)
        => RuntimeEngine.Rendering.Debug.RenderCylinder(
            transform,
            localUpAxis,
            radius,
            halfHeight,
            solid,
            color,
            depthTested);

    public void RenderDebugQuad(Vector3 center, Rotator rotation, Vector2 extents, bool solid, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderQuad(center, rotation, extents, solid, color);

    public void RenderDebugPoint(Vector3 position, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderPoint(position, color);

    public void RenderDebugText(Vector3 position, string text, ColorF4 color)
        => RuntimeEngine.Rendering.Debug.RenderText(position, text, color);

    public void RenderDebugText(Vector3 position, string text, ColorF4 color, float scale)
        => RuntimeEngine.Rendering.Debug.RenderText(position, text, color, scale);

    public void RenderDebugShapes(bool depthTested)
        => RuntimeEngine.Rendering.Debug.RenderShapes(depthTested);

    public string EngineAssetsPath => Engine.Assets.EngineAssetsPath;
    public string GameAssetsPath => Engine.Assets.GameAssetsPath;
    public string? GameCachePath => Engine.Assets.GameCachePath;

    public string ResolveEngineAssetPath(params string[] relativePathFolders)
        => Engine.Assets.ResolveEngineAssetPath(relativePathFolders);

    public object? GetOrCreateThirdPartyImportOptions(string sourcePath, Type assetType)
        => Engine.Assets.GetOrCreateThirdPartyImportOptions(sourcePath, assetType);

    public TAsset? LoadAsset<TAsset>(string filePath, JobPriority priority, bool bypassJobThread)
        where TAsset : XRAsset, new()
        => Engine.Assets.Load<TAsset>(filePath, priority, bypassJobThread);

    public bool TryResolveThirdPartyCachePath(
        string filePath,
        Type assetType,
        string? cacheVariantKey,
        out string cachePath)
        => Engine.Assets.TryResolveThirdPartyCachePath(filePath, assetType, cacheVariantKey, out cachePath);

    public TAsset? LoadThirdPartyVariantWithCache<TAsset>(
        string filePath,
        object? importOptions,
        string cacheVariantKey,
        JobPriority priority,
        bool bypassJobThread)
        where TAsset : XRAsset, new()
        => Engine.Assets.Load3rdPartyVariantWithCache<TAsset>(
            filePath,
            importOptions,
            cacheVariantKey,
            priority,
            bypassJobThread);

    public void EvictAsset(XRAsset asset, string resolvedPath)
    {
        Engine.Assets.LoadedAssetsByPathInternal.TryRemove(resolvedPath, out _);
        if (!string.IsNullOrWhiteSpace(asset.OriginalPath))
            Engine.Assets.LoadedAssetsByOriginalPathInternal.TryRemove(asset.OriginalPath, out _);
        Engine.Assets.LoadedAssetsByIDInternal.TryRemove(asset.ID, out _);
    }

    public Task<TAsset> LoadEngineAssetAsync<TAsset>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where TAsset : XRAsset, new()
        => Engine.Assets.LoadEngineAssetAsync<TAsset>(priority, bypassJobThread, relativePathFolders);

    public TAsset? LoadAsset<TAsset>(string filePath) where TAsset : XRAsset, new()
        => Engine.Assets?.Load<TAsset>(filePath);

    public IRuntimeRenderPipelineHost? CreateDefaultRenderPipeline()
        => RuntimeEngine.Rendering.NewRenderPipeline();

    public VisualScene3D CreateVisualScene()
        => new();

    public AbstractPhysicsScene CreatePhysicsScene()
        => Engine.UserSettings.PhysicsLibrary switch
        {
            EPhysicsLibrary.Jitter => new JitterScene(),
            EPhysicsLibrary.Jolt => new JoltScene(),
            _ => new PhysxScene(),
        };

    public IRuntimeRendererHost CreateRenderer(IRuntimeRenderWindowHost window, RuntimeGraphicsApiKind apiKind)
        => _rendererBackends.CreateRequired(apiKind, new RendererBackendCreateContext(window));

    public IRuntimeWindowScenePanelAdapter CreateWindowScenePanelAdapter()
        => XRWindow.CreateScenePanelAdapter();

    public BoundingRectangle? GetScenePanelRenderRegion(IRuntimeRenderWindowHost window)
        => window is XRWindow xrWindow
            ? RuntimeEngine.Rendering.ScenePanelRenderRegionProvider?.Invoke(xrWindow)
            : null;

    public bool AllowWindowClose(IRuntimeRenderWindowHost window)
    {
        if (Engine.WindowCloseRequested is null)
            return true;

        XRWindow xrWindow = (XRWindow)window;
        return Engine.WindowCloseRequested.Invoke(xrWindow) == Engine.WindowCloseRequestResult.Allow;
    }

    public bool QuiesceForWindowRendererTeardown(IRuntimeRenderWindowHost window)
        => window is not XRWindow xrWindow || Engine.QuiesceForWindowRendererTeardown(xrWindow);

    public void RemoveWindow(IRuntimeRenderWindowHost window)
    {
        if (window is XRWindow xrWindow)
            Engine.RemoveWindow(xrWindow);
    }

    public void ReplicateWindowTargetWorldChange(IRuntimeRenderWindowHost window)
    {
        if (window is not XRWindow xrWindow || (Engine.Networking?.IsClient ?? false))
            return;

        string? encoded = RuntimeEngine.EncodeWindowTargetWorldHierarchyJson(xrWindow);
        Engine.Networking?.ReplicateStateChange(
            new StateChangeInfo(
                EStateChangeType.WorldChange,
                encoded is null ? "null" : encoded),
            true,
            true);
    }

    public void PublishRenderStatsSnapshot()
    {
#if !XRE_PUBLISHED
        Engine.ProfileCapture.RecordRenderStatsSnapshot();
#endif
    }

    public void SubscribePreUpdateFrame(Action callback)
        => Engine.Time.Timer.PreUpdateFrame += callback;

    public void UnsubscribePreUpdateFrame(Action callback)
        => Engine.Time.Timer.PreUpdateFrame -= callback;

    public void BeginRenderStatsFrame()
        => RuntimeEngine.Rendering.Stats.BeginFrame();

    public void IncrementRenderDrawCalls(int count)
        => RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(count);

    public void IncrementRenderMultiDrawCalls(int count)
        => RuntimeEngine.Rendering.Stats.Frame.IncrementMultiDrawCalls(count);

    public void AddRenderTrianglesRendered(int count)
        => RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(count);

    public void AddRenderGpuBufferAllocation(long bytes)
        => RuntimeEngine.Rendering.Stats.Vram.AddBufferAllocation(bytes);

    public void RemoveRenderGpuBufferAllocation(long bytes)
        => RuntimeEngine.Rendering.Stats.Vram.RemoveBufferAllocation(bytes);

    public void AddRenderGpuTextureAllocation(long bytes)
        => RuntimeEngine.Rendering.Stats.Vram.AddTextureAllocation(bytes);

    public void RemoveRenderGpuTextureAllocation(long bytes)
        => RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(bytes);

    public void AddRenderGpuRenderBufferAllocation(long bytes)
        => RuntimeEngine.Rendering.Stats.Vram.AddRenderBufferAllocation(bytes);

    public void RemoveRenderGpuRenderBufferAllocation(long bytes)
        => RuntimeEngine.Rendering.Stats.Vram.RemoveRenderBufferAllocation(bytes);

    public bool CanAllocateRenderVram(long requestedBytes, long existingAllocationBytes, out long projectedBytes, out long budgetBytes)
        => RuntimeEngine.Rendering.Stats.Vram.CanAllocateVram(requestedBytes, existingAllocationBytes, out projectedBytes, out budgetBytes);

    public void RecordRenderGpuBufferMapped(int count = 1)
        => RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped(count);

    public void RecordRenderGpuReadbackBytes(long bytes)
        => RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(bytes);

    public void RecordRenderRendererStateCounter(ERendererProfilerCounter counter, long count = 1)
        => RuntimeEngine.Rendering.Stats.RendererState.RecordCounter(counter, count);

    public void RecordRenderMemoryBarrier(EMemoryBarrierMask mask)
        => RuntimeEngine.Rendering.Stats.RendererState.RecordMemoryBarrier(mask);

    public void RecordRenderSceneAssetVisible(
        string? sourceAssetIdentity,
        string? cookedVariantIdentity,
        string? meshName,
        string? materialName,
        int materialSlots,
        int textureCount,
        long triangleCount,
        bool skinned,
        string? representation)
        => RuntimeEngine.Rendering.Stats.SceneAssets.RecordVisibleRenderer(
            sourceAssetIdentity,
            cookedVariantIdentity,
            meshName,
            materialName,
            materialSlots,
            textureCount,
            triangleCount,
            skinned,
            representation);

    public void RecordRenderTextureUpload(long bytes, TimeSpan elapsed)
        => RuntimeEngine.Rendering.Stats.SceneAssets.RecordTextureUpload(bytes, elapsed);

    public void RecordRenderSkinningUpload(
        long boneMatrixBytes,
        long blendshapeWeightBytes,
        int skinningDispatches = 0,
        int blendshapeDispatches = 0,
        long coreInfluenceBytes = 0,
        long spillHeaderBytes = 0,
        long spillEntryBytes = 0,
        long skinPaletteBytes = 0,
        int skippedSkinningDispatches = 0,
        int reusedSkinnedOutputBuffers = 0,
        int liveSkinningShaderPermutations = 0,
        long blendshapeActiveListUploadBytes = 0,
        long blendshapeDeltaBytes = 0,
        int blendshapeAuthoredShapeCount = 0,
        int blendshapeActiveShapeCount = 0,
        int blendshapeAffectedVertexCount = 0,
        int skippedBlendshapeDispatches = 0,
        int compactedActiveBlendshapeCount = 0,
        int liveBlendshapeShaderPermutations = 0)
        => RuntimeEngine.Rendering.Stats.SceneAssets.RecordSkinningUpload(
            boneMatrixBytes,
            blendshapeWeightBytes,
            skinningDispatches,
            blendshapeDispatches,
            coreInfluenceBytes,
            spillHeaderBytes,
            spillEntryBytes,
            skinPaletteBytes,
            skippedSkinningDispatches,
            reusedSkinnedOutputBuffers,
            liveSkinningShaderPermutations,
            blendshapeActiveListUploadBytes,
            blendshapeDeltaBytes,
            blendshapeAuthoredShapeCount,
            blendshapeActiveShapeCount,
            blendshapeAffectedVertexCount,
            skippedBlendshapeDispatches,
            compactedActiveBlendshapeCount,
            liveBlendshapeShaderPermutations);

    public void RecordRenderShaderVariant(bool requested, bool warming, bool linked, bool failed, bool loadedFromDiskCache, bool generatedThisRun)
        => RuntimeEngine.Rendering.Stats.SceneAssets.RecordShaderVariant(requested, warming, linked, failed, loadedFromDiskCache, generatedThisRun);

    public void RecordRenderGpuDrivenBucketWork(int activeBuckets, int emptyBucketSkips, int fullBucketScans, int materialScatterDispatches)
        => RuntimeEngine.Rendering.Stats.GpuDriven.RecordBucketWork(activeBuckets, emptyBucketSkips, fullBucketScans, materialScatterDispatches);

    public void RecordRenderGpuDrivenCommandCompaction(long culledCommands, long delayedDrawCountValue, long gpuCompactionOverflow, long activeListOverflow, long bucketOverflow, long meshletOverflow)
        => RuntimeEngine.Rendering.Stats.GpuDriven.RecordCommandCompaction(culledCommands, delayedDrawCountValue, gpuCompactionOverflow, activeListOverflow, bucketOverflow, meshletOverflow);

    public void RecordRenderGpuDrivenStageTiming(TimeSpan indirectGeneration, TimeSpan gpuCull, TimeSpan sortCompact)
        => RuntimeEngine.Rendering.Stats.GpuDriven.RecordGpuDrivenStageTiming(indirectGeneration, gpuCull, sortCompact);

    public void RecordRenderGpuDrivenDelayedDiagnosticReadback(long bytes)
        => RuntimeEngine.Rendering.Stats.GpuDriven.RecordDelayedDiagnosticReadback(bytes);

    public void RecordRenderGpuDrivenHiZMode(string? mode)
        => RuntimeEngine.Rendering.Stats.GpuDriven.UpdateHiZMode(mode);

    public void RecordRenderGpuDrivenHiZPhase(bool twoPhase, long phaseOneDraws, long phaseTwoDraws)
        => RuntimeEngine.Rendering.Stats.GpuDriven.RecordHiZPhase(twoPhase, phaseOneDraws, phaseTwoDraws);

    public void RecordRenderVisibilityBuffer(int passDraws, long classifiedPixels, int activeMaterialTiles, int classificationOverflow, TimeSpan reconstruction, TimeSpan materialShading)
        => RuntimeEngine.Rendering.Stats.GpuDriven.RecordVisibilityBuffer(passDraws, classifiedPixels, activeMaterialTiles, classificationOverflow, reconstruction, materialShading);

    public void RecordRenderRvcFrameCounters(RvcFrameCounters counters)
    {
        RuntimeEngine.Rendering.Stats.Rvc.RecordFrameCounters(counters);
    }

    public void RecordRenderRvcFrameProfile(RvcFrameProfileSnapshot profile)
    {
        RuntimeEngine.Rendering.Stats.Rvc.RecordFrameProfile(profile);
    }

    public void RecordRenderGpuCpuFallback(int eventCount, int recoveredCommands)
        => RuntimeEngine.Rendering.Stats.GpuFallback.RecordGpuCpuFallback(eventCount, recoveredCommands);

    public void RecordRenderForbiddenGpuFallback(int eventCount = 1)
        => RuntimeEngine.Rendering.Stats.GpuFallback.RecordForbiddenGpuFallback(eventCount);

    public void RecordRenderResourceChurn(string resourceKind, string resourceName, string eventName, string? reason = null)
        => RuntimeEngine.Rendering.Stats.ResourceChurn.Record(resourceKind, resourceName, eventName, reason);

    public void RecordRenderShadowAtlasSolveDiagnostics(ShadowAtlasSolveDiagnostics diagnostics)
        => RuntimeEngine.Rendering.Stats.ShadowAtlas.RecordSolveDiagnostics(diagnostics);

    public void RecordRenderGpuTransparencyDomainCounts(uint opaqueOrOtherVisible, uint maskedVisible, uint approximateVisible, uint exactVisible)
        => RuntimeEngine.Rendering.Stats.GpuTransparency.RecordGpuTransparencyDomainCounts(opaqueOrOtherVisible, maskedVisible, approximateVisible, exactVisible);

    public void RecordRenderGpuMeshletStrategyRequested(int eventCount = 1)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletStrategyRequested(eventCount);

    public void RecordRenderGpuMeshletProductionFrame(int eventCount = 1)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletProductionFrame(eventCount);

    public void RecordRenderGpuMeshletFallback(int eventCount = 1)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletFallback(eventCount);

    public void RecordRenderGpuMeshletDispatchSkipped(int eventCount = 1)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletDispatchSkipped(eventCount);

    public void RecordRenderGpuMeshletTaskStats(uint emitted, uint frustumCulled, uint coneCulled, uint hiZCulled)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletTaskStats(emitted, frustumCulled, coneCulled, hiZCulled);

    public void RecordRenderGpuMeshletExpansionOverflow(uint overflowCount)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletExpansionOverflow(overflowCount);

    public void RecordRenderGpuMeshletBufferBytesResident(long bytes)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletBufferBytesResident(bytes < 0 ? 0UL : (ulong)bytes);

    public void RecordRenderGpuMeshletInstrumentation(
        uint visibleMeshletCount,
        uint dispatchedMeshletCount,
        uint taskRecordOverflowCount,
        TimeSpan dispatchTime,
        uint readbackBytes)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletInstrumentation(
            visibleMeshletCount,
            dispatchedMeshletCount,
            taskRecordOverflowCount,
            dispatchTime,
            readbackBytes);

    public void RecordRenderGpuMeshletCacheHit(int eventCount = 1)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheHit(eventCount);

    public void RecordRenderGpuMeshletCacheMiss(int eventCount = 1)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheMiss(eventCount);

    public void RecordRenderGpuMeshletCacheStale(int eventCount = 1)
        => RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheStale(eventCount);

    public void RecordRenderOctreeCollect(int visibleRenderables, int emittedCommands)
        => RuntimeEngine.Rendering.Stats.Octree.RecordOctreeCollect(visibleRenderables, emittedCommands);

    public void RecordRenderCpuSpatialTreeStats(string mode, SpatialTreeOccupancyStats occupancy, long collectTicks)
        => RuntimeEngine.Rendering.Stats.Octree.RecordCpuSpatialTreeStats(mode, occupancy, collectTicks);

    public void RecordRenderRtxIoCopyIndirect(long copiedBytes, TimeSpan submissionTime)
        => RuntimeEngine.Rendering.Stats.RtxIo.RecordRtxIoCopyIndirect(copiedBytes, submissionTime);

    public void RecordRenderRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan submissionTime)
        => RuntimeEngine.Rendering.Stats.RtxIo.RecordRtxIoDecompression(compressedBytes, decompressedBytes, submissionTime);

    public void RecordRenderSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded)
        => RuntimeEngine.Rendering.Stats.SkinnedBounds.RecordSkinnedBoundsRefreshDeferredFinished(queueWaitTicks, cpuJobTicks, applyTicks, succeeded);

    public void RecordRenderSkinnedBoundsRefreshDeferredScheduled()
        => RuntimeEngine.Rendering.Stats.SkinnedBounds.RecordSkinnedBoundsRefreshDeferredScheduled();

    public void RecordRenderSkinnedBoundsRefreshGpuCompleted(long computeTicks, long applyTicks)
        => RuntimeEngine.Rendering.Stats.SkinnedBounds.RecordSkinnedBoundsRefreshGpuCompleted(computeTicks, applyTicks);

    public void RecordRenderVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrCommandBuildTimes(leftBuildTime, rightBuildTime);

    public void RecordRenderVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrPerViewVisibleCounts(leftVisible, rightVisible);

    public void RecordRenderVrRenderSubmitTime(TimeSpan submitTime)
    {
        RuntimeEngine.Rendering.Stats.Vr.RecordVrRenderSubmitTime(submitTime);
        RecordVrSubmitFrameOutput(
            RuntimeEngine.VRState.IsOpenXRActive ? EFrameOutputKind.OpenXREyeSubmit : EFrameOutputKind.OpenVRSubmit,
            submitTime,
            RuntimeEngine.VRState.IsOpenXRActive ? "OpenXR render submit" : "OpenVR render submit");
    }

    public void RecordRenderVrXrWaitFrameBlockTime(TimeSpan waitTime)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrWaitFrameBlockTime(waitTime);

    public void RecordRenderVrXrEndFrameSubmitTime(TimeSpan submitTime, ulong renderFrameId = 0UL)
    {
        RuntimeEngine.Rendering.Stats.Vr.RecordVrXrEndFrameSubmitTime(submitTime);
        // A successful xrEndFrame is authoritative proof that the OpenXR session is active.
        // Do not race the application-side IsInVR mirror when publishing render telemetry.
        RecordVrSubmitFrameOutput(
            EFrameOutputKind.OpenXREyeSubmit,
            submitTime,
            "OpenXR xrEndFrame submit",
            renderFrameId,
            requireActiveVr: false);
    }

    public void RecordRenderVrXrPredictedToLatePoseDelta(double millimeters, double degrees)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrPredictedToLatePoseDelta(millimeters, degrees);

    public void RecordRenderVrXrPredictedDisplayLeadTime(double leadTimeMs)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrPredictedDisplayLeadTime(leadTimeMs);

    public void RecordRenderVrXrMissedDeadlineFrame()
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrMissedDeadlineFrame();

    public void RecordRenderVrXrTrackingLossFrame()
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrTrackingLossFrame();

    public void RecordRenderVrXrRelocatePredictedTime(TimeSpan elapsed)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrRelocatePredictedTime(elapsed);

    public void RecordRenderVrXrCollectFrustumExpansionDegrees(double degrees)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrCollectFrustumExpansionDegrees(degrees);

    public void RecordRenderVrXrPacingThreadIdleTime(TimeSpan elapsed)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrPacingThreadIdleTime(elapsed);

    public void RecordRenderVrXrPacingHandoffStall()
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrXrPacingHandoffStall();

    private static void RecordVrSubmitFrameOutput(
        EFrameOutputKind outputKind,
        TimeSpan submitTime,
        string name,
        ulong renderFrameId = 0UL,
        bool requireActiveVr = true)
    {
        if (requireActiveVr && !RuntimeEngine.VRState.IsInVR)
            return;

        double cpuMs = Math.Max(0.0, submitTime.TotalMilliseconds * 0.5);
        ulong frameId = renderFrameId != 0UL
            ? renderFrameId
            : RuntimeEngine.Rendering.State.RenderFrameId;
        RecordVrSubmitFrameOutputForEye(outputKind, EVrOutputViewKind.LeftEye, frameId, cpuMs, name + " left");
        RecordVrSubmitFrameOutputForEye(outputKind, EVrOutputViewKind.RightEye, frameId, cpuMs, name + " right");
    }

    private static void RecordVrSubmitFrameOutputForEye(
        EFrameOutputKind outputKind,
        EVrOutputViewKind viewKind,
        ulong frameId,
        double cpuMs,
        string name)
    {
        var pacing = FrameOutputPacingDecision.Due(viewKind, outputKind, frameId);
        var telemetry = new FrameOutputTelemetry(
            outputKind,
            viewKind,
            EFrameOutputPhase.Submit,
            pacing,
            name,
            string.Empty,
            true,
            true,
            true,
            false,
            false,
            true,
            0,
            0,
            0,
            0,
            cpuMs,
            0.0);
        RuntimeEngine.Rendering.Stats.FrameOutputs.RecordOutput(telemetry);
    }

    public void RecordRenderVulkanAdhocBarrier(int emittedCount, int redundantCount)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAdhocBarrier(emittedCount, redundantCount);

    public void RecordRenderVulkanAllocation(int allocationClass, long bytes)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocation((RuntimeEngine.Rendering.Stats.Vulkan.EVulkanAllocationTelemetryClass)allocationClass, bytes);

    public void RecordRenderVulkanBarrierPlannerPass(int imageBarrierCount, int bufferBarrierCount, int queueOwnershipTransfers, int stageFlushes)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBarrierPlannerPass(imageBarrierCount, bufferBarrierCount, queueOwnershipTransfers, stageFlushes);

    public void RecordRenderVulkanBindChurn(
        int pipelineBinds = 0,
        int descriptorBinds = 0,
        int pushConstantWrites = 0,
        int vertexBufferBinds = 0,
        int indexBufferBinds = 0,
        int pipelineBindSkips = 0,
        int descriptorBindSkips = 0,
        int vertexBufferBindSkips = 0,
        int indexBufferBindSkips = 0)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindChurn(
            pipelineBinds,
            descriptorBinds,
            pushConstantWrites,
            vertexBufferBinds,
            indexBufferBinds,
            pipelineBindSkips,
            descriptorBindSkips,
            vertexBufferBindSkips,
            indexBufferBindSkips);

    public void RecordRenderVulkanDescriptorBindingFailure(
        string? programName,
        string? bindingClass,
        string? bindingName,
        uint set,
        uint binding,
        bool skippedDraw,
        bool skippedDispatch,
        string? message)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorBindingFailure(
            programName,
            bindingClass,
            bindingName,
            set,
            binding,
            skippedDraw,
            skippedDispatch,
            message);

    public void RecordRenderVulkanDescriptorFallback(
        string? programName,
        string? bindingClass,
        string? bindingName,
        uint set,
        uint binding,
        int count = 1)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorFallback(
            programName,
            bindingClass,
            bindingName,
            set,
            binding,
            count);

    public void RecordRenderVulkanDescriptorPoolCreate()
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();

    public void RecordRenderVulkanDescriptorPoolDestroy()
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolDestroy();

    public void RecordRenderVulkanDescriptorPoolReset()
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolReset();

    public void RecordRenderVulkanResourceLifetimeGauges(int liveResourceCount, int trackedDescriptorSetCount, int pendingRetirementCount, long oldestPendingRetirementAgeMilliseconds)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResourceLifetimeGauges(liveResourceCount, trackedDescriptorSetCount, pendingRetirementCount, oldestPendingRetirementAgeMilliseconds);

    public void RecordRenderVulkanMeshFrameDataGauges(int arenaChunkCount, long mappedBytes, long reservedBytes, int reservationCount, ulong generation, int recordingLeases, int cachedLeases, int submittedLeases, int activeGenerationCount, int leaseRetainedGenerationCount)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMeshFrameDataGauges(arenaChunkCount, mappedBytes, reservedBytes, reservationCount, generation, recordingLeases, cachedLeases, submittedLeases, activeGenerationCount, leaseRetainedGenerationCount);

    public void RecordRenderVulkanFrameWideMeshFrameDataManifestGauges(ulong generation, long publicationCount, long lateRegistrationCount, int rendererCount, int familyCount, bool isSealed)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameWideMeshFrameDataManifestGauges(generation, publicationCount, lateRegistrationCount, rendererCount, familyCount, isSealed);

    public void AdjustRenderVulkanMeshDescriptorOwnership(int allocationVariants, int pools, int allocatedSets, int reservedSets)
        => RuntimeEngine.Rendering.Stats.Vulkan.AdjustVulkanMeshDescriptorOwnership(allocationVariants, pools, allocatedSets, reservedSets);

    public void RecordRenderVulkanDynamicUniformAllocation(long bytes)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformAllocation(bytes);

    public void RecordRenderVulkanDynamicUniformExhaustion()
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDynamicUniformExhaustion();

    public void RecordRenderVulkanRecordCommandBufferAllocation(long bytes)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRecordCommandBufferAllocation(bytes);

    public void RecordRenderVulkanFrameDiagnostics(
        int droppedFrameOps,
        int droppedDrawOps,
        int droppedComputeOps,
        int sceneSwapchainWriters,
        int overlaySwapchainWriters,
        int forcedDiagnosticSwapchainWriters,
        int fboOnlyDrawOps,
        int fboOnlyBlitOps,
        bool missingSceneSwapchainWriters,
        string? firstFailedOpType,
        int firstFailedPassIndex,
        int firstFailedPipelineIdentity,
        int firstFailedViewportIdentity,
        string? firstFailedTargetName,
        string? firstFailedMaterialName,
        string? firstFailedShaderName,
        string? firstFailedMessage,
        string? diagnosticSummary)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameDiagnostics(
            droppedFrameOps,
            droppedDrawOps,
            droppedComputeOps,
            sceneSwapchainWriters,
            overlaySwapchainWriters,
            forcedDiagnosticSwapchainWriters,
            fboOnlyDrawOps,
            fboOnlyBlitOps,
            missingSceneSwapchainWriters,
            firstFailedOpType,
            firstFailedPassIndex,
            firstFailedPipelineIdentity,
            firstFailedViewportIdentity,
            firstFailedTargetName,
            firstFailedMaterialName,
            firstFailedShaderName,
            firstFailedMessage,
            diagnosticSummary);

    public void RecordRenderVulkanFrameGpuCommandBufferTime(TimeSpan elapsed)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameGpuCommandBufferTime(elapsed);

    public void RecordRenderVulkanFrameLifecycleTiming(
        TimeSpan waitFence,
        TimeSpan acquireImage,
        TimeSpan recordCommandBuffer,
        TimeSpan submit,
        TimeSpan trim,
        TimeSpan present,
        TimeSpan total)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameLifecycleTiming(
            waitFence,
            acquireImage,
            recordCommandBuffer,
            submit,
            trim,
            present,
            total);

    public void RecordRenderVulkanFrameLifecycleDetailTiming(
        TimeSpan sampleTimingQueries,
        TimeSpan drainRetiredResources,
        TimeSpan acquireBridgeSubmit,
        TimeSpan waitSwapchainImage,
        TimeSpan resetDynamicUniformRing,
        TimeSpan snapshotImGuiOverlay,
        TimeSpan recordSceneCommandBuffer,
        TimeSpan recordImGuiOverlay,
        TimeSpan recordDynamicUiTextOverlay)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameLifecycleDetailTiming(
            sampleTimingQueries,
            drainRetiredResources,
            acquireBridgeSubmit,
            waitSwapchainImage,
            resetDynamicUniformRing,
            snapshotImGuiOverlay,
            recordSceneCommandBuffer,
            recordImGuiOverlay,
            recordDynamicUiTextOverlay);

    public void RecordRenderVulkanFrameOpCensus(
        int totalCount,
        int clearCount,
        int meshDrawCount,
        int indirectDrawCount,
        int meshTaskDispatchCount,
        int blitCount,
        int computeCount,
        int swapchainWriteCount,
        int fboWriteCount,
        int uniquePassCount,
        int uniqueContextCount,
        int uniqueTargetCount)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameOpCensus(
            totalCount,
            clearCount,
            meshDrawCount,
            indirectDrawCount,
            meshTaskDispatchCount,
            blitCount,
            computeCount,
            swapchainWriteCount,
            fboWriteCount,
            uniquePassCount,
            uniqueContextCount,
            uniqueTargetCount);

    public void RecordRenderVulkanCommandBufferCacheOutcome(
        bool reusedClean,
        bool recorded,
        bool forcedDirty,
        bool frameOpSignatureDirty,
        bool plannerDirty,
        bool profilerDirty,
        string? dirtyReason,
        EVulkanCommandBufferDecisionReason detailReasons,
        ulong structuralSignature,
        ulong descriptorGeneration,
        int swapchainSlot)
    {
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
            reusedClean,
            recorded,
            forcedDirty,
            frameOpSignatureDirty,
            plannerDirty,
            profilerDirty,
            dirtyReason,
            detailReasons,
            structuralSignature,
            descriptorGeneration,
            swapchainSlot);
        RuntimeEngine.Rendering.Stats.FrameOutputs.RecordWork(new FrameOutputWorkTelemetry(
            CompiledPlanCacheHits: reusedClean ? 1 : 0,
            CompiledPlanCacheMisses: recorded ? 1 : 0));
    }

    public void RecordRenderVulkanCpuStage(EVulkanCpuStage stage, TimeSpan elapsed, long allocatedBytes)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCpuStage(stage, elapsed, allocatedBytes);

    public void RecordRenderVulkanCommandBuffersDirty(string? reason)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBuffersDirty(reason);

    public void RecordRenderVulkanExactResourceInvalidation(
        int exactVariantsDirtied,
        int exactCommandChainsDirtied,
        int unrelatedVariantsPreserved,
        int globalFallbackInvalidations)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
            exactVariantsDirtied,
            exactCommandChainsDirtied,
            unrelatedVariantsPreserved,
            globalFallbackInvalidations);

    public void RecordRenderVulkanTrackingBatch(
        int dependencyBinds,
        int uniqueDependencies,
        int imageAccessWrites,
        int compactImageRanges)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingBatch(
            dependencyBinds,
            uniqueDependencies,
            imageAccessWrites,
            compactImageRanges);

    public void RecordRenderVulkanDescriptorExpansion(int cacheHits, int cacheMisses)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorExpansion(cacheHits, cacheMisses);

    public void RecordRenderVulkanTrackingContention(int lifetimeLockContentions, int layoutLockContentions)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanTrackingContention(lifetimeLockContentions, layoutLockContentions);

    public void RecordRenderVulkanCommandChainMetrics(
        int chainsScheduled,
        int chainsRecorded,
        int chainsReused,
        int chainsFrameDataRefreshed,
        int volatileChainsRecorded,
        int primaryCommandBuffersReused,
        int primaryCommandBuffersRecorded,
        int visibilityPackets,
        int renderPackets,
        int secondaryCommandBuffers,
        TimeSpan chainWorkerRecordTime,
        TimeSpan renderThreadWaitForWorkersTime,
        string? firstStructuralDirtyReason,
        string? firstDescriptorGenerationMismatch,
        string? firstResourcePlanRevisionMismatch)
    {
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
            chainsScheduled,
            chainsRecorded,
            chainsReused,
            chainsFrameDataRefreshed,
            volatileChainsRecorded,
            primaryCommandBuffersReused,
            primaryCommandBuffersRecorded,
            visibilityPackets,
            renderPackets,
            secondaryCommandBuffers,
            chainWorkerRecordTime,
            renderThreadWaitForWorkersTime,
            firstStructuralDirtyReason,
            firstDescriptorGenerationMismatch,
            firstResourcePlanRevisionMismatch);
        RuntimeEngine.Rendering.Stats.FrameOutputs.RecordWork(new FrameOutputWorkTelemetry(
            SharedPassReuses: chainsReused,
            RecordedWorkItems: chainsRecorded + primaryCommandBuffersRecorded,
            ReusedWorkItems: chainsReused + primaryCommandBuffersReused));
    }

    public void RecordRenderVulkanGpuDrivenStageTiming(int stage, TimeSpan elapsed)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanGpuDrivenStageTiming((RuntimeEngine.Rendering.Stats.Vulkan.EVulkanGpuDrivenStageTiming)stage, elapsed);

    public void RecordRenderVulkanIndirectBatchMerge(int requestedBatchCount, int mergedBatchCount)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectBatchMerge(requestedBatchCount, mergedBatchCount);

    public void RecordRenderVulkanIndirectEffectiveness(uint requestedDraws, uint culledDraws, uint emittedIndirectDraws, uint consumedDraws, uint overflowCount = 0u)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectEffectiveness(requestedDraws, culledDraws, emittedIndirectDraws, consumedDraws, overflowCount);

    public void RecordRenderVulkanIndirectRecordingMode(bool usedSecondary, bool usedParallel, int opCount)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectRecordingMode(usedSecondary, usedParallel, opCount);

    public void RecordRenderVulkanIndirectSubmission(bool usedCountPath, bool usedLoopFallback, int apiCalls, uint submittedDraws)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanIndirectSubmission(usedCountPath, usedLoopFallback, apiCalls, submittedDraws);

    public void RecordRenderVulkanOomFallback()
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();

    public void RecordRenderVulkanPipelineCacheLookup(bool cacheHit)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheLookup(cacheHit);

    public void RecordRenderVulkanPipelineCacheMiss(string? summary)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineCacheMiss(summary);

    public void RecordRenderVulkanPipelineTelemetry(
        EVulkanPipelineTelemetryEvent eventKind,
        EVulkanDriverPipelineCacheOutcome cacheOutcome,
        bool backgroundCompile,
        double compileMilliseconds,
        int queueDepth,
        int queueCapacity)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPipelineTelemetry(
            eventKind,
            cacheOutcome,
            backgroundCompile,
            compileMilliseconds,
            queueDepth,
            queueCapacity);

    public void RecordRenderVulkanQueueOverlapWindow(int overlapCandidatePasses, int transferCost, TimeSpan frameDelta, bool promotedMode, bool demotedMode)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueOverlapWindow(overlapCandidatePasses, transferCost, frameDelta, promotedMode, demotedMode);

    public void RecordRenderVulkanQueueSubmit()
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanQueueSubmit();

    public void RecordRenderVulkanPresentResult(int result, bool accepted)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPresentResult(result, accepted);

    public void RecordRenderVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourcePlanReplacement(imageCount, bufferCount);

    public void RecordRenderVulkanSwapchainRetirement(int queued, int drained, int pending, int deferred)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSwapchainRetirement(queued, drained, pending, deferred);

    public void RecordRenderVulkanRetiredResourceDrain(
        int descriptorPools,
        int descriptorSets,
        int commandBuffers,
        int queryPools,
        int bufferViews,
        int pipelines,
        int framebuffers,
        int buffers,
        int bufferMemories,
        int images,
        int imageViews,
        int samplers,
        int imageMemories,
        long imageBytes)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(
            descriptorPools,
            descriptorSets,
            commandBuffers,
            queryPools,
            bufferViews,
            pipelines,
            framebuffers,
            buffers,
            bufferMemories,
            images,
            imageViews,
            samplers,
            imageMemories,
            imageBytes);

    public void RecordRenderVulkanValidationMessage(bool isError, string? message)
        => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanValidationMessage(isError, message);

    public bool IsWindowScenePanelPresentationEnabled
        => Engine.IsEditor &&
           Engine.PlayMode.IsEditing &&
           Engine.EditorPreferences.Viewport.PresentationMode == EditorPreferences.EViewportPresentationMode.UseViewportPanel;

    public EInteractiveWindowResizeStrategy InteractiveResizeStrategy
        => Engine.EditorPreferences.Viewport.InteractiveResizeStrategy;

    public int ScenePanelResizeDebounceMs
        => Engine.EditorPreferences.Viewport.ScenePanelResizeDebounceMs;

    public bool ForceFullViewport => XREngine.Rendering.RenderDiagnosticsFlags.ForceFullViewport;

    public bool RenderWindowsWhileInVR => RuntimeEngine.Rendering.Settings.RenderWindowsWhileInVR;
    public bool EnableOpenXrVulkanParallelRendering
        => Engine.GameSettings is not IVRGameStartupSettings vrSettings || vrSettings.EnableOpenXrVulkanParallelRendering;
    public bool IsOpenXrRuntimeRequested
        => Engine.StartupOpenXrRuntimeRequested ||
           Engine.GameSettings is IVRGameStartupSettings { VRRuntime: EVRRuntime.OpenXR } ||
           RuntimeEngine.VRState.IsOpenXRActive ||
           RuntimeEngine.VRState.OpenXRApi is not null;
    public EVrViewRenderMode VrViewRenderMode => RuntimeEngine.Rendering.Settings.VrViewRenderMode;
    public EVrMirrorMode VrMirrorMode => RuntimeEngine.Rendering.Settings.VrMirrorMode;
    public float GetVrOutputTargetRateHz(EVrOutputViewKind viewKind)
        => viewKind switch
        {
            EVrOutputViewKind.LeftEye => RuntimeEngine.Rendering.Settings.VrLeftEyeTargetRateHz,
            EVrOutputViewKind.RightEye => RuntimeEngine.Rendering.Settings.VrRightEyeTargetRateHz,
            EVrOutputViewKind.DesktopEditor => RuntimeEngine.Rendering.Settings.VrDesktopEditorTargetRateHz,
            EVrOutputViewKind.CyclopeanDesktop => RuntimeEngine.Rendering.Settings.VrCyclopeanDesktopTargetRateHz,
            _ => 0.0f,
        };
    public bool VrDesktopAutoSkipWhenOverBudget => RuntimeEngine.Rendering.Settings.VrDesktopAutoSkipWhenOverBudget;
    public FrameOutputPacingDecision EvaluateFrameOutputPacing(
        EVrOutputViewKind viewKind,
        EFrameOutputKind outputKind,
        bool xrCritical)
    {
        ulong frameId = Engine.Time.Timer.CollectFrameId;
        float targetRateHz = GetVrOutputTargetRateHz(viewKind);
        bool autoSkipWhenOverBudget = RuntimeEngine.Rendering.Settings.VrDesktopAutoSkipWhenOverBudget;

        if (!xrCritical &&
            RuntimeEngine.VRState.IsInVR &&
            viewKind is EVrOutputViewKind.DesktopEditor or EVrOutputViewKind.CyclopeanDesktop)
        {
            RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot manifest = RuntimeEngine.Rendering.Stats.FrameOutputs.LastManifest;
            EVrMirrorMode mode = RuntimeEngine.Rendering.Settings.VrMirrorMode;
            if (ShouldKeepIndependentDesktopLive(mode))
                autoSkipWhenOverBudget = false;

            if (autoSkipWhenOverBudget && !HasRecentRenderedDesktopOutput(manifest, viewKind))
                autoSkipWhenOverBudget = false;

            if (mode == EVrMirrorMode.Off)
            {
                return RuntimeEngine.Rendering.Stats.FrameOutputs.RecordForcedSkip(
                    viewKind,
                    outputKind,
                    frameId,
                    EFrameOutputSkipReason.MirrorOff,
                    targetRateHz);
            }

            if (mode is EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct)
            {
                return RuntimeEngine.Rendering.Stats.FrameOutputs.RecordForcedSkip(
                    viewKind,
                    outputKind,
                    frameId,
                    EFrameOutputSkipReason.HeldLastImage,
                    targetRateHz);
            }

            if (autoSkipWhenOverBudget && ShouldHoldDesktopOutputForVrPressure(frameId, manifest))
            {
                return RuntimeEngine.Rendering.Stats.FrameOutputs.RecordForcedSkip(
                    viewKind,
                    outputKind,
                    frameId,
                    EFrameOutputSkipReason.Budget,
                    targetRateHz);
            }

            // The host owns VR desktop pressure gating. Passing this through to the
            // generic cadence evaluator lets it immediately re-skip a frame the host
            // intentionally released to prevent black/frozen desktop output.
            autoSkipWhenOverBudget = false;
        }

        return RuntimeEngine.Rendering.Stats.FrameOutputs.EvaluatePacing(
            viewKind,
            outputKind,
            frameId,
            xrCritical,
            targetRateHz,
            autoSkipWhenOverBudget);
    }

    private bool ShouldKeepIndependentDesktopLive(EVrMirrorMode mode)
        => mode == EVrMirrorMode.FullIndependentRender &&
           RuntimeEngine.Rendering.Settings.RenderWindowsWhileInVR;

    private static bool HasRecentRenderedDesktopOutput(
        RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot manifest,
        EVrOutputViewKind viewKind)
    {
        RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot[] outputs = manifest.Outputs;
        for (int i = 0; i < outputs.Length; i++)
        {
            RuntimeEngine.Rendering.Stats.FrameOutputEntrySnapshot output = outputs[i];
            if (output.ViewKind != viewKind || !output.Active)
                continue;

            if ((output.OutputKind == EFrameOutputKind.DesktopScene && output.RenderPhaseSceneRendered) ||
                (output.OutputKind == EFrameOutputKind.DesktopMirror && output.Rendered))
            {
                return true;
            }
        }

        return false;
    }

    private bool ShouldHoldDesktopOutputForVrPressure(
        ulong frameId,
        RuntimeEngine.Rendering.Stats.FrameOutputManifestSnapshot manifest)
    {
        double budgetMs = manifest.BudgetMs > 0.0 ? manifest.BudgetMs : 1000.0 / 90.0;
        double lastWholeFrameMs = manifest.WholeFrameMs;
        if (budgetMs <= 0.0)
            return false;

        lock (_vrDesktopPressureLock)
        {
            if (_vrDesktopPressureFrameId == frameId)
                return _vrDesktopPressureHoldCurrentFrame;

            _vrDesktopPressureFrameId = frameId;
            _vrDesktopPressureHoldCurrentFrame = false;

            if (lastWholeFrameMs > budgetMs)
            {
                int holdFrames = Math.Clamp((int)Math.Ceiling(lastWholeFrameMs / budgetMs), 1, 90);
                _vrDesktopPressureHoldFramesRemaining = Math.Max(_vrDesktopPressureHoldFramesRemaining, holdFrames);
            }

            if (_vrDesktopPressureHoldFramesRemaining <= 0)
            {
                _vrDesktopPressureConsecutiveSkips = 0;
                return false;
            }

            if (_vrDesktopPressureConsecutiveSkips >= MaxConsecutiveVrDesktopBudgetSkips)
            {
                _vrDesktopPressureHoldFramesRemaining = 0;
                _vrDesktopPressureConsecutiveSkips = 0;
                Debug.RenderingEvery(
                    "VR.DesktopPressure.ForceRefreshAfterBudgetSkips",
                    TimeSpan.FromSeconds(1),
                    "[FrameOutput] Forcing VR desktop refresh after {0} consecutive budget skips.",
                    MaxConsecutiveVrDesktopBudgetSkips);
                return false;
            }

            _vrDesktopPressureHoldFramesRemaining--;
            _vrDesktopPressureConsecutiveSkips++;
            _vrDesktopPressureHoldCurrentFrame = true;
            return true;
        }
    }

    public void PlanRenderOutput(in RenderOutputRequest request, bool isDue)
    {
        if (!request.IsDefined)
            return;

        lock (_renderOutputGraphLock)
        {
            bool independentDesktopScene =
                RuntimeEngine.Rendering.Settings.VrMirrorMode == EVrMirrorMode.FullIndependentRender;
            EFrameOutputKind xrSourceKind = RuntimeEngine.VRState.IsOpenXRActive
                ? EFrameOutputKind.OpenXREyeSubmit
                : EFrameOutputKind.OpenVRSubmit;
            _renderOutputGraphPlanner.Plan(
                request,
                isDue,
                independentDesktopScene,
                xrSourceKind);
        }
    }

    public void RecordRenderFrameOutput(in FrameOutputTelemetry telemetry)
    {
        RenderOutputRequest request = telemetry.Request.IsDefined
            ? telemetry.Request
            : telemetry.Pacing.Request;
        FrameOutputTelemetry recorded = telemetry;
        if (request.IsDefined)
        {
            lock (_renderOutputGraphLock)
            {
                bool independentDesktopScene =
                    RuntimeEngine.Rendering.Settings.VrMirrorMode == EVrMirrorMode.FullIndependentRender;
                EFrameOutputKind xrSourceKind = RuntimeEngine.VRState.IsOpenXRActive
                    ? EFrameOutputKind.OpenXREyeSubmit
                    : EFrameOutputKind.OpenVRSubmit;
                _renderOutputGraphPlanner.Plan(
                    request,
                    telemetry.Pacing.IsDue,
                    independentDesktopScene,
                    xrSourceKind);
                if (telemetry.Rendered)
                    _renderOutputGraphPlanner.Complete(request);

                if (_renderOutputGraphPlanner.TryGetStatus(request, out RenderOutputDagNodeStatus status))
                {
                    ERenderOutputWorkDisposition disposition = status.State == ERenderOutputNodeState.Reused
                        ? ERenderOutputWorkDisposition.ReusedStale
                        : telemetry.WorkDisposition;
                    recorded = telemetry with
                    {
                        Request = request,
                        WorkDisposition = disposition,
                        ContentAgeFrames = status.ContentAgeFrames,
                        PolicyAuthorized = status.State != ERenderOutputNodeState.Reused || status.AuthorizedReuse,
                    };
                }
            }
        }

        RuntimeEngine.Rendering.Stats.FrameOutputs.RecordOutput(recorded);
    }
    public void RecordRenderFrameOutputWork(in FrameOutputWorkTelemetry telemetry)
        => RuntimeEngine.Rendering.Stats.FrameOutputs.RecordWork(telemetry);
    public bool EnableVrFoveatedViewSet => RuntimeEngine.Rendering.Settings.EnableVrFoveatedViewSet;
    public ERvcPipelineMode RvcPipelineMode => RuntimeEngine.Rendering.Settings.RvcPipelineMode;
    public bool RvcQuadViewEnabled => RuntimeEngine.Rendering.Settings.RvcQuadViewEnabled;
    public bool RvcOpenXrVisibilityMaskEnabled
        => RuntimeEngine.VRState.IsOpenXRActive &&
           RuntimeEngine.VRState.OpenXRApi?.IsRvcOpenXrVisibilityMaskExtensionEnabled == true;
    public EVrFoveationMode VrFoveationMode => RuntimeEngine.Rendering.Settings.VrFoveationMode;
    public EVrFoveationQualityPreset VrFoveationQualityPreset => RuntimeEngine.Rendering.Settings.VrFoveationQualityPreset;
    public bool VrFoveationRequireRequested => RuntimeEngine.Rendering.Settings.VrFoveationRequireRequested;
    public EOpenXrEyeResolutionPreset OpenXrEyeResolutionPreset => RuntimeEngine.Rendering.Settings.OpenXrEyeResolutionPreset;
    public float OpenXrEyeResolutionScale => RuntimeEngine.Rendering.Settings.OpenXrEyeResolutionScale;
    public uint OpenXrCustomEyeResolutionWidth => RuntimeEngine.Rendering.Settings.OpenXrCustomEyeResolutionWidth;
    public uint OpenXrCustomEyeResolutionHeight => RuntimeEngine.Rendering.Settings.OpenXrCustomEyeResolutionHeight;
    public bool IsInVR => RuntimeEngine.VRState.IsInVR;
    public bool IsOpenXRActive => RuntimeEngine.VRState.IsOpenXRActive;
    public bool VrMirrorComposeFromEyeTextures
        => RuntimeEngine.Rendering.Settings.RenderWindowsWhileInVR &&
           RuntimeEngine.Rendering.Settings.VrMirrorMode is EVrMirrorMode.BlitSubmittedEye or EVrMirrorMode.CyclopeanReconstruct;
    public bool VrCopyEyePreviewTextures => RuntimeEngine.Rendering.Settings.VrCopyEyePreviewTextures;
    public Vector2 VrFoveationCenterUv => RuntimeEngine.Rendering.Settings.VrFoveationCenterUv;
    public float VrFoveationInnerRadius => RuntimeEngine.Rendering.Settings.VrFoveationInnerRadius;
    public float VrFoveationOuterRadius => RuntimeEngine.Rendering.Settings.VrFoveationOuterRadius;
    public Vector3 VrFoveationShadingRates => RuntimeEngine.Rendering.Settings.VrFoveationShadingRates;
    public float VrFoveationVisibilityMargin => RuntimeEngine.Rendering.Settings.VrFoveationVisibilityMargin;
    public bool VrFoveationForceFullResForUiAndNearField => RuntimeEngine.Rendering.Settings.VrFoveationForceFullResForUiAndNearField;
    public float VrFoveationFullResNearDistanceMeters => RuntimeEngine.Rendering.Settings.VrFoveationFullResNearDistanceMeters;
    public bool OpenXrCullWithFrustum => RuntimeEngine.Rendering.Settings.OpenXrCullWithFrustum;
    public bool OpenXrDebugGl => RuntimeEngine.Rendering.Settings.OpenXrDebugGl;
    public bool OpenXrDebugClearOnly => RuntimeEngine.Rendering.Settings.OpenXrDebugClearOnly;
    public bool OpenXrDebugLifecycle => RuntimeEngine.Rendering.Settings.OpenXrDebugLifecycle;
    public bool OpenXrDebugRenderRightThenLeft => RuntimeEngine.Rendering.Settings.OpenXrDebugRenderRightThenLeft;
    public bool OpenXrPrepareFrameAfterDesktopRender => RuntimeEngine.Rendering.Settings.OpenXrPrepareFrameAfterDesktopRender;
    public float OpenXrDeadlineSafetyMarginMs => RuntimeEngine.Rendering.Settings.OpenXrDeadlineSafetyMarginMs;
    public float OpenXrPoseTimeOffsetMs => RuntimeEngine.Rendering.Settings.OpenXrPoseTimeOffsetMs;
    public OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy => RuntimeEngine.Rendering.Settings.OpenXrCollectVisiblePosePolicy;
    public float OpenXrCollectVisibleFrustumPaddingDegrees => RuntimeEngine.Rendering.Settings.OpenXrCollectVisibleFrustumPaddingDegrees;
    public OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy => RuntimeEngine.Rendering.Settings.OpenXrTrackingLossPolicy;
    public OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy => RuntimeEngine.Rendering.Settings.OpenXrActionSyncPolicy;
    public OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode => RuntimeEngine.Rendering.Settings.OpenXrRenderPacingMode;

    public bool TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        => RuntimeEngine.VRState.TryRenderDesktopMirrorComposition(targetWidth, targetHeight);

    public void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
        => RuntimeEngine.Rendering.Stats.Vr.RecordVrPerViewDrawCounts(leftDraws, rightDraws);

    public void DestroyObjectsForRenderer(IRuntimeRendererHost renderer)
    {
        if (renderer is AbstractRenderer abstractRenderer)
            RuntimeRenderObjectServices.Current?.DestroyObjectsForOwner(abstractRenderer);
    }

    public bool IsViewportCurrentlyRendering(IRuntimeViewportHost viewport)
        => viewport is XRViewport xrViewport &&
           (RuntimeEngine.Rendering.State.RenderingPipelineState?.ViewportStack.Contains(xrViewport) ?? false);

    public bool ShouldForceDebugOpaquePipeline => XREngine.Rendering.RenderDiagnosticsFlags.ForceDebugOpaquePipeline;

    public IRuntimeRenderPipelineHost? CreateDebugOpaquePipelineOverride()
        => new DebugOpaqueRenderPipeline();

    public void PrepareUpscaleBridgeForFrame(IRuntimeViewportHost viewport, IRuntimeRenderPipelineFrameContext pipeline)
    {
        if (viewport is XRViewport xrViewport && pipeline is XRRenderPipelineInstance instance)
            RuntimeEngine.Rendering.PrepareVulkanUpscaleBridgeForFrame(xrViewport, instance);
    }

    public void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program)
        => RuntimeEngine.Rendering.ConfigureExactTransparencyMaterialProgram(material, program);

    public int GetBytesPerPixel(ESizedInternalFormat format)
        => RuntimeEngine.Rendering.Stats.PixelFormats.GetBytesPerPixel(format);

    public int GetBytesPerPixel(ERenderBufferStorage storage)
        => RuntimeEngine.Rendering.Stats.PixelFormats.GetBytesPerPixel(storage);

    private static TCapability? GetPrimaryRendererCapability<TCapability>()
        where TCapability : class
    {
        if (AbstractRenderer.Current is IRuntimeRendererHost currentRenderer &&
            currentRenderer.TryGetBackendCapability(out TCapability? currentCapability))
        {
            return currentCapability;
        }

        for (int i = 0; i < RuntimeEngine.Windows.Count; i++)
        {
            IRuntimeRendererHost? renderer = RuntimeEngine.Windows[i].Renderer;
            if (renderer is not null && renderer.TryGetBackendCapability(out TCapability? capability))
                return capability;
        }

        return null;
    }

    public void AddFrameBufferBandwidth(long totalBytes)
        => RuntimeEngine.Rendering.Stats.Vram.AddFBOBandwidth(totalBytes);

    public void DispatchCompute(XRRenderProgram program, uint groupCountX, uint groupCountY, uint groupCountZ)
        => AbstractRenderer.Current?.DispatchCompute(program, (int)groupCountX, (int)groupCountY, (int)groupCountZ);

    public bool TryBlitFrameBufferToFrameBuffer(
        XRFrameBuffer sourceFrameBuffer,
        XRFrameBuffer destinationFrameBuffer,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter)
    {
        if (AbstractRenderer.Current is null)
            return false;

        AbstractRenderer.Current.BlitFBOToFBO(
            sourceFrameBuffer,
            destinationFrameBuffer,
            readBuffer,
            colorBit,
            depthBit,
            stencilBit,
            linearFilter);
        return true;
    }

    public bool TryBlitViewportToFrameBuffer(
        IRuntimeViewportGrabSource viewport,
        XRFrameBuffer framebuffer,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter)
    {
        if (viewport is not XRViewport xrViewport || AbstractRenderer.Current is null)
            return false;

        AbstractRenderer.Current.BlitViewportToFBO(
            xrViewport,
            framebuffer,
            readBuffer,
            colorBit,
            depthBit,
            stencilBit,
            linearFilter);
        return true;
    }

    public RuntimeGraphicsApiKind GetWindowRenderBackend(IRuntimeRenderWindowHost? window)
        => window is XRWindow xrWindow ? GetRendererBackend(xrWindow.Renderer) : RuntimeGraphicsApiKind.Unknown;

    public IEnumerable<IRuntimeViewportHost> EnumerateActiveViewports()
        => RuntimeEngine.EnumerateActiveViewports();

    public IEnumerable<IPawnController> EnumerateLocalPlayers()
        => Engine.State.LocalPlayers.OfType<IPawnController>();

    // The RuntimeEngine facade resolves through this installed factory.
    // Calling it here would re-enter this adapter.
    public XRCamera.EDepthMode ResolveSceneCameraDepthModePreference()
        => EngineRenderingSettingsApplication.ResolveSceneCameraDepthModePreference();

    public IRuntimeInputControllablePawn? EnsurePawnForCamera(SceneNode sceneNode, CameraComponent camera, ELocalPlayerIndex playerIndex, Type? pawnType = null)
    {
        PawnComponent? pawn = null;

        if (pawnType is null)
        {
            sceneNode.TryGetComponent<PawnComponent>(out pawn);
            pawn ??= sceneNode.AddComponent<PawnComponent>();
        }
        else if (typeof(PawnComponent).IsAssignableFrom(pawnType))
        {
            foreach (var component in sceneNode.Components)
            {
                if (pawnType.IsInstanceOfType(component) && component is PawnComponent existing)
                {
                    pawn = existing;
                    break;
                }
            }

            pawn ??= sceneNode.AddComponent(pawnType) as PawnComponent;
        }

        if (pawn is null)
            return null;

        pawn.CameraComponent = camera;
        pawn.EnqueuePossessionByLocalPlayer(playerIndex);
        return pawn;
    }

    public void PickViewportPhysicsAsync(
        XRViewport viewport,
        CameraComponent camera,
        Vector2 normalizedViewportPosition,
        LayerMask layerMask,
        object? filter,
        SortedDictionary<float, List<(XRComponent? item, object? data)>> orderedPhysicsResults,
        Action<SortedDictionary<float, List<(XRComponent? item, object? data)>>?> physicsFinishedCallback,
        bool useUnjitteredProjection)
    {
        if (viewport.World is XRWorldInstance world)
        {
            world.RaycastPhysicsAsync(
                camera,
                normalizedViewportPosition,
                layerMask,
                filter as AbstractPhysicsScene.IAbstractQueryFilter,
                orderedPhysicsResults,
                physicsFinishedCallback,
                useUnjitteredProjection);
        }
    }

    private static RuntimeGraphicsApiKind GetRendererBackend(object? renderer)
    {
        if (renderer is not IRuntimeRendererHost runtimeRenderer)
            return RuntimeGraphicsApiKind.Unknown;

        if (runtimeRenderer.BackendId == RendererBackendId.Vulkan)
            return RuntimeGraphicsApiKind.Vulkan;

        return runtimeRenderer.BackendId == RendererBackendId.OpenGL
            ? RuntimeGraphicsApiKind.OpenGL
            : RuntimeGraphicsApiKind.Unknown;
    }
}
