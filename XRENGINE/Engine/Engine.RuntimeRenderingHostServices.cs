using System.Collections;
using System.IO;
using System.Numerics;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine;

internal sealed class EngineRuntimeRenderingHostServices : IRuntimeRenderingHostServices
{
    public IDisposable? StartProfileScope(string? scopeName)
        => string.IsNullOrWhiteSpace(scopeName)
            ? Engine.Profiler.Start()
            : Engine.Profiler.Start(scopeName);

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
    public bool IsNvidia => Engine.Rendering.State.IsNVIDIA;
    public string AssetFileExtension => AssetManager.AssetExtension;
    public string? TextureFallbackPath => Path.Combine(Engine.GameSettings.TexturesFolder, "Filler.png");
    public XRMaterial? InvalidMaterial => Engine.Rendering.State.CurrentRenderingPipeline?.InvalidMaterial;
    public Vector3 DefaultLuminance => Engine.Rendering.Settings.DefaultLuminance;
    public double RenderDeltaSeconds => Engine.Time.Timer.Render.Delta;
    public long LastRenderTimestampTicks => Engine.Time.Timer.Render.LastTimestampTicks;
    public ETwoPlayerPreference TwoPlayerViewportPreference => Engine.GameSettings.TwoPlayerViewportPreference;
    public EThreePlayerPreference ThreePlayerViewportPreference => Engine.GameSettings.ThreePlayerViewportPreference;
    public RuntimeGraphicsApiKind CurrentRenderBackend => GetRendererBackend(AbstractRenderer.Current);

    public void LogOutput(string message)
        => Debug.Out(message);

    public void LogWarning(string message)
        => Debug.LogWarning(message);

    public void LogException(Exception ex, string? context = null)
        => Debug.LogException(ex, context);

    public void RecordMissingAsset(string assetPath, string category, string? context = null)
        => AssetDiagnostics.RecordMissingAsset(assetPath, category, context);

    public byte[] ReadAllBytes(string filePath)
        => DirectStorageIO.ReadAllBytes(filePath);

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

    public void EnqueueRenderThreadCoroutine(Func<bool> task)
        => Engine.AddRenderThreadCoroutine(task);

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
    public bool IsInVR => Engine.VRState.IsInVR;
    public bool IsOpenXRActive => Engine.VRState.IsOpenXRActive;
    public bool VrMirrorComposeFromEyeTextures => Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures;

    public void TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        => _ = Engine.VRState.OpenXRApi?.TryRenderDesktopMirrorComposition(targetWidth, targetHeight);

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

    public void ConfigureMaterialProgram(XRMaterialBase material, XRRenderProgram program)
        => ExactTransparencyShaderBindings.ConfigureMaterialProgram(material, program);

    public int GetBytesPerPixel(ESizedInternalFormat format)
        => Engine.Rendering.Stats.GetBytesPerPixel(format);

    public int GetBytesPerPixel(ERenderBufferStorage storage)
        => Engine.Rendering.Stats.GetBytesPerPixel(storage);

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
