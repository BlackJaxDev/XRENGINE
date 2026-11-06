using System.Diagnostics;
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
    public bool TryFadeHeadset(float seconds, float opacity)
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
    public bool TryScheduleFadeInAfterBlackHold(float fadeSeconds, int renderFrames, double maximumSeconds)
    {
        ulong startFrame = RenderFrameId;
        double deadline = TimeSeconds + maximumSeconds;
        try
        {
            // The step runs on the render thread once in each pass, so it sees each new render frame. It allocates once, at the close.
            RuntimeEngine.AddRenderThreadCoroutine(
                () =>
                {
                    if (RenderFrameId - startFrame < (ulong)renderFrames && TimeSeconds < deadline)
                        return false;

                    ResetEyeExposure();
                    TryFadeHeadset(fadeSeconds, 0.0f);
                    return true;
                },
                "Cutscene.VrFadeInAfterBlackHold",
                RenderThreadJobKind.Unknown);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
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
