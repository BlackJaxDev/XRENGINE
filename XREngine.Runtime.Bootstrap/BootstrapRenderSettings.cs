using System.Numerics;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapRenderSettings
{
    private static bool _emulatedVrStereoPreviewHooked;
    private static string? _lastOpenGLShaderLinkSettingsLog;

    private static void EnsureEmulatedVRStereoPreviewRenderingHooked()
    {
        if (_emulatedVrStereoPreviewHooked)
            return;

        var settings = RuntimeBootstrapState.Settings;
        if (!(settings.VRPawn && settings.EmulatedVRPawn && settings.PreviewVRStereoViews))
            return;

        _emulatedVrStereoPreviewHooked = true;

        Engine.Windows.PostAnythingAdded += OnWindowAddedForEmulatedVRStereoPreview;
        foreach (var window in Engine.Windows)
            OnWindowAddedForEmulatedVRStereoPreview(window);
    }

    private static void OnWindowAddedForEmulatedVRStereoPreview(XRWindow window)
        => Engine.InvokeOnMainThread(
            () => Engine.VRState.InitRenderEmulated(window),
            "Bootstrap: Init emulated VR stereo",
            executeNowIfAlreadyMainThread: true);

    public static void Apply()
    {
        var settings = RuntimeBootstrapState.Settings;
        var renderSettings = Engine.Rendering.Settings;
        var debug = Engine.EditorPreferences.Debug;
        ApplyOpenGLShaderLinkSettings(settings);

        debug.RenderMesh3DBounds = settings.RenderMeshBounds;
        debug.RenderTransformDebugInfo = settings.RenderTransformDebugInfo;
        debug.RenderTransformLines = settings.RenderTransformLines;
        debug.RenderTransformCapsules = settings.RenderTransformCapsules;
        debug.RenderTransformPoints = settings.RenderTransformPoints;
        debug.Preview3DWorldOctree = settings.VisualizeOctree;
        debug.Preview2DWorldQuadtree = settings.VisualizeQuadtree;
        debug.RenderCullingVolumes = false;

        renderSettings.RecalcChildMatricesLoopType = settings.RecalcChildMatricesType;
        renderSettings.TickGroupedItemsInParallel = settings.TickGroupedItemsInParallel;
        renderSettings.RenderWindowsWhileInVR = settings.RenderWindowsWhileInVR;
        renderSettings.AllowShaderPipelines = settings.AllowShaderPipelines;
        renderSettings.AllowSkinning = settings.AllowSkinning;
        renderSettings.RenderVRSinglePassStereo = settings.SinglePassStereoVR;
        Debug.Out($"[BootstrapRenderSettings] Applied AllowSkinning={renderSettings.AllowSkinning} AllowShaderPipelines={renderSettings.AllowShaderPipelines}");
        if (settings.RenderPhysicsDebug)
            renderSettings.PhysicsVisualizeSettings.SetAllTrue();

        // Profiler frame logging is driven by EditorPreferences.Debug.EnableProfilerFrameLogging,
        // whose setter syncs Engine.Profiler.EnableFrameLogging automatically.
        // Do not override it here — that would discard the user's saved preference.

        EnsureEmulatedVRStereoPreviewRenderingHooked();
    }

    public static void ApplyOpenGLShaderLinkSettings(UnitTestingWorldSettings settings)
    {
        var renderSettings = Engine.Rendering.Settings;
        int rawCompilerThreadCount = settings.OpenGLShaderCompilerThreadCount;
        int compilerThreadCount = ResolveOpenGLShaderCompilerThreadCount(settings.OpenGLShaderLinkStrategy, rawCompilerThreadCount);

        renderSettings.AllowBinaryProgramCaching = settings.AllowBinaryProgramCaching;
        renderSettings.AsyncProgramBinaryUpload = settings.AsyncProgramBinaryUpload;
        renderSettings.AsyncProgramCompilation = settings.AsyncProgramCompilation;
        renderSettings.OpenGLProgramCompileLinkWorkerCount = settings.OpenGLProgramCompileLinkWorkerCount;
        renderSettings.MaxAsyncShaderProgramsPerFrame = settings.MaxAsyncShaderProgramsPerFrame;
        renderSettings.OpenGLShaderLinkStrategy = settings.OpenGLShaderLinkStrategy;
        renderSettings.OpenGLShaderCompilerThreadCount = compilerThreadCount;
        renderSettings.OpenGLParallelShaderCompileProbeEnabled = settings.OpenGLParallelShaderCompileProbeEnabled;
        renderSettings.OpenGLParallelShaderCompileProbeTimeoutMs = settings.OpenGLParallelShaderCompileProbeTimeoutMs;

        LogOpenGLShaderLinkSettings(settings, rawCompilerThreadCount, compilerThreadCount);
    }

    private static int ResolveOpenGLShaderCompilerThreadCount(EOpenGLShaderLinkStrategy strategy, int configuredThreadCount)
    {
        if (strategy == EOpenGLShaderLinkStrategy.DriverParallel && configuredThreadCount == 0)
        {
            Debug.Out("[BootstrapRenderSettings] OpenGLShaderCompilerThreadCount=0 disables driver compiler threads; using -1 because OpenGLShaderLinkStrategy=DriverParallel.");
            return -1;
        }

        return configuredThreadCount;
    }

    private static void LogOpenGLShaderLinkSettings(
        UnitTestingWorldSettings settings,
        int rawCompilerThreadCount,
        int appliedCompilerThreadCount)
    {
        string compilerThreads = rawCompilerThreadCount == appliedCompilerThreadCount
            ? appliedCompilerThreadCount.ToString()
            : $"{rawCompilerThreadCount}->{appliedCompilerThreadCount}";

        string summary =
            $"strategy={settings.OpenGLShaderLinkStrategy}, cache={settings.AllowBinaryProgramCaching}, asyncBinaryUpload={settings.AsyncProgramBinaryUpload}, " +
            $"asyncSource={settings.AsyncProgramCompilation}, sharedWorkers={settings.OpenGLProgramCompileLinkWorkerCount}, maxAsyncPerFrame={settings.MaxAsyncShaderProgramsPerFrame}, " +
            $"compilerThreads={compilerThreads}, probe={settings.OpenGLParallelShaderCompileProbeEnabled}, probeTimeoutMs={settings.OpenGLParallelShaderCompileProbeTimeoutMs}";

        if (string.Equals(_lastOpenGLShaderLinkSettingsLog, summary, StringComparison.Ordinal))
            return;

        _lastOpenGLShaderLinkSettingsLog = summary;
        Debug.Out($"[BootstrapRenderSettings] OpenGL shader linking: {summary}");
    }
}
