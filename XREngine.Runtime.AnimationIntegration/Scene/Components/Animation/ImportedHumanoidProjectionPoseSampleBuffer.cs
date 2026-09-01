using System.Numerics;
using XREngine.Animation.IK;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Reusable sink for the authored pre-Loop-Pose data used by humanoid root
/// projection and endpoint probes.
/// </summary>
internal sealed class ImportedHumanoidProjectionPoseSampleBuffer : IImportedHumanoidProjectionPoseSink
{
    private const int MuscleCount = (int)EHumanoidValue.RightHandThumb3Stretched + 1;

    public float[] MuscleValues { get; } = new float[MuscleCount];
    public ImportedHumanoidProjectionFootGoals FootGoals;

    public void Clear()
    {
        Array.Clear(MuscleValues);
        FootGoals.Clear();
    }

    public void SetImportedHumanoidProjectionMuscle(
        EHumanoidValue value,
        float amount,
        bool flipImportedMuscleZ)
    {
        int index = (int)value;
        if ((uint)index >= (uint)MuscleValues.Length)
            return;

        MuscleValues[index] = HumanoidComponent.ConvertImportedHumanoidAmount(
            value,
            amount,
            flipImportedMuscleZ);
    }

    public void SetImportedHumanoidProjectionGoalPosition(
        ELimbEndEffector goal,
        Vector3 position)
        => FootGoals.Set(goal, position);
}
