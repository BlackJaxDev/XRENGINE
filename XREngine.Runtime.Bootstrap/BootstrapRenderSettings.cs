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
        if (!(settings.VRPawn && settings.SceneOnlyVRPawn && settings.PreviewVRStereoViews))
            return;

        _emulatedVrStereoPreviewHooked = true;

        Engine.Windows.PostAnythingAdded += OnWindowAddedForEmulatedVRStereoPreview;
        foreach (var window in Engine.Windows)
            OnWindowAddedForEmulatedVRStereoPreview(window);
    }

    private static void OnWindowAddedForEmulatedVRStereoPreview(XRWindow window)
        => Engine.InvokeOnMainThread(
            () => Engine.VRState.InitRenderEmulated(window),
            "Bootstrap: Init scene-only VR stereo",
            executeNowIfAlreadyMainThread: true);

    public static void Apply()
    {
        var settings = RuntimeBootstrapState.Settings;
        var renderSettings = Engine.Rendering.Settings;
        var debug = Engine.EditorPreferences.Debug;
        ApplyOpenGLShaderLinkSettings(settings);

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderMeshBounds)))
            debug.RenderMesh3DBounds = settings.RenderMeshBounds;
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderTransformDebugInfo)))
            debug.RenderTransformDebugInfo = settings.RenderTransformDebugInfo;
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderTransformLines)))
            debug.RenderTransformLines = settings.RenderTransformLines;
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderTransformCapsules)))
            debug.RenderTransformCapsules = settings.RenderTransformCapsules;
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderTransformPoints)))
            debug.RenderTransformPoints = settings.RenderTransformPoints;
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VisualizeOctree)))
            debug.Preview3DWorldOctree = settings.VisualizeOctree;
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.VisualizeQuadtree)))
            debug.Preview2DWorldQuadtree = settings.VisualizeQuadtree;
        debug.RenderCullingVolumes = false;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RecalcChildMatricesType)))
            renderSettings.RecalcChildMatricesLoopType = settings.RecalcChildMatricesType;
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.TickGroupedItemsInParallel)))
            renderSettings.TickGroupedItemsInParallel = settings.TickGroupedItemsInParallel;

        bool requiresDesktopVrWindow = settings.VRPawn && (settings.AllowEditingInVR || settings.PreviewVRStereoViews);
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.RenderWindowsWhileInVR)) || requiresDesktopVrWindow)
            renderSettings.RenderWindowsWhileInVR = settings.RenderWindowsWhileInVR || requiresDesktopVrWindow;
        renderSettings.VrMirrorComposeFromEyeTextures = !requiresDesktopVrWindow;

        bool groupedRenderingSpecified = settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering));
        if (groupedRenderingSpecified)
        {
            renderSettings.AllowShaderPipelines = settings.Rendering.OpenGL.AllowProgramPipelines;
            renderSettings.VulkanRenderTargetMode = settings.Rendering.Vulkan.RenderTargetMode;
            renderSettings.Vulkan.Startup.FallbackPolicy = settings.Rendering.BackendFallbackPolicy;
        }
        else if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AllowShaderPipelines)))
        {
            renderSettings.AllowShaderPipelines = settings.AllowShaderPipelines;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AllowSkinning)))
            renderSettings.AllowSkinning = settings.AllowSkinning;
        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.SinglePassStereoVR)))
            renderSettings.RenderVRSinglePassStereo = settings.SinglePassStereoVR;
        Debug.Out(
            $"[BootstrapRenderSettings] Applied AllowSkinning={renderSettings.AllowSkinning} " +
            $"AllowShaderPipelines={renderSettings.AllowShaderPipelines} " +
            $"RenderWindowsWhileInVR={renderSettings.RenderWindowsWhileInVR} " +
            $"VrMirrorComposeFromEyeTextures={renderSettings.VrMirrorComposeFromEyeTextures}");
        if (settings.RenderPhysicsDebug)
            renderSettings.PhysicsVisualizeSettings.SetAllTrue();

        // Profiler frame logging is driven by EditorPreferences.Debug.EnableProfilerFrameLogging,
        // whose setter syncs Engine.Profiler.EnableFrameLogging automatically.
        // Do not override it here â€” that would discard the user's saved preference.

        EnsureEmulatedVRStereoPreviewRenderingHooked();
    }

    public static void ReapplyEditorRenderStateAfterBootstrap(string reason)
    {
        try
        {
            Engine.Rendering.ApplyEditorPreferencesChange(null);

            foreach (var window in Engine.Windows)
                window.RequestRenderStateRecheck(resetCircuitBreaker: true);

            Debug.Rendering($"[BootstrapRenderSettings] Reapplied editor render state after bootstrap ({reason}).");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, $"[BootstrapRenderSettings] Failed to reapply editor render state after bootstrap ({reason}).");
        }
    }

    public static void ApplyOpenGLShaderLinkSettings(UnitTestingWorldSettings settings)
    {
        var renderSettings = Engine.Rendering.Settings;
        bool applied = false;

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.Rendering)))
        {
            UnitTestingOpenGLShaderLinkingSettings linkSettings = settings.Rendering.OpenGL.ShaderLinking;
            int groupedRawCompilerThreadCount = linkSettings.DriverCompilerThreadCount;
            int groupedCompilerThreadCount = ResolveOpenGLShaderCompilerThreadCount(linkSettings.Strategy, groupedRawCompilerThreadCount);

            renderSettings.OpenGLShaderLinkStrategy = linkSettings.Strategy;
            renderSettings.AllowBinaryProgramCaching = linkSettings.AllowBinaryProgramCaching;
            renderSettings.AsyncProgramBinaryUpload = linkSettings.AsyncProgramBinaryUpload;
            renderSettings.AsyncProgramCompilation = linkSettings.AsyncProgramCompilation;
            renderSettings.OpenGLProgramCompileLinkWorkerCount = linkSettings.ProgramCompileLinkWorkerCount;
            renderSettings.MaxAsyncShaderProgramsPerFrame = linkSettings.MaxAsyncShaderProgramsPerFrame;
            renderSettings.OpenGLShaderCompilerThreadCount = groupedCompilerThreadCount;
            renderSettings.OpenGLParallelShaderCompileProbeEnabled = linkSettings.DriverParallelProbeEnabled;
            renderSettings.OpenGLParallelShaderCompileProbeTimeoutMs = linkSettings.DriverParallelProbeTimeoutMs;

            LogOpenGLShaderLinkSettings(groupedRawCompilerThreadCount, groupedCompilerThreadCount);
            return;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AllowBinaryProgramCaching)))
        {
            renderSettings.AllowBinaryProgramCaching = settings.AllowBinaryProgramCaching;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AsyncProgramBinaryUpload)))
        {
            renderSettings.AsyncProgramBinaryUpload = settings.AsyncProgramBinaryUpload;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.AsyncProgramCompilation)))
        {
            renderSettings.AsyncProgramCompilation = settings.AsyncProgramCompilation;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLProgramCompileLinkWorkerCount)))
        {
            renderSettings.OpenGLProgramCompileLinkWorkerCount = settings.OpenGLProgramCompileLinkWorkerCount;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.MaxAsyncShaderProgramsPerFrame)))
        {
            renderSettings.MaxAsyncShaderProgramsPerFrame = settings.MaxAsyncShaderProgramsPerFrame;
            applied = true;
        }

        bool strategySpecified = settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLShaderLinkStrategy));
        bool compilerThreadCountSpecified = settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLShaderCompilerThreadCount));
        int rawCompilerThreadCount = compilerThreadCountSpecified
            ? settings.OpenGLShaderCompilerThreadCount
            : renderSettings.OpenGLShaderCompilerThreadCount;
        EOpenGLShaderLinkStrategy linkStrategy = strategySpecified
            ? settings.OpenGLShaderLinkStrategy
            : renderSettings.OpenGLShaderLinkStrategy;
        int compilerThreadCount = ResolveOpenGLShaderCompilerThreadCount(linkStrategy, rawCompilerThreadCount);

        if (strategySpecified)
        {
            renderSettings.OpenGLShaderLinkStrategy = settings.OpenGLShaderLinkStrategy;
            applied = true;
        }

        if (strategySpecified || compilerThreadCountSpecified)
        {
            renderSettings.OpenGLShaderCompilerThreadCount = compilerThreadCount;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLParallelShaderCompileProbeEnabled)))
        {
            renderSettings.OpenGLParallelShaderCompileProbeEnabled = settings.OpenGLParallelShaderCompileProbeEnabled;
            applied = true;
        }

        if (settings.IsJsonPropertySpecified(nameof(UnitTestingWorldSettings.OpenGLParallelShaderCompileProbeTimeoutMs)))
        {
            renderSettings.OpenGLParallelShaderCompileProbeTimeoutMs = settings.OpenGLParallelShaderCompileProbeTimeoutMs;
            applied = true;
        }

        if (applied)
            LogOpenGLShaderLinkSettings(rawCompilerThreadCount, compilerThreadCount);
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
        int rawCompilerThreadCount,
        int appliedCompilerThreadCount)
    {
        var renderSettings = Engine.Rendering.Settings;
        string compilerThreads = rawCompilerThreadCount == appliedCompilerThreadCount
            ? appliedCompilerThreadCount.ToString()
            : $"{rawCompilerThreadCount}->{appliedCompilerThreadCount}";

        string summary =
            $"strategy={renderSettings.OpenGLShaderLinkStrategy}, cache={renderSettings.AllowBinaryProgramCaching}, asyncBinaryUpload={renderSettings.AsyncProgramBinaryUpload}, " +
            $"asyncSource={renderSettings.AsyncProgramCompilation}, sharedWorkers={renderSettings.OpenGLProgramCompileLinkWorkerCount}, maxAsyncPerFrame={renderSettings.MaxAsyncShaderProgramsPerFrame}, " +
            $"compilerThreads={compilerThreads}, probe={renderSettings.OpenGLParallelShaderCompileProbeEnabled}, probeTimeoutMs={renderSettings.OpenGLParallelShaderCompileProbeTimeoutMs}";

        if (string.Equals(_lastOpenGLShaderLinkSettingsLog, summary, StringComparison.Ordinal))
            return;

        _lastOpenGLShaderLinkSettingsLog = summary;
        Debug.Out($"[BootstrapRenderSettings] OpenGL shader linking: {summary}");
    }
}
