namespace XREngine.Input;

/// <summary>A coordinate or ownership cut that invalidates interpolation and view history.</summary>
public enum EVrPoseDiscontinuity
{
    Teleport, SnapTurn, Recenter, AvatarReplacement, SessionGeneration, CameraMode,
}
