using System.Numerics;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Runtime.Bootstrap;

public static class BootstrapRenderSettings
{
    private static bool _emulatedVrStereoPreviewHooked;

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

        debug.RenderMesh3DBounds = settings.RenderMeshBounds;
        debug.RenderTransformDebugInfo = settings.RenderTransformDebugInfo;
        debug.RenderTransformLines = settings.RenderTransformLines;
        debug.RenderTransformCapsules = settings.RenderTransformCapsules;
        debug.RenderTransformPoints = settings.RenderTransformPoints;
        debug.RenderCullingVolumes = false;

        renderSettings.RecalcChildMatricesLoopType = settings.RecalcChildMatricesType;
        renderSettings.TickGroupedItemsInParallel = settings.TickGroupedItemsInParallel;
        renderSettings.RenderWindowsWhileInVR = settings.RenderWindowsWhileInVR;
        renderSettings.AllowShaderPipelines = settings.AllowShaderPipelines;
        renderSettings.RenderVRSinglePassStereo = settings.SinglePassStereoVR;
        if (settings.RenderPhysicsDebug)
            renderSettings.PhysicsVisualizeSettings.SetAllTrue();

        Engine.Profiler.EnableFrameLogging = settings.EnableProfilerLogging;

        EnsureEmulatedVRStereoPreviewRenderingHooked();
    }
}