using System.Numerics;
using XREngine.Components.Animation;

namespace XREngine.Animation;

/// <summary>
/// Reusable per-motion pose buffer filled by an <see cref="IMotionBricksLocomotionDriver"/>
/// on each animation evaluation tick without heap allocations.
/// </summary>
public sealed class MotionBricksEvaluatedPose
{
    public const int MuscleChannelCount = (int)EHumanoidValue.RightHandThumb3Stretched + 1;

    /// <summary>
    /// Normalized humanoid muscle values indexed by <see cref="EHumanoidValue"/>.
    /// </summary>
    public float[] MuscleValues { get; } = new float[MuscleChannelCount];

    /// <summary>
    /// Reference Body position at the start of the active locomotion epoch.
    /// </summary>
    public Vector3 CanonicalBodyPosition { get; set; } = new(0.0f, 1.0f, 0.0f);

    /// <summary>
    /// Reference Body orientation at the start of the active locomotion epoch.
    /// </summary>
    public Quaternion CanonicalBodyRotation { get; set; } = Quaternion.Identity;

    /// <summary>
    /// Current evaluated Body position in humanoid avatar meters.
    /// </summary>
    public Vector3 CurrentBodyPosition { get; set; } = new(0.0f, 1.0f, 0.0f);

    /// <summary>
    /// Current evaluated Body orientation in humanoid coordinate space.
    /// </summary>
    public Quaternion CurrentBodyRotation { get; set; } = Quaternion.Identity;

    /// <summary>
    /// Predicted foot-contact probabilities <c>(LeftFoot, LeftToeBase, RightFoot, RightToeBase)</c>
    /// clamped to <c>[0, 1]</c>.
    /// </summary>
    public Vector4 FootContacts { get; set; } = Vector4.Zero;

    /// <summary>
    /// True once the driver has produced at least one valid finite pose sample.
    /// </summary>
    public bool HasValidPose { get; set; }

    public void Clear()
    {
        Array.Clear(MuscleValues);
        CanonicalBodyPosition = new Vector3(0.0f, 1.0f, 0.0f);
        CanonicalBodyRotation = Quaternion.Identity;
        CurrentBodyPosition = new Vector3(0.0f, 1.0f, 0.0f);
        CurrentBodyRotation = Quaternion.Identity;
        FootContacts = Vector4.Zero;
        HasValidPose = false;
    }
}
