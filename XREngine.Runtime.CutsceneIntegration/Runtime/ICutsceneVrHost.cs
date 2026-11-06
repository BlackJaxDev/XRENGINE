using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// The VR state, the headset fade, and the rig-move notifications that the cutscene VR camera modes use.
/// The interface gives no write path to the eye cameras or to the head pose. <see cref="HeadTransform"/> is read only to check
/// the rig origin. <see cref="ResetEyeViewHistory"/> changes only render history, not a pose.
/// <see cref="RuntimeCutsceneVrHost"/> reads the engine state. Tests supply their own host.
/// No member throws for a lost VR session: a failed fade returns false.
/// </summary>
public interface ICutsceneVrHost
{
    /// <summary>True while a VR session runs.</summary>
    bool IsInVR { get; }

    /// <summary>The VR runtime that runs the session.</summary>
    RuntimeVrState.VRRuntime ActiveRuntime { get; }

    /// <summary>The desktop output policy that the user selected for a VR session.</summary>
    EVrMirrorMode MirrorMode { get; }

    /// <summary>
    /// True if a VR session runs and its runtime can fade the headset image now. It becomes false when the session or the
    /// runtime compositor stops. See <see cref="TryFadeHeadset"/>.
    /// </summary>
    bool CanFadeHeadset { get; }

    /// <summary>A monotonic time in seconds. The fade phases use it, because the runtime fade runs on wall time.</summary>
    double TimeSeconds { get; }

    /// <summary>A counter of render frames. The router holds the headset black for some render frames after a rig move.</summary>
    ulong RenderFrameId { get; }

    /// <summary>The transform of the tracked head (the HMD node), or null if the VR session published none.</summary>
    TransformBase? HeadTransform { get; }

    /// <summary>True if a viewport renders a headset eye. A cutscene never claims the camera of such a viewport.</summary>
    /// <param name="viewport">The viewport to check.</param>
    bool IsEyeViewport(XRViewport viewport);

    /// <summary>True if a transform is the transform of a headset eye camera.</summary>
    /// <param name="transform">The transform to check.</param>
    bool IsEyeCameraTransform(TransformBase transform);

    /// <summary>
    /// Starts a fade of the headset image to black at an opacity, from the current fade state. The VR runtime animates the fade,
    /// so no call is necessary during the fade. The method does not throw.
    /// </summary>
    /// <param name="seconds">The fade time in seconds. Zero changes the image at once.</param>
    /// <param name="opacity">The black opacity at the end of the fade: 0 shows the scene, and 1 is full black.</param>
    /// <returns>False if the runtime did not accept the fade, for example because the session stopped.</returns>
    bool TryFadeHeadset(float seconds, float opacity);

    /// <summary>
    /// Publishes a teleport of the local player, so that systems that filter the tracked pose (spectator follow, calibration,
    /// tracker binding) reset instead of seeing a jump as motion.
    /// </summary>
    /// <returns>False if the application host installed no publisher.</returns>
    bool TryPublishTeleport();

    /// <summary>
    /// Discards the temporal render history and the auto exposure adaptation of the headset eye views after a rig move.
    /// It changes no pose. The exposure reset follows the epoch rule of <see cref="XRCamera.ResetAutoExposureHistory"/>.
    /// </summary>
    void ResetEyeViewHistory();
}
