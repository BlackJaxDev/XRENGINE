using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Scene;
namespace XREngine.Rendering;

/// <summary>
/// Static access point for the host service implementation used by runtime rendering code.
/// </summary>
public static class RuntimeRenderingHostServices
{
    private static IRuntimeRenderingHostServices _current = new DefaultRuntimeRenderingHostServices();

    /// <summary>
    /// Current concrete host services. Assigning <see langword="null"/> resets to the safe default no-op host.
    /// </summary>
    public static IRuntimeRenderingHostServices Current
    {
        get => _current;
        set => _current = value ?? new DefaultRuntimeRenderingHostServices();
    }

    /// <summary>
    /// Absolute path to the game-level cache directory.  Set by the host engine
    /// during initialization.  Used by <c>BvhDiskCache</c> and similar caches
    /// that live in the rendering layer.
    /// </summary>
    public static string? GameCachePath { get; set; }

    private sealed class DefaultRuntimeRenderingHostServices : IRuntimeRenderingHostServices
    {
        private readonly Stopwatch _elapsedStopwatch = Stopwatch.StartNew();

        #region Profiling

        public IDisposable? StartProfileScope(string? scopeName)
            => null;

        #endregion

        #region Import and shader settings

        public bool AllowShaderPipelines => RuntimeRenderingHostServiceDefaults.AllowShaderPipelines;
        public bool EnableExactTransparencyTechniques => RuntimeRenderingHostServiceDefaults.EnableExactTransparencyTechniques;
        public bool UseInterleavedMeshBuffer => RuntimeRenderingHostServiceDefaults.UseInterleavedMeshBuffer;
        public bool UseIntegerUniformsInShaders => RuntimeRenderingHostServiceDefaults.UseIntegerUniformsInShaders;
        public bool RemapBlendshapeDeltas => RuntimeRenderingHostServiceDefaults.RemapBlendshapeDeltas;
        public bool AllowBlendshapes => RuntimeRenderingHostServiceDefaults.AllowBlendshapes;
        public bool PopulateVertexDataInParallel => RuntimeRenderingHostServiceDefaults.PopulateVertexDataInParallel;
        public bool ProcessMeshImportsAsynchronously => RuntimeRenderingHostServiceDefaults.ProcessMeshImportsAsynchronously;
        public bool AllowSkinning => RuntimeRenderingHostServiceDefaults.AllowSkinning;
        public bool OptimizeSkinningTo4Weights => RuntimeRenderingHostServiceDefaults.OptimizeSkinningTo4Weights;
        public bool OptimizeSkinningWeightsIfPossible => RuntimeRenderingHostServiceDefaults.OptimizeSkinningWeightsIfPossible;

        #endregion

        #region Frame and render state

        public bool IsRenderThread => RuntimeRenderingHostServiceDefaults.IsRenderThread;
        public bool IsRendererActive => RuntimeRenderingHostServiceDefaults.IsRendererActive;
        public bool IsShadowPass => RuntimeRenderingHostServiceDefaults.IsShadowPass;
        public bool IsStereoPass => RuntimeRenderingHostServiceDefaults.IsStereoPass;
        public bool IsSceneCapturePass => RuntimeRenderingHostServiceDefaults.IsSceneCapturePass;
        public bool RenderCullingVolumesEnabled => RuntimeRenderingHostServiceDefaults.RenderCullingVolumesEnabled;
        public bool IsNvidia => RuntimeRenderingHostServiceDefaults.IsNvidia;
        public Vector3 DefaultLuminance => new(
            RuntimeRenderingHostServiceDefaults.DefaultLuminanceX,
            RuntimeRenderingHostServiceDefaults.DefaultLuminanceY,
            RuntimeRenderingHostServiceDefaults.DefaultLuminanceZ);
        public long ElapsedTicks => _elapsedStopwatch.ElapsedTicks;
        public float ElapsedTime => (float)_elapsedStopwatch.Elapsed.TotalSeconds;
        public double UpdateDeltaSeconds => RuntimeRenderingHostServiceDefaults.DefaultDeltaSeconds;
        public long LastUpdateTimestampTicks => RuntimeRenderingHostServiceDefaults.DefaultTimestampTicks;
        public double RenderDeltaSeconds => RuntimeRenderingHostServiceDefaults.DefaultDeltaSeconds;
        public long LastRenderTimestampTicks => RuntimeRenderingHostServiceDefaults.DefaultTimestampTicks;
        public long TrackedVramBytes => RuntimeRenderingHostServiceDefaults.DefaultTrackedVramBytes;
        public long TrackedVramBudgetBytes => RuntimeRenderingHostServiceDefaults.DefaultTrackedVramBudgetBytes;
        public bool EnableGpuIndirectDebugLogging => RuntimeRenderingHostServiceDefaults.EnableGpuIndirectDebugLogging;
        public ETwoPlayerPreference TwoPlayerViewportPreference => RuntimeRenderingHostServiceDefaults.TwoPlayerViewportPreference;
        public EThreePlayerPreference ThreePlayerViewportPreference => RuntimeRenderingHostServiceDefaults.ThreePlayerViewportPreference;
        public RuntimeGraphicsApiKind CurrentRenderBackend => RuntimeRenderingHostServiceDefaults.CurrentRenderBackend;
        public IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState => null;
        public IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext => null;
        public bool IsPlayModeTransitioning => RuntimeRenderingHostServiceDefaults.IsPlayModeTransitioning;
        public string PlayModeStateName => RuntimeRenderingHostServiceDefaults.PlayModeStateName;
        public EAntiAliasingMode DefaultAntiAliasingMode => RuntimeRenderingHostServiceDefaults.DefaultAntiAliasingMode;
        public uint DefaultMsaaSampleCount => RuntimeRenderingHostServiceDefaults.DefaultMsaaSampleCount;
        public bool DefaultOutputHDR => RuntimeRenderingHostServiceDefaults.DefaultOutputHDR;
        public float DefaultTsrRenderScale => RuntimeRenderingHostServiceDefaults.DefaultTsrRenderScale;
        public bool ForwardDepthPrePassEnabled => RuntimeRenderingHostServiceDefaults.ForwardDepthPrePassEnabled;
        public bool ForwardPrePassSharesGBufferTargets => RuntimeRenderingHostServiceDefaults.ForwardPrePassSharesGBufferTargets;
        public bool EnableRenderStatisticsTracking => RuntimeRenderingHostServiceDefaults.EnableRenderStatisticsTracking;
        public bool EnableGpuRenderPipelineProfiling => RuntimeRenderingHostServiceDefaults.EnableGpuRenderPipelineProfiling;
        public ulong CurrentRenderFrameId => RuntimeRenderingHostServiceDefaults.CurrentRenderFrameId;

        #endregion

        #region Shadow settings

        public bool ProvidesShadowAtlasSettings => RuntimeRenderingHostServiceDefaults.ProvidesShadowAtlasSettings;
        public bool UseSpotShadowAtlas => RuntimeRenderingHostServiceDefaults.UseSpotShadowAtlas;
        public bool UseDirectionalShadowAtlas => RuntimeRenderingHostServiceDefaults.UseDirectionalShadowAtlas;
        public bool UsePointShadowAtlas => RuntimeRenderingHostServiceDefaults.UsePointShadowAtlas;
        public uint ShadowAtlasPageSize => RuntimeRenderingHostServiceDefaults.ShadowAtlasPageSize;
        public int MaxShadowAtlasPages => RuntimeRenderingHostServiceDefaults.MaxShadowAtlasPages;
        public long MaxShadowAtlasMemoryBytes => RuntimeRenderingHostServiceDefaults.MaxShadowAtlasMemoryBytes;
        public int MaxShadowTilesRenderedPerFrame => RuntimeRenderingHostServiceDefaults.MaxShadowTilesRenderedPerFrame;
        public float MaxShadowRenderMilliseconds => RuntimeRenderingHostServiceDefaults.MaxShadowRenderMilliseconds;
        public uint MinShadowAtlasTileResolution => RuntimeRenderingHostServiceDefaults.MinShadowAtlasTileResolution;
        public uint MaxShadowAtlasTileResolution => RuntimeRenderingHostServiceDefaults.MaxShadowAtlasTileResolution;

        #endregion

        #region Viewports, cameras, and players

        public RuntimeGraphicsApiKind GetWindowRenderBackend(IRuntimeRenderWindowHost? window)
            => RuntimeRenderingHostServiceDefaults.CurrentRenderBackend;

        public IEnumerable<IRuntimeViewportHost> EnumerateActiveViewports()
            => [];

        public IEnumerable<IPawnController> EnumerateLocalPlayers()
            => [];

        public XRCamera.EDepthMode ResolveSceneCameraDepthModePreference()
            => RuntimeRenderingHostServiceDefaults.SceneCameraDepthModePreference;

        public IRuntimeInputControllablePawn? EnsurePawnForCamera(SceneNode sceneNode, CameraComponent camera, ELocalPlayerIndex playerIndex, Type? pawnType = null)
            => null;

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
            physicsFinishedCallback(null);
        }

        #endregion

        #region Pipeline context and diagnostics

        public IDisposable? PushRenderingPipeline(IRuntimeRenderPipelineFrameContext pipeline)
            => null;

        public void LogOutput(string message)
        {
        }

        public void LogWarning(string message)
        {
        }

        public void LogException(Exception ex, string? context = null)
        {
        }

        public void RecordMissingAsset(string assetPath, string category, string? context = null)
        {
        }

        #endregion

        #region Asset and texture IO

        public string AssetFileExtension => RuntimeRenderingHostServiceDefaults.AssetFileExtension;
        public string? TextureFallbackPath => null;
        public XRMaterial? InvalidMaterial => null;
        public TextureRuntimeLogMode TextureLogMode => RuntimeRenderingHostServiceDefaults.TextureLogMode;
        public double TextureSlowCpuDecodeResizeMilliseconds => RuntimeRenderingHostServiceDefaults.TextureSlowCpuDecodeResizeMilliseconds;
        public double TextureSlowMipBuildMilliseconds => RuntimeRenderingHostServiceDefaults.TextureSlowMipBuildMilliseconds;
        public double TextureSlowUploadChunkMilliseconds => RuntimeRenderingHostServiceDefaults.TextureSlowUploadChunkMilliseconds;
        public double TextureSlowTransitionMilliseconds => RuntimeRenderingHostServiceDefaults.TextureSlowTransitionMilliseconds;
        public double TextureSlowQueueWaitMilliseconds => RuntimeRenderingHostServiceDefaults.TextureSlowQueueWaitMilliseconds;
        public double TextureUploadFrameBudgetMilliseconds => RuntimeRenderingHostServiceDefaults.TextureUploadFrameBudgetMilliseconds;

        public byte[] ReadAllBytes(string filePath)
            => File.ReadAllBytes(filePath);

        public string ResolveTextureStreamingAuthorityPath(string filePath)
            => string.IsNullOrWhiteSpace(filePath) ? filePath : Path.GetFullPath(filePath);

        public SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format)
            => SparseTextureStreamingSupport.Unsupported(RuntimeRenderingHostServiceDefaults.SparseTextureStreamingUnsupportedReason);

        public bool TryScheduleSparseTextureStreamingTransitionAsync(
            XRTexture2D texture,
            SparseTextureStreamingTransitionRequest request,
            CancellationToken cancellationToken,
            Action<SparseTextureStreamingTransitionResult> onCompleted,
            Action<Exception>? onError = null)
            => false;

        public SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
            XRTexture2D texture,
            SparseTextureStreamingTransitionRequest request,
            SparseTextureStreamingTransitionResult transitionResult)
            => SparseTextureStreamingFinalizeResult.Failed(RuntimeRenderingHostServiceDefaults.SparseTextureStreamingFinalizeNotConfiguredReason);

        public EnumeratorJob ScheduleEnumeratorJob(
            Func<IEnumerable> routineFactory,
            JobPriority priority = JobPriority.Normal,
            Action? completed = null,
            Action<Exception>? error = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(RuntimeRenderingHostServiceDefaults.JobSchedulingNotConfiguredMessage);

        #endregion

        #region Scheduling and frame callbacks

        public void SubscribeViewportSwapBuffers(Action swapBuffers)
        {
        }

        public void UnsubscribeViewportSwapBuffers(Action swapBuffers)
        {
        }

        public void SubscribeViewportCollectVisible(Action collectVisible)
        {
        }

        public void UnsubscribeViewportCollectVisible(Action collectVisible)
        {
        }

        public void SubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame)
        {
        }

        public void UnsubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame)
        {
        }

        public void SubscribePlayModeTransitions(Action callback)
        {
        }

        public void UnsubscribePlayModeTransitions(Action callback)
        {
        }

        public void EnqueueRenderThreadTask(Action task)
            => task();

        public void EnqueueRenderThreadTask(Action task, string reason)
            => task();

        public void EnqueueAppThreadTask(Action task)
            => task();

        public void EnqueueAppThreadTask(Action task, string reason)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task)
            => task();

        public void EnqueueRenderThreadCoroutine(Func<bool> task, string reason)
            => task();

        public void ProcessRenderThreadTasks()
        {
        }

        #endregion

        #region Debug drawing and scene maintenance

        public IDisposable? PushTransformId(uint transformId)
            => null;

        public void RecordOctreeSkippedMove()
        {
        }

        public void ProcessGpuPhysicsChainDispatches()
        {
        }

        public void ProcessGpuPhysicsChainCompletions()
        {
        }

        public void RenderDebugRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugLine(Vector3 start, Vector3 end, ColorF4 color)
        {
        }

        public void RenderDebugSphere(Vector3 center, float radius, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugCone(Vector3 center, Vector3 up, float radius, float height, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugAABB(Vector3 halfExtents, Vector3 center, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugQuad(Vector3 center, Rotator rotation, Vector2 extents, bool solid, ColorF4 color)
        {
        }

        public void RenderDebugPoint(Vector3 position, ColorF4 color)
        {
        }

        public void RenderDebugText(Vector3 position, string text, ColorF4 color)
        {
        }

        public void RenderDebugShapes()
        {
        }

        #endregion

        #region Factories and window presentation

        public TAsset? LoadAsset<TAsset>(string filePath) where TAsset : XRAsset, new()
            => null;

        public IRuntimeRenderPipelineHost? CreateDefaultRenderPipeline()
            => null;

        public IRuntimeRendererHost CreateRenderer(IRuntimeRenderWindowHost window, RuntimeGraphicsApiKind apiKind)
            => throw new InvalidOperationException(RuntimeRenderingHostServiceDefaults.RendererCreationNotConfiguredMessage);

        public IRuntimeWindowScenePanelAdapter CreateWindowScenePanelAdapter()
            => NullRuntimeWindowScenePanelAdapter.Instance;

        public BoundingRectangle? GetScenePanelRenderRegion(IRuntimeRenderWindowHost window)
            => null;

        public bool AllowWindowClose(IRuntimeRenderWindowHost window)
            => true;

        public void RemoveWindow(IRuntimeRenderWindowHost window)
        {
        }

        public void ReplicateWindowTargetWorldChange(IRuntimeRenderWindowHost window)
        {
        }

        public void BeginRenderStatsFrame()
        {
        }

        public bool IsWindowScenePanelPresentationEnabled => RuntimeRenderingHostServiceDefaults.IsWindowScenePanelPresentationEnabled;
        public int ScenePanelResizeDebounceMs => RuntimeRenderingHostServiceDefaults.ScenePanelResizeDebounceMs;
        public bool ForceFullViewport => RuntimeRenderingHostServiceDefaults.ForceFullViewport;

        #endregion

        #region VR and desktop mirror

        public bool RenderWindowsWhileInVR => RuntimeRenderingHostServiceDefaults.RenderWindowsWhileInVR;
        public bool EnableVrFoveatedViewSet => RuntimeRenderingHostServiceDefaults.EnableVrFoveatedViewSet;
        public bool IsInVR => RuntimeRenderingHostServiceDefaults.IsInVR;
        public bool IsOpenXRActive => RuntimeRenderingHostServiceDefaults.IsOpenXRActive;
        public bool VrMirrorComposeFromEyeTextures => RuntimeRenderingHostServiceDefaults.VrMirrorComposeFromEyeTextures;
        public Vector2 VrFoveationCenterUv => new(
            RuntimeRenderingHostServiceDefaults.VrFoveationCenterU,
            RuntimeRenderingHostServiceDefaults.VrFoveationCenterV);
        public float VrFoveationInnerRadius => RuntimeRenderingHostServiceDefaults.VrFoveationInnerRadius;
        public float VrFoveationOuterRadius => RuntimeRenderingHostServiceDefaults.VrFoveationOuterRadius;
        public Vector3 VrFoveationShadingRates => new(
            RuntimeRenderingHostServiceDefaults.VrFoveationInnerShadingRate,
            RuntimeRenderingHostServiceDefaults.VrFoveationMiddleShadingRate,
            RuntimeRenderingHostServiceDefaults.VrFoveationOuterShadingRate);
        public float VrFoveationVisibilityMargin => RuntimeRenderingHostServiceDefaults.VrFoveationVisibilityMargin;
        public bool VrFoveationForceFullResForUiAndNearField => RuntimeRenderingHostServiceDefaults.VrFoveationForceFullResForUiAndNearField;
        public float VrFoveationFullResNearDistanceMeters => RuntimeRenderingHostServiceDefaults.VrFoveationFullResNearDistanceMeters;

        public void TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        {
        }

        public void RecordVrPerViewDrawCounts(uint leftDraws, uint rightDraws)
        {
        }

        #endregion

        #region Backend utilities

        public void DestroyObjectsForRenderer(IRuntimeRendererHost renderer)
        {
        }

        public bool IsViewportCurrentlyRendering(IRuntimeViewportHost viewport)
            => RuntimeRenderingHostServiceDefaults.IsViewportCurrentlyRendering;

        public bool ShouldForceDebugOpaquePipeline => RuntimeRenderingHostServiceDefaults.ShouldForceDebugOpaquePipeline;

        public IRuntimeRenderPipelineHost? CreateDebugOpaquePipelineOverride()
            => null;

        public void PrepareUpscaleBridgeForFrame(IRuntimeViewportHost viewport, IRuntimeRenderPipelineFrameContext pipeline)
        {
        }

        public void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program)
        {
        }

        public int GetBytesPerPixel(ESizedInternalFormat format)
            => RuntimeRenderingHostServiceDefaults.FallbackBytesPerPixel;

        public int GetBytesPerPixel(ERenderBufferStorage storage)
            => RuntimeRenderingHostServiceDefaults.FallbackBytesPerPixel;

        public void AddFrameBufferBandwidth(long totalBytes)
        {
        }

        public void DispatchCompute(XRRenderProgram program, uint groupCountX, uint groupCountY, uint groupCountZ)
        {
        }

        public bool TryBlitFrameBufferToFrameBuffer(
            XRFrameBuffer sourceFrameBuffer,
            XRFrameBuffer destinationFrameBuffer,
            EReadBufferMode readBuffer,
            bool colorBit,
            bool depthBit,
            bool stencilBit,
            bool linearFilter)
            => false;

        public bool TryBlitViewportToFrameBuffer(
            IRuntimeViewportGrabSource viewport,
            XRFrameBuffer framebuffer,
            EReadBufferMode readBuffer,
            bool colorBit,
            bool depthBit,
            bool stencilBit,
            bool linearFilter)
            => false;

        #endregion

        /// <summary>
        /// Null object used when no editor scene-panel presentation adapter is installed.
        /// </summary>
        private sealed class NullRuntimeWindowScenePanelAdapter : IRuntimeWindowScenePanelAdapter
        {
            public static NullRuntimeWindowScenePanelAdapter Instance { get; } = new();

            public XRTexture2D? Texture => null;
            public XRFrameBuffer? FrameBuffer => null;

            public void Dispose()
            {
            }

            public void InvalidateResources()
            {
            }

            public void InvalidateResourcesImmediate()
            {
            }

            public void OnFramebufferResized(IRuntimeRenderWindowHost window, int framebufferWidth, int framebufferHeight)
            {
            }

            public bool TryRenderScenePanelMode(IRuntimeRenderWindowHost window)
                => false;

            public void EndScenePanelMode(IRuntimeRenderWindowHost window)
            {
            }
        }
    }
}
