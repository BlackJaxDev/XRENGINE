using System.Diagnostics;
using XREngine.Components.Animation;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// The engine implementation of <see cref="ICutsceneVrHost"/>. It reads <see cref="RuntimeEngine.VRState"/> and the presentation
/// settings, fades the headset through the OpenVR compositor (<see cref="RuntimeOpenVrCompositorServices"/>), and publishes a
/// teleport through <see cref="RuntimeVrState.TryPublishLocalPlayerTeleport"/>.
/// OpenXR has no runtime fade in this engine, so <see cref="CanFadeHeadset"/> is false for an OpenXR session.
/// </summary>
public sealed class RuntimeCutsceneVrHost : ICutsceneVrHost
{
    private readonly object _fadeSync = new();
    private long _fadeGeneration;

    private RuntimeCutsceneVrHost()
    {
    }

    /// <summary>The one instance.</summary>
    public static RuntimeCutsceneVrHost Instance { get; } = new();

    /// <inheritdoc/>
    public bool IsInVR => RuntimeEngine.VRState.IsInVR;

    /// <inheritdoc/>
    public RuntimeVrState.VRRuntime ActiveRuntime => RuntimeEngine.VRState.ActiveRuntime;

    /// <inheritdoc/>
    public EVrMirrorMode MirrorMode => RuntimeRenderingHostServices.Presentation.VrMirrorMode;

    /// <inheritdoc/>
    public bool CanFadeHeadset
    {
        get
        {
            RuntimeVrState state = RuntimeEngine.VRState;
            return state.IsInVR
                && state.IsOpenVRActive
                && state.OpenVrRuntimeActiveForRender
                && RuntimeOpenVrCompositorServices.Current is { IsAvailable: true };
        }
    }

    /// <inheritdoc/>
    public double TimeSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <inheritdoc/>
    public ulong RenderFrameId => RuntimeEngine.Rendering.State.RenderFrameId;

    /// <inheritdoc/>
    public TransformBase? HeadTransform => RuntimeEngine.VRState.ViewInformation.HMDNode?.Transform;

    /// <inheritdoc/>
    public bool IsEyeViewport(XRViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        RuntimeVrState state = RuntimeEngine.VRState;
        return ReferenceEquals(viewport, state.LeftEyeViewport)
            || ReferenceEquals(viewport, state.RightEyeViewport)
            || ReferenceEquals(viewport, state.StereoViewport)
            || viewport.ActiveCamera?.StereoEyeLeft is not null;
    }

    /// <inheritdoc/>
    public bool IsEyeCameraTransform(TransformBase transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var view = RuntimeEngine.VRState.ViewInformation;
        return ReferenceEquals(transform, view.LeftEyeCamera?.Transform)
            || ReferenceEquals(transform, view.RightEyeCamera?.Transform);
    }

    /// <inheritdoc/>
    public long FadeGeneration => Interlocked.Read(ref _fadeGeneration);

    /// <inheritdoc/>
    public bool TryFadeHeadset(float seconds, float opacity)
    {
        lock (_fadeSync)
        {
            Interlocked.Increment(ref _fadeGeneration);
            return FadeCore(seconds, opacity);
        }
    }

    /// <inheritdoc/>
    public bool TryFadeHeadsetIfGeneration(long expectedGeneration, float seconds, float opacity)
    {
        // The update thread and the render thread can both fade, so the check and the fade share one lock.
        lock (_fadeSync)
        {
            // A call that does not match changes nothing, so it cannot make a later recorded generation stale.
            if (Interlocked.Read(ref _fadeGeneration) != expectedGeneration)
                return false;

            Interlocked.Increment(ref _fadeGeneration);
            return FadeCore(seconds, opacity);
        }
    }

    private static bool FadeCore(float seconds, float opacity)
        => RuntimeOpenVrCompositorServices.Current is { } compositor
            && compositor.TryFadeToColor(seconds, 0.0f, 0.0f, 0.0f, opacity);

    /// <inheritdoc/>
    public bool TryPublishTeleport()
        => RuntimeEngine.VRState.TryPublishLocalPlayerTeleport();

    /// <inheritdoc/>
    public void InvalidateEyeViewHistory()
    {
        RuntimeVrState state = RuntimeEngine.VRState;
        XRViewport? left = state.LeftEyeViewport;
        XRViewport? right = state.RightEyeViewport;
        XRViewport? stereo = state.StereoViewport;

        // A viewport invalidation also invalidates its active camera. An eye camera with no eye viewport is invalidated alone,
        // so each camera advances its epoch once. No call publishes property change events.
        left?.InvalidateTemporalHistory(false);
        right?.InvalidateTemporalHistory(false);
        stereo?.InvalidateTemporalHistory(false);
        var view = state.ViewInformation;
        InvalidateIfNotViewportCamera(view.LeftEyeCamera, left, right, stereo);
        if (!ReferenceEquals(view.RightEyeCamera, view.LeftEyeCamera))
            InvalidateIfNotViewportCamera(view.RightEyeCamera, left, right, stereo);
    }

    /// <inheritdoc/>
    public void ResetEyeExposure()
    {
        var view = RuntimeEngine.VRState.ViewInformation;
        view.LeftEyeCamera?.ResetAutoExposureHistory();
        if (!ReferenceEquals(view.RightEyeCamera, view.LeftEyeCamera))
            view.RightEyeCamera?.ResetAutoExposureHistory();
    }

    /// <inheritdoc/>
    public bool TryScheduleFadeInAfterBlackHold(float fadeSeconds, int renderFrames, double maximumSeconds, long generation)
    {
        CutsceneVrCloseFadeIn step = new(this, generation, fadeSeconds, renderFrames, maximumSeconds);
        try
        {
            // The render step sees each new render frame. The update step runs at the deadline when render jobs are late.
            // The two delegates and the step allocate once, at the close.
            RuntimeEngine.AddRenderThreadCoroutine(step.RenderStep, "Cutscene.VrFadeInAfterBlackHold", RenderThreadJobKind.Unknown);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }

        try
        {
            RuntimeAnimationHostServices.Current.AddAppThreadCoroutine(step.UpdateStep);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The render step still runs. Only the deadline when render jobs are late is lost.
        }

        return true;
    }

    private static void InvalidateIfNotViewportCamera(XRCamera? camera, XRViewport? left, XRViewport? right, XRViewport? stereo)
    {
        if (camera is null
            || ReferenceEquals(camera, left?.ActiveCamera)
            || ReferenceEquals(camera, right?.ActiveCamera)
            || ReferenceEquals(camera, stereo?.ActiveCamera))
            return;

        camera.InvalidateTemporalHistory(false);
    }
}
