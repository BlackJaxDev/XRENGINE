using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Reusable sink for authored humanoid muscle samples used by endpoint-only pose probes.
/// </summary>
internal sealed class UnityHumanoidMuscleSampleBuffer : IUnityHumanoidProjectionPoseSink
{
    private const int MuscleCount = (int)EHumanoidValue.RightHandThumb3Stretched + 1;

    public float[] Values { get; } = new float[MuscleCount];

    public void Clear()
        => Array.Clear(Values);

    public void SetUnityHumanoidProjectionMuscle(
        EHumanoidValue value,
        float amount,
        bool flipImportedMuscleZ)
    {
        int index = (int)value;
        if ((uint)index >= (uint)Values.Length)
            return;

        Values[index] = HumanoidComponent.ConvertImportedHumanoidAmount(
            value,
            amount,
            flipImportedMuscleZ);
    }
}
