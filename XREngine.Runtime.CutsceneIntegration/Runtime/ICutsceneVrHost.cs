using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// The VR state and the headset fade that the cutscene VR camera modes use. The interface only reads VR state and fades the
/// headset image. It gives no access to the eye cameras or to the head pose, so a cutscene cannot write them.
/// <see cref="RuntimeCutsceneVrHost"/> reads the engine state. Tests supply their own host.
/// </summary>
public interface ICutsceneVrHost
{
    /// <summary>True while a VR session runs.</summary>
    bool IsInVR { get; }

    /// <summary>The VR runtime that runs the session.</summary>
    RuntimeVrState.VRRuntime ActiveRuntime { get; }

    /// <summary>The desktop output policy that the user selected for a VR session.</summary>
    EVrMirrorMode MirrorMode { get; }

    /// <summary>True if the VR runtime can fade the headset image to a color. See <see cref="FadeHeadset"/>.</summary>
    bool CanFadeHeadset { get; }

    /// <summary>A monotonic time in seconds. The fade phases use it, because the runtime fade runs on wall time.</summary>
    double TimeSeconds { get; }

    /// <summary>True if a viewport renders a headset eye. A cutscene never claims the camera of such a viewport.</summary>
    /// <param name="viewport">The viewport to check.</param>
    bool IsEyeViewport(XRViewport viewport);

    /// <summary>
    /// Starts a fade of the headset image to black at an opacity, from the current fade state. The VR runtime animates the fade,
    /// so no call is necessary during the fade. A call is valid only when <see cref="CanFadeHeadset"/> is true.
    /// </summary>
    /// <param name="seconds">The fade time in seconds. Zero changes the image at once.</param>
    /// <param name="opacity">The black opacity at the end of the fade: 0 shows the scene, and 1 is full black.</param>
    void FadeHeadset(float seconds, float opacity);
}
