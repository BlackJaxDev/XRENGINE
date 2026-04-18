using System.Collections;
using System.IO;
using System.Numerics;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public enum RuntimeGraphicsApiKind
{
    Unknown = 0,
    OpenGL,
    Vulkan
}

public interface IRuntimeRenderPipelineHost
{
}

public interface IRuntimeRendererHost
{
}

public interface IRuntimeRenderWindowHost
{
}

public interface IRuntimeViewportHost
{
}

public interface IRuntimeWindowScenePanelAdapter : IDisposable
{
    XRTexture2D? Texture { get; }
    XRFrameBuffer? FrameBuffer { get; }
    void InvalidateResources();
    void InvalidateResourcesImmediate();
    void OnFramebufferResized(IRuntimeRenderWindowHost window, int framebufferWidth, int framebufferHeight);
    bool TryRenderScenePanelMode(IRuntimeRenderWindowHost window);
    void EndScenePanelMode(IRuntimeRenderWindowHost window);
}

public interface IRuntimeRenderingHostServices
{
    IDisposable? StartProfileScope(string? scopeName);
    bool AllowShaderPipelines { get; }
    bool EnableExactTransparencyTechniques { get; }
    bool UseInterleavedMeshBuffer { get; }
    bool UseIntegerUniformsInShaders { get; }
    bool RemapBlendshapeDeltas { get; }
    bool AllowBlendshapes { get; }
    bool PopulateVertexDataInParallel { get; }
    bool ProcessMeshImportsAsynchronously { get; }
    bool AllowSkinning { get; }
    bool OptimizeSkinningTo4Weights { get; }
    bool OptimizeSkinningWeightsIfPossible { get; }
    bool IsRenderThread { get; }
    bool IsRendererActive { get; }
    bool IsShadowPass { get; }
    bool IsStereoPass { get; }
    bool IsSceneCapturePass { get; }
    bool IsNvidia { get; }
    string AssetFileExtension { get; }
    string? TextureFallbackPath { get; }
    XRMaterial? InvalidMaterial { get; }
    Vector3 DefaultLuminance { get; }
    double RenderDeltaSeconds { get; }
    long LastRenderTimestampTicks { get; }
    long TrackedVramBytes { get; }
    long TrackedVramBudgetBytes { get; }
    ETwoPlayerPreference TwoPlayerViewportPreference { get; }
    EThreePlayerPreference ThreePlayerViewportPreference { get; }
    RuntimeGraphicsApiKind CurrentRenderBackend { get; }
    RuntimeGraphicsApiKind GetWindowRenderBackend(IRuntimeRenderWindowHost? window);
    void LogOutput(string message);
    void LogWarning(string message);
    void LogException(Exception ex, string? context = null);
    void RecordMissingAsset(string assetPath, string category, string? context = null);
    byte[] ReadAllBytes(string filePath);
    string ResolveTextureStreamingAuthorityPath(string filePath);
    SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format);
    bool TryScheduleSparseTextureStreamingTransitionAsync(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        CancellationToken cancellationToken,
        Action<SparseTextureStreamingTransitionResult> onCompleted,
        Action<Exception>? onError = null);
    SparseTextureStreamingFinalizeResult FinalizeSparseTextureStreamingTransition(
        XRTexture2D texture,
        SparseTextureStreamingTransitionRequest request,
        SparseTextureStreamingTransitionResult transitionResult);
    EnumeratorJob ScheduleEnumeratorJob(
        Func<IEnumerable> routineFactory,
        JobPriority priority = JobPriority.Normal,
        Action? completed = null,
        Action<Exception>? error = null,
        CancellationToken cancellationToken = default);
    void SubscribeViewportSwapBuffers(Action swapBuffers);
    void UnsubscribeViewportSwapBuffers(Action swapBuffers);
    void SubscribeViewportCollectVisible(Action collectVisible);
    void UnsubscribeViewportCollectVisible(Action collectVisible);
    void SubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame);
    void UnsubscribeWindowTickCallbacks(Action swapBuffers, Action renderFrame);
    void SubscribePlayModeTransitions(Action callback);
    void UnsubscribePlayModeTransitions(Action callback);
    void EnqueueRenderThreadTask(Action task);
    void EnqueueRenderThreadTask(Action task, string reason);
    void EnqueueRenderThreadCoroutine(Func<bool> task);
    TAsset? LoadAsset<TAsset>(string filePath) where TAsset : XRAsset, new();
    IRuntimeRenderPipelineHost? CreateDefaultRenderPipeline();
    IRuntimeRendererHost CreateRenderer(IRuntimeRenderWindowHost window, RuntimeGraphicsApiKind apiKind);
    IRuntimeWindowScenePanelAdapter CreateWindowScenePanelAdapter();
    bool AllowWindowClose(IRuntimeRenderWindowHost window);
    void RemoveWindow(IRuntimeRenderWindowHost window);
    void ReplicateWindowTargetWorldChange(IRuntimeRenderWindowHost window);
    void BeginRenderStatsFrame();
    bool IsWindowScenePanelPresentationEnabled { get; }
    bool ForceFullViewport { get; }
    bool RenderWindowsWhileInVR { get; }
    bool IsInVR { get; }
    bool IsOpenXRActive { get; }
    bool VrMirrorComposeFromEyeTextures { get; }
    void TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight);
    void DestroyObjectsForRenderer(IRuntimeRendererHost renderer);
    bool IsViewportCurrentlyRendering(IRuntimeViewportHost viewport);
    bool ShouldForceDebugOpaquePipeline { get; }
    IRuntimeRenderPipelineHost? CreateDebugOpaquePipelineOverride();
    void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program);
    int GetBytesPerPixel(ESizedInternalFormat format);
    int GetBytesPerPixel(ERenderBufferStorage storage);
    void AddFrameBufferBandwidth(long totalBytes);
    void DispatchCompute(XRRenderProgram program, uint groupCountX, uint groupCountY, uint groupCountZ);
    bool TryBlitFrameBufferToFrameBuffer(
        XRFrameBuffer sourceFrameBuffer,
        XRFrameBuffer destinationFrameBuffer,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter);
    bool TryBlitViewportToFrameBuffer(
        IRuntimeViewportGrabSource viewport,
        XRFrameBuffer framebuffer,
        EReadBufferMode readBuffer,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter);
}

public static class RuntimeRenderingHostServices
{
    private static IRuntimeRenderingHostServices _current = new DefaultRuntimeRenderingHostServices();

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
        public IDisposable? StartProfileScope(string? scopeName)
            => null;

        public bool AllowShaderPipelines => false;
        public bool EnableExactTransparencyTechniques => false;
        public bool UseInterleavedMeshBuffer => false;
        public bool UseIntegerUniformsInShaders => false;
        public bool RemapBlendshapeDeltas => false;
        public bool AllowBlendshapes => true;
        public bool PopulateVertexDataInParallel => true;
        public bool ProcessMeshImportsAsynchronously => false;
        public bool AllowSkinning => true;
        public bool OptimizeSkinningTo4Weights => false;
        public bool OptimizeSkinningWeightsIfPossible => false;
        public bool IsRenderThread => true;
        public bool IsRendererActive => false;
        public bool IsShadowPass => false;
        public bool IsStereoPass => false;
        public bool IsSceneCapturePass => false;
        public bool IsNvidia => false;
        public string AssetFileExtension => "asset";
        public string? TextureFallbackPath => null;
        public XRMaterial? InvalidMaterial => null;
        public Vector3 DefaultLuminance => new(0.2126f, 0.7152f, 0.0722f);
        public double RenderDeltaSeconds => 0.0;
        public long LastRenderTimestampTicks => 0L;
        public long TrackedVramBytes => 0L;
        public long TrackedVramBudgetBytes => long.MaxValue;
        public ETwoPlayerPreference TwoPlayerViewportPreference => ETwoPlayerPreference.SplitHorizontally;
        public EThreePlayerPreference ThreePlayerViewportPreference => EThreePlayerPreference.PreferFirstPlayer;
        public RuntimeGraphicsApiKind CurrentRenderBackend => RuntimeGraphicsApiKind.Unknown;

        public RuntimeGraphicsApiKind GetWindowRenderBackend(IRuntimeRenderWindowHost? window)
            => RuntimeGraphicsApiKind.Unknown;

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

        public byte[] ReadAllBytes(string filePath)
            => File.ReadAllBytes(filePath);

        public string ResolveTextureStreamingAuthorityPath(string filePath)
            => string.IsNullOrWhiteSpace(filePath) ? filePath : Path.GetFullPath(filePath);

        public SparseTextureStreamingSupport GetSparseTextureStreamingSupport(ESizedInternalFormat format)
            => SparseTextureStreamingSupport.Unsupported("No renderer-specific sparse texture capability service is configured.");

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
            => SparseTextureStreamingFinalizeResult.Failed("RuntimeRenderingHostServices.Current has not been configured for sparse texture finalization.");

        public EnumeratorJob ScheduleEnumeratorJob(
            Func<IEnumerable> routineFactory,
            JobPriority priority = JobPriority.Normal,
            Action? completed = null,
            Action<Exception>? error = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("RuntimeRenderingHostServices.Current has not been configured for job scheduling.");

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

        public void EnqueueRenderThreadCoroutine(Func<bool> task)
            => task();

        public TAsset? LoadAsset<TAsset>(string filePath) where TAsset : XRAsset, new()
            => null;

        public IRuntimeRenderPipelineHost? CreateDefaultRenderPipeline()
            => null;

        public IRuntimeRendererHost CreateRenderer(IRuntimeRenderWindowHost window, RuntimeGraphicsApiKind apiKind)
            => throw new InvalidOperationException("RuntimeRenderingHostServices.Current has not been configured to create renderers.");

        public IRuntimeWindowScenePanelAdapter CreateWindowScenePanelAdapter()
            => NullRuntimeWindowScenePanelAdapter.Instance;

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

        public bool IsWindowScenePanelPresentationEnabled => false;
        public bool ForceFullViewport => false;
        public bool RenderWindowsWhileInVR => false;
        public bool IsInVR => false;
        public bool IsOpenXRActive => false;
        public bool VrMirrorComposeFromEyeTextures => false;

        public void TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        {
        }

        public void DestroyObjectsForRenderer(IRuntimeRendererHost renderer)
        {
        }

        public bool IsViewportCurrentlyRendering(IRuntimeViewportHost viewport)
            => false;

        public bool ShouldForceDebugOpaquePipeline => false;

        public IRuntimeRenderPipelineHost? CreateDebugOpaquePipelineOverride()
            => null;

        public void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program)
        {
        }

        public int GetBytesPerPixel(ESizedInternalFormat format)
            => 4;

        public int GetBytesPerPixel(ERenderBufferStorage storage)
            => 4;

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
