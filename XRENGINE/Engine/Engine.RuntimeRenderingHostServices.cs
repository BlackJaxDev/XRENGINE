using System.Collections;
using System.IO;
using System.Numerics;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms.Rotations;
using XREngine.Diagnostics;
using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Compute;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;
using XREngine.Scene.Physics;

namespace XREngine;

internal sealed class EngineRuntimeRenderingHostServices : IRuntimeRenderingHostServices
{
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
        return Engine.Profiler.Start(string.IsNullOrWhiteSpace(scopeName) ? "<unnamed>" : scopeName);
    }

    public bool AllowShaderPipelines => Engine.Rendering.Settings.AllowShaderPipelines;
    public bool EnableExactTransparencyTechniques => Engine.EditorPreferences.Debug.EnableExactTransparencyTechniques;
    public bool UseInterleavedMeshBuffer => Engine.Rendering.Settings.UseInterleavedMeshBuffer;
    public bool UseIntegerUniformsInShaders => Engine.Rendering.Settings.UseIntegerUniformsInShaders;
    public bool RemapBlendshapeDeltas => Engine.Rendering.Settings.RemapBlendshapeDeltas;
    public bool AllowBlendshapes => Engine.Rendering.Settings.AllowBlendshapes;
    public bool PopulateVertexDataInParallel => Engine.Rendering.Settings.PopulateVertexDataInParallel;
    public bool ProcessMeshImportsAsynchronously => Engine.Rendering.Settings.ProcessMeshImportsAsynchronously;
    public bool AllowSkinning => Engine.Rendering.Settings.AllowSkinning;
    public bool OptimizeSkinningTo4Weights => Engine.Rendering.Settings.OptimizeSkinningTo4Weights;
    public bool OptimizeSkinningWeightsIfPossible => Engine.Rendering.Settings.OptimizeSkinningWeightsIfPossible;
    public bool CalculateSkinningInComputeShader => Engine.Rendering.Settings.CalculateSkinningInComputeShader;
    public bool CalculateBlendshapesInComputeShader => Engine.Rendering.Settings.CalculateBlendshapesInComputeShader;
    public int ShaderConfigVersion => Engine.Rendering.Settings.ShaderConfigVersion;

    public void SubscribeRenderingSettingsChanged(Action callback)
        => Engine.Rendering.SettingsChanged += callback;

    public void UnsubscribeRenderingSettingsChanged(Action callback)
        => Engine.Rendering.SettingsChanged -= callback;

    public void SubscribeAntiAliasingSettingsChanged(Action callback)
        => Engine.Rendering.AntiAliasingSettingsChanged += callback;

    public void UnsubscribeAntiAliasingSettingsChanged(Action callback)
        => Engine.Rendering.AntiAliasingSettingsChanged -= callback;

    public bool IsRenderThread => Engine.IsRenderThread;
    public bool IsRendererActive => AbstractRenderer.Current?.Active ?? false;
    public bool IsShadowPass => Engine.Rendering.State.IsShadowPass;
    public bool IsStereoPass => Engine.Rendering.State.IsStereoPass;
    public bool IsSceneCapturePass => Engine.Rendering.State.IsSceneCapturePass;
    public bool RenderCullingVolumesEnabled => Engine.EditorPreferences.Debug.RenderCullingVolumes;
    public bool IsNvidia => Engine.Rendering.State.IsNVIDIA;
    public string AssetFileExtension => AssetManager.AssetExtension;
    public string? TextureFallbackPath => Path.Combine(Engine.GameSettings.TexturesFolder, "Filler.png");
    public XRMaterial? InvalidMaterial => Engine.Rendering.State.CurrentRenderingPipeline?.InvalidMaterial;
    public Vector3 DefaultLuminance => Engine.Rendering.Settings.DefaultLuminance;
    public long ElapsedTicks => Engine.ElapsedTicks;
    public float ElapsedTime => Engine.ElapsedTime;
    public double UpdateDeltaSeconds => Engine.Time.Timer.Update.Delta;
    public long LastUpdateTimestampTicks => Engine.Time.Timer.Update.LastTimestampTicks;
    public double RenderDeltaSeconds => Engine.Time.Timer.Render.Delta;
    public long LastRenderTimestampTicks => Engine.Time.Timer.Render.LastTimestampTicks;
    public long TrackedVramBytes => Engine.Rendering.Stats.AllocatedVRAMBytes;
    public long TrackedVramBudgetBytes => Engine.Rendering.Stats.VramBudgetBytes;
    public bool EnableGpuIndirectDebugLogging => Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;
    public EOcclusionCullingMode GpuOcclusionCullingMode => Engine.EffectiveSettings.GpuOcclusionCullingMode;
    public int CpuQueryOcclusionRetestPeriodFrames => Engine.Rendering.Settings.CpuQueryOcclusionRetestPeriodFrames;
    public bool EnableCpuSoftwareOcclusionCulling => Engine.EffectiveSettings.EnableCpuSoftwareOcclusionCulling;
    public int CpuSocBufferWidth => Engine.EffectiveSettings.CpuSocBufferWidth;
    public int CpuSocBufferHeight => Engine.EffectiveSettings.CpuSocBufferHeight;
    public int CpuSocOccluderTriangleBudget => Engine.EffectiveSettings.CpuSocOccluderTriangleBudget;
    public int CpuSocMaxOccluders => Engine.EffectiveSettings.CpuSocMaxOccluders;
    public float CpuSocMinOccluderScreenArea => Engine.EffectiveSettings.CpuSocMinOccluderScreenArea;
    public bool CpuSocUseAvx2 => Engine.EffectiveSettings.CpuSocUseAvx2;
    public bool CpuSocDebugVisualization => Engine.EffectiveSettings.CpuSocDebugVisualization;
    public bool CpuSocDebugForceVisible => Engine.EffectiveSettings.CpuSocDebugForceVisible;
    public TextureRuntimeLogMode TextureLogMode => Engine.Rendering.Settings.TextureLogMode;
    public double TextureSlowCpuDecodeResizeMilliseconds => Engine.Rendering.Settings.TextureSlowCpuDecodeResizeMilliseconds;
    public double TextureSlowMipBuildMilliseconds => Engine.Rendering.Settings.TextureSlowMipBuildMilliseconds;
    public double TextureSlowUploadChunkMilliseconds => Engine.Rendering.Settings.TextureSlowUploadChunkMilliseconds;
    public double TextureSlowTransitionMilliseconds => Engine.Rendering.Settings.TextureSlowTransitionMilliseconds;
    public double TextureSlowQueueWaitMilliseconds => Engine.Rendering.Settings.TextureSlowQueueWaitMilliseconds;
    public double TextureUploadFrameBudgetMilliseconds => Engine.Rendering.Settings.TextureUploadFrameBudgetMilliseconds;
    public ETwoPlayerPreference TwoPlayerViewportPreference => Engine.GameSettings.TwoPlayerViewportPreference;
    public EThreePlayerPreference ThreePlayerViewportPreference => Engine.GameSettings.ThreePlayerViewportPreference;
    public RuntimeGraphicsApiKind CurrentRenderBackend
    {
        get
        {
            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (renderer is null)
                renderer = Engine.Windows.FirstOrDefault()?.Renderer;

            return GetRendererBackend(renderer);
        }
    }

    public IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState
        => Engine.Rendering.State.CurrentRenderingPipeline?.RenderState;

    public IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext
        => Engine.Rendering.State.CurrentRenderingPipeline;

    public bool IsPlayModeTransitioning => Engine.PlayMode.IsTransitioning;
    public string PlayModeStateName => Engine.PlayMode.State.ToString();
    public EAntiAliasingMode DefaultAntiAliasingMode => Engine.EffectiveSettings.AntiAliasingMode;
    public uint DefaultMsaaSampleCount => Engine.EffectiveSettings.MsaaSampleCount;
    public bool DefaultOutputHDR => Engine.Rendering.Settings.OutputHDR;
    public float DefaultTsrRenderScale => Engine.Rendering.Settings.TsrRenderScale;
    public bool ForwardDepthPrePassEnabled => Engine.EditorPreferences.Debug.ForwardDepthPrePassEnabled;
    public bool ForwardPrePassSharesGBufferTargets => Engine.EditorPreferences.Debug.ForwardPrePassSharesGBufferTargets;
    public bool EnableRenderStatisticsTracking => Engine.Rendering.Stats.EnableTracking;
    public bool EnableGpuRenderPipelineProfiling => Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;
    public ulong CurrentRenderFrameId => Engine.Rendering.State.RenderFrameId;
    public bool ProvidesShadowAtlasSettings => true;
    public bool UseSpotShadowAtlas => Engine.Rendering.Settings.UseSpotShadowAtlas;
    public bool UseDirectionalShadowAtlas => Engine.Rendering.Settings.UseDirectionalShadowAtlas;
    public bool UsePointShadowAtlas => Engine.Rendering.Settings.UsePointShadowAtlas;
    public uint ShadowAtlasPageSize => Engine.Rendering.Settings.ShadowAtlasPageSize;
    public int MaxShadowAtlasPages => Engine.Rendering.Settings.MaxShadowAtlasPages;
    public long MaxShadowAtlasMemoryBytes => Engine.Rendering.Settings.MaxShadowAtlasMemoryBytes;
    public int MaxShadowTilesRenderedPerFrame => Engine.Rendering.Settings.MaxShadowTilesRenderedPerFrame;
    public float MaxShadowRenderMilliseconds => Engine.Rendering.Settings.MaxShadowRenderMilliseconds;
    public uint MinShadowAtlasTileResolution => Engine.Rendering.Settings.MinShadowAtlasTileResolution;
    public uint MaxShadowAtlasTileResolution => Engine.Rendering.Settings.MaxShadowAtlasTileResolution;

    public void LogOutput(string message)
        => Debug.Out(message);

    public IDisposable? PushRenderingPipeline(IRuntimeRenderPipelineFrameContext pipeline)
        => pipeline is XRRenderPipelineInstance instance
            ? Engine.Rendering.State.PushRenderingPipeline(instance)
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
        => AbstractRenderer.Current is OpenGLRenderer glRenderer
            ? glRenderer.GetSparseTextureStreamingSupport(format)
            : SparseTextureStreamingSupport.Unsupported("Sparse texture streaming currently requires the OpenGL renderer.");

    public bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null)
    {
        OpenGLRenderer? glRenderer = GetPrimaryOpenGlRenderer();
        if (glRenderer is null)
            return false;

        string textureName = string.IsNullOrWhiteSpace(texture.Name) ? "UnnamedTexture" : texture.Name;
        Engine.EnqueueRenderThreadTask(() =>
        {
            try
            {
                OpenGLRenderer? renderThreadRenderer = GetPrimaryOpenGlRenderer();
                if (renderThreadRenderer is null)
                {
                    onCompleted(SparseTextureStreamingTransitionResult.Unsupported("OpenGL renderer is unavailable for sparse texture streaming."));
                    return;
                }

                GLTexture2D? glTexture = renderThreadRenderer.GetOrCreateAPIRenderObject(texture, generateNow: false) as GLTexture2D;
                if (glTexture is null)
                {
                    onCompleted(SparseTextureStreamingTransitionResult.Unsupported("OpenGL texture wrapper is unavailable for sparse texture streaming."));
                    return;
                }

                void CompleteAsyncTransition(SparseTextureStreamingTransitionResult result)
                    => Engine.EnqueueRenderThreadTask(
                        () => onCompleted(result),
                        $"XRTexture2D.CompleteSparseTransition[{textureName}]");

                void ReportAsyncTransitionError(Exception ex)
                    => Engine.EnqueueRenderThreadTask(
                        () => onError?.Invoke(ex),
                        $"XRTexture2D.FailSparseTransition[{textureName}]");

                if (!glTexture.TryScheduleSparseTextureStreamingTransitionAsync(request, cancellationToken, CompleteAsyncTransition, ReportAsyncTransitionError))
                    onCompleted(texture.ApplySparseTextureStreamingTransition(request));
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }, $"XRTexture2D.ScheduleSparseTransition[{textureName}]");

        return true;
    }

    public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult)
    {
        OpenGLRenderer? glRenderer = GetPrimaryOpenGlRenderer();
        if (glRenderer is null)
            return SparseTextureStreamingFinalizeResult.Failed("OpenGL renderer is unavailable for sparse texture finalization.");

        GLTexture2D? glTexture = glRenderer.GetOrCreateAPIRenderObject(texture, generateNow: false) as GLTexture2D;
        if (glTexture is null)
            return SparseTextureStreamingFinalizeResult.Failed("OpenGL texture wrapper is unavailable for sparse texture finalization.");

        return glTexture.FinalizeSparseTextureStreamingTransition(request, transitionResult);
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

    public void EnqueueRenderThreadTask(Action task, string reason)
        => Engine.EnqueueRenderThreadTask(task, reason);

    public void EnqueueAppThreadTask(Action task)
        => Engine.EnqueueAppThreadTask(task);

    public void EnqueueAppThreadTask(Action task, string reason)
        => Engine.EnqueueAppThreadTask(task, reason);

    public void EnqueueRenderThreadCoroutine(Func<bool> task)
        => Engine.AddRenderThreadCoroutine(task);

    public void EnqueueRenderThreadCoroutine(Func<bool> task, string reason)
        => Engine.AddRenderThreadCoroutine(task, reason);

    public void ProcessRenderThreadTasks()
        => Engine.ProcessMainThreadTasks();

    public IDisposable? PushTransformId(uint transformId)
        => Engine.Rendering.State.PushTransformId(transformId);

    public void RecordOctreeSkippedMove()
        => Engine.Rendering.Stats.RecordOctreeSkippedMove();

    public void ProcessGpuPhysicsChainDispatches()
        => GPUPhysicsChainDispatcher.Instance.ProcessDispatches();

    public void ProcessGpuPhysicsChainCompletions()
        => GPUPhysicsChainDispatcher.Instance.ProcessCompletions();

    public void RenderDebugRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderRect2D(rectangle, solid, color);

    public void RenderDebugLine(Vector3 start, Vector3 end, ColorF4 color)
        => Engine.Rendering.Debug.RenderLine(start, end, color);

    public void RenderDebugSphere(Vector3 center, float radius, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderSphere(center, radius, solid, color);

    public void RenderDebugCone(Vector3 center, Vector3 up, float radius, float height, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderCone(center, up, radius, height, solid, color);

    public void RenderDebugAABB(Vector3 halfExtents, Vector3 center, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderAABB(halfExtents, center, solid, color);

    public void RenderDebugBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderBox(halfExtents, center, transform, solid, color);

    public void RenderDebugQuad(Vector3 center, Rotator rotation, Vector2 extents, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderQuad(center, rotation, extents, solid, color);

    public void RenderDebugPoint(Vector3 position, ColorF4 color)
        => Engine.Rendering.Debug.RenderPoint(position, color);

    public void RenderDebugText(Vector3 position, string text, ColorF4 color)
        => Engine.Rendering.Debug.RenderText(position, text, color);

    public void RenderDebugShapes()
        => Engine.Rendering.Debug.RenderShapes();

    public TAsset? LoadAsset<TAsset>(string filePath) where TAsset : XRAsset, new()
        => Engine.Assets?.Load<TAsset>(filePath);

    public IRuntimeRenderPipelineHost? CreateDefaultRenderPipeline()
        => Engine.Rendering.NewRenderPipeline();

    public IRuntimeRendererHost CreateRenderer(IRuntimeRenderWindowHost window, RuntimeGraphicsApiKind apiKind)
    {
        XRWindow xrWindow = (XRWindow)window;
        return apiKind switch
        {
            RuntimeGraphicsApiKind.OpenGL => new OpenGLRenderer(xrWindow, true),
            RuntimeGraphicsApiKind.Vulkan => new VulkanRenderer(xrWindow, true),
            _ => throw new InvalidOperationException($"Unsupported graphics API: {apiKind}"),
        };
    }

    public IRuntimeWindowScenePanelAdapter CreateWindowScenePanelAdapter()
        => new XRWindowScenePanelAdapter();

    public BoundingRectangle? GetScenePanelRenderRegion(IRuntimeRenderWindowHost window)
        => window is XRWindow xrWindow
            ? Engine.Rendering.ScenePanelRenderRegionProvider?.Invoke(xrWindow)
            : null;

    public bool AllowWindowClose(IRuntimeRenderWindowHost window)
    {
        if (Engine.WindowCloseRequested is null)
            return true;

        XRWindow xrWindow = (XRWindow)window;
        return Engine.WindowCloseRequested.Invoke(xrWindow) == Engine.WindowCloseRequestResult.Allow;
    }

    public void RemoveWindow(IRuntimeRenderWindowHost window)
    {
        if (window is XRWindow xrWindow)
            Engine.RemoveWindow(xrWindow);
    }

    public void ReplicateWindowTargetWorldChange(IRuntimeRenderWindowHost window)
    {
        if (window is not XRWindow xrWindow || (Engine.Networking?.IsClient ?? false))
            return;

        string? encoded = xrWindow.EncodeTargetWorldHierarchyJson();
        Engine.Networking?.ReplicateStateChange(
            new StateChangeInfo(
                EStateChangeType.WorldChange,
                encoded is null ? "null" : encoded),
            true,
            true);
    }

    public void BeginRenderStatsFrame()
        => Engine.Rendering.Stats.BeginFrame();

    public void IncrementRenderDrawCalls(int count)
        => Engine.Rendering.Stats.IncrementDrawCalls(count);

    public void IncrementRenderMultiDrawCalls(int count)
        => Engine.Rendering.Stats.IncrementMultiDrawCalls(count);

    public void AddRenderTrianglesRendered(int count)
        => Engine.Rendering.Stats.AddTrianglesRendered(count);

    public void AddRenderGpuBufferAllocation(long bytes)
        => Engine.Rendering.Stats.AddBufferAllocation(bytes);

    public void RemoveRenderGpuBufferAllocation(long bytes)
        => Engine.Rendering.Stats.RemoveBufferAllocation(bytes);

    public void AddRenderGpuTextureAllocation(long bytes)
        => Engine.Rendering.Stats.AddTextureAllocation(bytes);

    public void RemoveRenderGpuTextureAllocation(long bytes)
        => Engine.Rendering.Stats.RemoveTextureAllocation(bytes);

    public void AddRenderGpuRenderBufferAllocation(long bytes)
        => Engine.Rendering.Stats.AddRenderBufferAllocation(bytes);

    public void RemoveRenderGpuRenderBufferAllocation(long bytes)
        => Engine.Rendering.Stats.RemoveRenderBufferAllocation(bytes);

    public bool CanAllocateRenderVram(long requestedBytes, long existingAllocationBytes, out long projectedBytes, out long budgetBytes)
        => Engine.Rendering.Stats.CanAllocateVram(requestedBytes, existingAllocationBytes, out projectedBytes, out budgetBytes);

    public void RecordRenderGpuBufferMapped(int count = 1)
        => Engine.Rendering.Stats.RecordGpuBufferMapped(count);

    public void RecordRenderGpuReadbackBytes(long bytes)
        => Engine.Rendering.Stats.RecordGpuReadbackBytes(bytes);

    public void RecordRenderGpuCpuFallback(int eventCount, int recoveredCommands)
        => Engine.Rendering.Stats.RecordGpuCpuFallback(eventCount, recoveredCommands);

    public void RecordRenderForbiddenGpuFallback(int eventCount = 1)
        => Engine.Rendering.Stats.RecordForbiddenGpuFallback(eventCount);

    public void RecordRenderGpuTransparencyDomainCounts(uint opaqueOrOtherVisible, uint maskedVisible, uint approximateVisible, uint exactVisible)
        => Engine.Rendering.Stats.RecordGpuTransparencyDomainCounts(opaqueOrOtherVisible, maskedVisible, approximateVisible, exactVisible);

    public void RecordRenderGpuMeshletStrategyRequested(int eventCount = 1)
        => Engine.Rendering.Stats.RecordGpuMeshletStrategyRequested(eventCount);

    public void RecordRenderGpuMeshletProductionFrame(int eventCount = 1)
        => Engine.Rendering.Stats.RecordGpuMeshletProductionFrame(eventCount);

    public void RecordRenderGpuMeshletFallback(int eventCount = 1)
        => Engine.Rendering.Stats.RecordGpuMeshletFallback(eventCount);

    public void RecordRenderGpuMeshletDispatchSkipped(int eventCount = 1)
        => Engine.Rendering.Stats.RecordGpuMeshletDispatchSkipped(eventCount);

    public void RecordRenderGpuMeshletTaskStats(uint emitted, uint frustumCulled, uint coneCulled, uint hiZCulled)
        => Engine.Rendering.Stats.RecordGpuMeshletTaskStats(emitted, frustumCulled, coneCulled, hiZCulled);

    public void RecordRenderGpuMeshletExpansionOverflow(uint overflowCount)
        => Engine.Rendering.Stats.RecordGpuMeshletExpansionOverflow(overflowCount);

    public void RecordRenderGpuMeshletBufferBytesResident(long bytes)
        => Engine.Rendering.Stats.RecordGpuMeshletBufferBytesResident(bytes < 0 ? 0UL : (ulong)bytes);

    public void RecordRenderGpuMeshletCacheHit(int eventCount = 1)
        => Engine.Rendering.Stats.RecordGpuMeshletCacheHit(eventCount);

    public void RecordRenderGpuMeshletCacheMiss(int eventCount = 1)
        => Engine.Rendering.Stats.RecordGpuMeshletCacheMiss(eventCount);

    public void RecordRenderGpuMeshletCacheStale(int eventCount = 1)
        => Engine.Rendering.Stats.RecordGpuMeshletCacheStale(eventCount);

    public void RecordRenderOctreeCollect(int visibleRenderables, int emittedCommands)
        => Engine.Rendering.Stats.RecordOctreeCollect(visibleRenderables, emittedCommands);

    public void RecordRenderRtxIoCopyIndirect(long copiedBytes, TimeSpan submissionTime)
        => Engine.Rendering.Stats.RecordRtxIoCopyIndirect(copiedBytes, submissionTime);

    public void RecordRenderRtxIoDecompression(long compressedBytes, long decompressedBytes, TimeSpan submissionTime)
        => Engine.Rendering.Stats.RecordRtxIoDecompression(compressedBytes, decompressedBytes, submissionTime);

    public void RecordRenderSkinnedBoundsRefreshDeferredFinished(long queueWaitTicks, long cpuJobTicks, long applyTicks, bool succeeded)
        => Engine.Rendering.Stats.RecordSkinnedBoundsRefreshDeferredFinished(queueWaitTicks, cpuJobTicks, applyTicks, succeeded);

    public void RecordRenderSkinnedBoundsRefreshDeferredScheduled()
        => Engine.Rendering.Stats.RecordSkinnedBoundsRefreshDeferredScheduled();

    public void RecordRenderSkinnedBoundsRefreshGpuCompleted(long computeTicks, long applyTicks)
        => Engine.Rendering.Stats.RecordSkinnedBoundsRefreshGpuCompleted(computeTicks, applyTicks);

    public void RecordRenderVrCommandBuildTimes(TimeSpan leftBuildTime, TimeSpan rightBuildTime)
        => Engine.Rendering.Stats.RecordVrCommandBuildTimes(leftBuildTime, rightBuildTime);

    public void RecordRenderVrPerViewVisibleCounts(uint leftVisible, uint rightVisible)
        => Engine.Rendering.Stats.RecordVrPerViewVisibleCounts(leftVisible, rightVisible);

    public void RecordRenderVrRenderSubmitTime(TimeSpan submitTime)
        => Engine.Rendering.Stats.RecordVrRenderSubmitTime(submitTime);

    public void RecordRenderVrXrWaitFrameBlockTime(TimeSpan waitTime)
        => Engine.Rendering.Stats.RecordVrXrWaitFrameBlockTime(waitTime);

    public void RecordRenderVrXrEndFrameSubmitTime(TimeSpan submitTime)
        => Engine.Rendering.Stats.RecordVrXrEndFrameSubmitTime(submitTime);

    public void RecordRenderVrXrPredictedToLatePoseDelta(double millimeters, double degrees)
        => Engine.Rendering.Stats.RecordVrXrPredictedToLatePoseDelta(millimeters, degrees);

    public void RecordRenderVrXrPredictedDisplayLeadTime(double leadTimeMs)
        => Engine.Rendering.Stats.RecordVrXrPredictedDisplayLeadTime(leadTimeMs);

    public void RecordRenderVrXrMissedDeadlineFrame()
        => Engine.Rendering.Stats.RecordVrXrMissedDeadlineFrame();

    public void RecordRenderVrXrTrackingLossFrame()
        => Engine.Rendering.Stats.RecordVrXrTrackingLossFrame();

    public void RecordRenderVrXrRelocatePredictedTime(TimeSpan elapsed)
        => Engine.Rendering.Stats.RecordVrXrRelocatePredictedTime(elapsed);

    public void RecordRenderVrXrCollectFrustumExpansionDegrees(double degrees)
        => Engine.Rendering.Stats.RecordVrXrCollectFrustumExpansionDegrees(degrees);

    public void RecordRenderVrXrPacingThreadIdleTime(TimeSpan elapsed)
        => Engine.Rendering.Stats.RecordVrXrPacingThreadIdleTime(elapsed);

    public void RecordRenderVrXrPacingHandoffStall()
        => Engine.Rendering.Stats.RecordVrXrPacingHandoffStall();

    public void RecordRenderVulkanAdhocBarrier(int emittedCount, int redundantCount)
        => Engine.Rendering.Stats.RecordVulkanAdhocBarrier(emittedCount, redundantCount);

    public void RecordRenderVulkanAllocation(int allocationClass, long bytes)
        => Engine.Rendering.Stats.RecordVulkanAllocation((Engine.Rendering.Stats.EVulkanAllocationTelemetryClass)allocationClass, bytes);

    public void RecordRenderVulkanBarrierPlannerPass(int imageBarrierCount, int bufferBarrierCount, int queueOwnershipTransfers, int stageFlushes)
        => Engine.Rendering.Stats.RecordVulkanBarrierPlannerPass(imageBarrierCount, bufferBarrierCount, queueOwnershipTransfers, stageFlushes);

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
        => Engine.Rendering.Stats.RecordVulkanBindChurn(
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
        => Engine.Rendering.Stats.RecordVulkanDescriptorBindingFailure(
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
        => Engine.Rendering.Stats.RecordVulkanDescriptorFallback(
            programName,
            bindingClass,
            bindingName,
            set,
            binding,
            count);

    public void RecordRenderVulkanDescriptorPoolCreate()
        => Engine.Rendering.Stats.RecordVulkanDescriptorPoolCreate();

    public void RecordRenderVulkanDescriptorPoolDestroy()
        => Engine.Rendering.Stats.RecordVulkanDescriptorPoolDestroy();

    public void RecordRenderVulkanDescriptorPoolReset()
        => Engine.Rendering.Stats.RecordVulkanDescriptorPoolReset();

    public void RecordRenderVulkanDynamicUniformAllocation(long bytes)
        => Engine.Rendering.Stats.RecordVulkanDynamicUniformAllocation(bytes);

    public void RecordRenderVulkanDynamicUniformExhaustion()
        => Engine.Rendering.Stats.RecordVulkanDynamicUniformExhaustion();

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
        => Engine.Rendering.Stats.RecordVulkanFrameDiagnostics(
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
        => Engine.Rendering.Stats.RecordVulkanFrameGpuCommandBufferTime(elapsed);

    public void RecordRenderVulkanFrameLifecycleTiming(
        TimeSpan waitFence,
        TimeSpan acquireImage,
        TimeSpan recordCommandBuffer,
        TimeSpan submit,
        TimeSpan trim,
        TimeSpan present,
        TimeSpan total)
        => Engine.Rendering.Stats.RecordVulkanFrameLifecycleTiming(
            waitFence,
            acquireImage,
            recordCommandBuffer,
            submit,
            trim,
            present,
            total);

    public void RecordRenderVulkanGpuDrivenStageTiming(int stage, TimeSpan elapsed)
        => Engine.Rendering.Stats.RecordVulkanGpuDrivenStageTiming((Engine.Rendering.Stats.EVulkanGpuDrivenStageTiming)stage, elapsed);

    public void RecordRenderVulkanIndirectBatchMerge(int requestedBatchCount, int mergedBatchCount)
        => Engine.Rendering.Stats.RecordVulkanIndirectBatchMerge(requestedBatchCount, mergedBatchCount);

    public void RecordRenderVulkanIndirectEffectiveness(uint requestedDraws, uint culledDraws, uint emittedIndirectDraws, uint consumedDraws, uint overflowCount = 0u)
        => Engine.Rendering.Stats.RecordVulkanIndirectEffectiveness(requestedDraws, culledDraws, emittedIndirectDraws, consumedDraws, overflowCount);

    public void RecordRenderVulkanIndirectRecordingMode(bool usedSecondary, bool usedParallel, int opCount)
        => Engine.Rendering.Stats.RecordVulkanIndirectRecordingMode(usedSecondary, usedParallel, opCount);

    public void RecordRenderVulkanIndirectSubmission(bool usedCountPath, bool usedLoopFallback, int apiCalls, uint submittedDraws)
        => Engine.Rendering.Stats.RecordVulkanIndirectSubmission(usedCountPath, usedLoopFallback, apiCalls, submittedDraws);

    public void RecordRenderVulkanOomFallback()
        => Engine.Rendering.Stats.RecordVulkanOomFallback();

    public void RecordRenderVulkanPipelineCacheLookup(bool cacheHit)
        => Engine.Rendering.Stats.RecordVulkanPipelineCacheLookup(cacheHit);

    public void RecordRenderVulkanPipelineCacheMiss(string? summary)
        => Engine.Rendering.Stats.RecordVulkanPipelineCacheMiss(summary);

    public void RecordRenderVulkanQueueOverlapWindow(int overlapCandidatePasses, int transferCost, TimeSpan frameDelta, bool promotedMode, bool demotedMode)
        => Engine.Rendering.Stats.RecordVulkanQueueOverlapWindow(overlapCandidatePasses, transferCost, frameDelta, promotedMode, demotedMode);

    public void RecordRenderVulkanQueueSubmit()
        => Engine.Rendering.Stats.RecordVulkanQueueSubmit();

    public void RecordRenderVulkanRetiredResourcePlanReplacement(int imageCount, int bufferCount)
        => Engine.Rendering.Stats.RecordVulkanRetiredResourcePlanReplacement(imageCount, bufferCount);

    public void RecordRenderVulkanValidationMessage(bool isError, string? message)
        => Engine.Rendering.Stats.RecordVulkanValidationMessage(isError, message);

    public bool IsWindowScenePanelPresentationEnabled
        => Engine.IsEditor &&
           Engine.EditorPreferences.ViewportPresentationMode == EditorPreferences.EViewportPresentationMode.UseViewportPanel;

    public int ScenePanelResizeDebounceMs
        => Engine.EditorPreferences.ScenePanelResizeDebounceMs;

    public bool ForceFullViewport
        => string.Equals(
            Environment.GetEnvironmentVariable("XRE_FORCE_FULL_VIEWPORT"),
            "1",
            StringComparison.Ordinal);

    public bool RenderWindowsWhileInVR => Engine.Rendering.Settings.RenderWindowsWhileInVR;
    public bool EnableVrFoveatedViewSet => Engine.Rendering.Settings.EnableVrFoveatedViewSet;
    public bool IsInVR => Engine.VRState.IsInVR;
    public bool IsOpenXRActive => Engine.VRState.IsOpenXRActive;
    public bool VrMirrorComposeFromEyeTextures => Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures;
    public Vector2 VrFoveationCenterUv => Engine.Rendering.Settings.VrFoveationCenterUv;
    public float VrFoveationInnerRadius => Engine.Rendering.Settings.VrFoveationInnerRadius;
    public float VrFoveationOuterRadius => Engine.Rendering.Settings.VrFoveationOuterRadius;
    public Vector3 VrFoveationShadingRates => Engine.Rendering.Settings.VrFoveationShadingRates;
    public float VrFoveationVisibilityMargin => Engine.Rendering.Settings.VrFoveationVisibilityMargin;
    public bool VrFoveationForceFullResForUiAndNearField => Engine.Rendering.Settings.VrFoveationForceFullResForUiAndNearField;
    public float VrFoveationFullResNearDistanceMeters => Engine.Rendering.Settings.VrFoveationFullResNearDistanceMeters;
    public bool OpenXrCullWithFrustum => Engine.Rendering.Settings.OpenXrCullWithFrustum;
    public bool OpenXrDebugGl => Engine.Rendering.Settings.OpenXrDebugGl;
    public bool OpenXrDebugClearOnly => Engine.Rendering.Settings.OpenXrDebugClearOnly;
    public bool OpenXrDebugLifecycle => Engine.Rendering.Settings.OpenXrDebugLifecycle;
    public bool OpenXrDebugRenderRightThenLeft => Engine.Rendering.Settings.OpenXrDebugRenderRightThenLeft;
    public bool OpenXrPrepareFrameAfterDesktopRender => Engine.Rendering.Settings.OpenXrPrepareFrameAfterDesktopRender;
    public float OpenXrDeadlineSafetyMarginMs => Engine.Rendering.Settings.OpenXrDeadlineSafetyMarginMs;
    public OpenXRAPI.OpenXrCollectVisiblePosePolicy OpenXrCollectVisiblePosePolicy => Engine.Rendering.Settings.OpenXrCollectVisiblePosePolicy;
    public float OpenXrCollectVisibleFrustumPaddingDegrees => Engine.Rendering.Settings.OpenXrCollectVisibleFrustumPaddingDegrees;
    public OpenXRAPI.OpenXrTrackingLossPolicy OpenXrTrackingLossPolicy => Engine.Rendering.Settings.OpenXrTrackingLossPolicy;
    public OpenXRAPI.OpenXrActionSyncPolicy OpenXrActionSyncPolicy => Engine.Rendering.Settings.OpenXrActionSyncPolicy;
    public OpenXRAPI.OpenXrRenderPacingMode OpenXrRenderPacingMode => Engine.Rendering.Settings.OpenXrRenderPacingMode;

    public void TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        => _ = Engine.VRState.OpenXRApi?.TryRenderDesktopMirrorComposition(targetWidth, targetHeight);

    public void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
        => Engine.Rendering.Stats.RecordVrPerViewDrawCounts(leftDraws, rightDraws);

    public void DestroyObjectsForRenderer(IRuntimeRendererHost renderer)
    {
        if (renderer is AbstractRenderer abstractRenderer)
            Engine.Rendering.DestroyObjectsForRenderer(abstractRenderer);
    }

    public bool IsViewportCurrentlyRendering(IRuntimeViewportHost viewport)
        => viewport is XRViewport xrViewport &&
           (Engine.Rendering.State.RenderingPipelineState?.ViewportStack.Contains(xrViewport) ?? false);

    public bool ShouldForceDebugOpaquePipeline
        => string.Equals(
            Environment.GetEnvironmentVariable("XRE_FORCE_DEBUG_OPAQUE_PIPELINE"),
            "1",
            StringComparison.Ordinal);

    public IRuntimeRenderPipelineHost? CreateDebugOpaquePipelineOverride()
        => new DebugOpaqueRenderPipeline();

    public void PrepareUpscaleBridgeForFrame(IRuntimeViewportHost viewport, IRuntimeRenderPipelineFrameContext pipeline)
    {
        if (viewport is XRViewport xrViewport && pipeline is XRRenderPipelineInstance instance)
            Engine.Rendering.PrepareVulkanUpscaleBridgeForFrame(xrViewport, instance);
    }

    public void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program)
        => ExactTransparencyShaderBindings.ConfigureMaterialProgram(material, program);

    public int GetBytesPerPixel(ESizedInternalFormat format)
        => Engine.Rendering.Stats.GetBytesPerPixel(format);

    public int GetBytesPerPixel(ERenderBufferStorage storage)
        => Engine.Rendering.Stats.GetBytesPerPixel(storage);

    private static OpenGLRenderer? GetPrimaryOpenGlRenderer()
    {
        if (AbstractRenderer.Current is OpenGLRenderer currentRenderer)
            return currentRenderer;

        for (int i = 0; i < Engine.Windows.Count; i++)
        {
            if (Engine.Windows[i].Renderer is OpenGLRenderer renderer)
                return renderer;
        }

        return null;
    }

    public void AddFrameBufferBandwidth(long totalBytes)
        => Engine.Rendering.Stats.AddFBOBandwidth(totalBytes);

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
        => Engine.EnumerateActiveViewports();

    public IEnumerable<IPawnController> EnumerateLocalPlayers()
        => Engine.State.LocalPlayers.OfType<IPawnController>();

    public XRCamera.EDepthMode ResolveSceneCameraDepthModePreference()
        => Engine.Rendering.ResolveSceneCameraDepthModePreference();

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
        => renderer switch
        {
            VulkanRenderer => RuntimeGraphicsApiKind.Vulkan,
            OpenGLRenderer => RuntimeGraphicsApiKind.OpenGL,
            _ => RuntimeGraphicsApiKind.Unknown,
        };
}
