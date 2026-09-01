using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Components.Animation;

namespace XREngine.Animation.Importers;

/// <summary>
/// Receives the authored, pre-Loop-Pose muscle sample used by root projection.
/// Unity computes feet-based projection before distributing the pose seam, so
/// evaluators retain this sample separately from the final corrected pose.
/// </summary>
public interface IImportedHumanoidProjectionPoseSink
{
    void SetImportedHumanoidProjectionMuscle(
        EHumanoidValue value,
        float amount,
        bool flipImportedMuscleZ);

    /// <summary>
    /// Receives one complete authored Body-relative IK goal position before
    /// Loop Pose correction. Feet-based root projection consumes the foot
    /// goals directly; hand goals are intentionally ignored by that policy.
    /// </summary>
    void SetImportedHumanoidProjectionGoalPosition(
        ELimbEndEffector goal,
        Vector3 position);
}
