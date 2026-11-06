namespace XREngine.Cutscenes;

/// <summary>
/// Tells how a cutscene shows its shots to a VR user. No mode writes the eye cameras or the head pose,
/// so the user keeps head tracking in each mode. The mode applies only while a VR session runs when the cutscene opens.
/// If the active VR runtime cannot run the mode, the open fails with a diagnostic. The director never uses another mode.
/// </summary>
public enum ECutsceneVrCameraMode
{
    /// <summary>
    /// The shot camera shows only on the desktop viewport of the director. The headset view does not change,
    /// and the VR user stays in the world. This is the default.
    /// </summary>
    SpectatorOnly,

    /// <summary>
    /// The rig origin follows the position and the heading of the shot camera, so the user is head-tracked relative to the shot.
    /// Each camera cut and each jump fades the headset through black, and the rig moves while the headset is black.
    /// It needs a headset fade from the VR runtime and a rig origin on the director.
    /// </summary>
    RigAnchor,

    /// <summary>
    /// The shot renders to a surface in the world. The engine has no world-surface consumer for a camera capture yet,
    /// so an open in VR with this mode fails with a diagnostic.
    /// </summary>
    WorldScreen,
}
