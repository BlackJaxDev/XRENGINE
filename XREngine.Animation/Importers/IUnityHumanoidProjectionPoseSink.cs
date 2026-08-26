using XREngine.Components.Animation;

namespace XREngine.Animation.Importers;

/// <summary>
/// Receives the authored, pre-Loop-Pose muscle sample used by root projection.
/// Unity computes feet-based projection before distributing the pose seam, so
/// evaluators retain this sample separately from the final corrected pose.
/// </summary>
public interface IUnityHumanoidProjectionPoseSink
{
    void SetUnityHumanoidProjectionMuscle(
        EHumanoidValue value,
        float amount,
        bool flipImportedMuscleZ);
}
