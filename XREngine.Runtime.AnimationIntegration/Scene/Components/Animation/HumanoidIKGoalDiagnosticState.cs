using System.Numerics;
using XREngine.Animation.IK;

namespace XREngine.Components.Animation;

/// <summary>
/// Allocation-free diagnostic snapshot separating authored goal data, body-frame conversion,
/// optional post-pose contact compensation, and avatar feet-spacing correction.
/// </summary>
public readonly record struct HumanoidIKGoalDiagnosticState(
    ELimbEndEffector Goal,
    Vector3 AuthoredBodyLocalPosition,
    Quaternion AuthoredBodyLocalRotation,
    Vector3 BodyFrameWorldPosition,
    Quaternion BodyFrameWorldRotation,
    Vector3 ContactCompensationOffset,
    Vector3 FeetSpacingCompensationOffset,
    Vector3 FinalWorldPosition,
    Quaternion FinalWorldRotation,
    EHumanoidIKGoalApplicationStatus Status)
{
    public static HumanoidIKGoalDiagnosticState Empty(ELimbEndEffector goal)
        => new(
            goal,
            Vector3.Zero,
            Quaternion.Identity,
            Vector3.Zero,
            Quaternion.Identity,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Quaternion.Identity,
            EHumanoidIKGoalApplicationStatus.None);
}
