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
    public void ResetEyeViewHistory()
    {
        RuntimeVrState state = RuntimeEngine.VRState;

        // The history goes first, so the exposure reset records the new epoch of each camera.
        state.LeftEyeViewport?.InvalidateTemporalHistory(false);
        state.RightEyeViewport?.InvalidateTemporalHistory(false);
        state.StereoViewport?.InvalidateTemporalHistory(false);
        var view = state.ViewInformation;
        view.LeftEyeCamera?.InvalidateTemporalHistory();
        view.RightEyeCamera?.InvalidateTemporalHistory();
        view.LeftEyeCamera?.ResetAutoExposureHistory();
        if (!ReferenceEquals(view.RightEyeCamera, view.LeftEyeCamera))
            view.RightEyeCamera?.ResetAutoExposureHistory();
    }
}
