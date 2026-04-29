using System.Collections;
using System.IO;
using System.Numerics;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

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
    public double RenderDeltaSeconds => Engine.Time.Timer.Render.Delta;
    public long LastRenderTimestampTicks => Engine.Time.Timer.Render.LastTimestampTicks;
    public long TrackedVramBytes => Engine.Rendering.Stats.AllocatedVRAMBytes;
    public long TrackedVramBudgetBytes => Engine.Rendering.Stats.VramBudgetBytes;
    public bool EnableGpuIndirectDebugLogging => Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;
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

    public void EnqueueRenderThreadCoroutine(Func<bool> task)
        => Engine.AddRenderThreadCoroutine(task);

    public void EnqueueRenderThreadCoroutine(Func<bool> task, string reason)
        => Engine.AddRenderThreadCoroutine(task, reason);

    public IDisposable? PushTransformId(uint transformId)
        => Engine.Rendering.State.PushTransformId(transformId);

    public void RecordOctreeSkippedMove()
        => Engine.Rendering.Stats.RecordOctreeSkippedMove();

    public void RenderDebugRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderRect2D(rectangle, solid, color);

    public void RenderDebugBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color)
        => Engine.Rendering.Debug.RenderBox(halfExtents, center, transform, solid, color);

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

    public bool IsWindowScenePanelPresentationEnabled
        => Engine.IsEditor &&
           Engine.EditorPreferences.ViewportPresentationMode == EditorPreferences.EViewportPresentationMode.UseViewportPanel;

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

    private static RuntimeGraphicsApiKind GetRendererBackend(object? renderer)
        => renderer switch
        {
            VulkanRenderer => RuntimeGraphicsApiKind.Vulkan,
            OpenGLRenderer => RuntimeGraphicsApiKind.OpenGL,
            _ => RuntimeGraphicsApiKind.Unknown,
        };
}
