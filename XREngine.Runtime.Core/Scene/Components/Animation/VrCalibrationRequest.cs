using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>Inputs for one VR rig capture. Slots use <see cref="EHumanoidIKTarget"/> indices.</summary>
public sealed class VrCalibrationRequest
{
    public object? Settings { get; init; }
    public VrCalibrationCapture?[] Slots { get; } = new VrCalibrationCapture?[11];
    /// <summary>Captured offsets to restore within the same session, including temporarily absent devices.</summary>
    public Matrix4x4?[] Offsets { get; } = new Matrix4x4?[11];
    /// <summary>Measured headset-to-head displacement from eye geometry, in scaled avatar-root space; null uses bind anatomy.</summary>
    public Matrix4x4? HeadsetToEyes { get; init; }
    public Matrix4x4 LeftGripToWrist { get; init; } = Matrix4x4.Identity;
    public Matrix4x4 RightGripToWrist { get; init; } = Matrix4x4.Identity;
    /// <summary>Require a level head for an intentional standing capture.</summary>
    public bool RequireLevelHead { get; init; } = true;
    /// <summary>Optional player tolerance override in degrees.</summary>
    public float? HeadTiltTolerance { get; init; }
}
