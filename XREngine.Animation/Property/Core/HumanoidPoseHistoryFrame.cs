using System.Numerics;

namespace XREngine.Animation;

/// <summary>
/// Preallocated snapshot of one evaluated humanoid pose (world body transform and canonical
/// normalized muscles) used to seed <see cref="MotionBricksLocomotionMotion"/> context
/// frames when entering procedural locomotion from a traditional animation clip.
/// </summary>
public sealed class HumanoidPoseHistoryFrame
{
    public Vector3 BodyPosition { get; set; }
    public Quaternion BodyRotation { get; set; } = Quaternion.Identity;
    public float[] MuscleValues { get; } = new float[MotionBricksEvaluatedPose.MuscleChannelCount];
    public float TimeSeconds { get; set; }
    public bool HasValidPose { get; set; }

    public void Clear()
    {
        BodyPosition = Vector3.Zero;
        BodyRotation = Quaternion.Identity;
        Array.Clear(MuscleValues);
        TimeSeconds = 0.0f;
        HasValidPose = false;
    }

    public void CopyFrom(HumanoidPoseHistoryFrame source)
    {
        BodyPosition = source.BodyPosition;
        BodyRotation = source.BodyRotation;
        source.MuscleValues.CopyTo(MuscleValues, 0);
        TimeSeconds = source.TimeSeconds;
        HasValidPose = source.HasValidPose;
    }
}
