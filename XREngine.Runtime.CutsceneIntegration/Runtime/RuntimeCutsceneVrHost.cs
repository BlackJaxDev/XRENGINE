using System.Diagnostics;
using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// The engine implementation of <see cref="ICutsceneVrHost"/>. It reads <see cref="RuntimeEngine.VRState"/> and the presentation
/// settings, and it fades the headset through the OpenVR compositor (<see cref="RuntimeOpenVrCompositorServices"/>).
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
        => RuntimeEngine.VRState.IsOpenVRActive && RuntimeOpenVrCompositorServices.Current is not null;

    /// <inheritdoc/>
    public double TimeSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

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
    public void FadeHeadset(float seconds, float opacity)
    {
        IRuntimeOpenVrCompositor compositor = RuntimeOpenVrCompositorServices.Current
            ?? throw new InvalidOperationException("The OpenVR compositor is not registered, so the headset cannot fade.");
        compositor.FadeToColor(seconds, 0.0f, 0.0f, 0.0f, opacity);
    }
}
