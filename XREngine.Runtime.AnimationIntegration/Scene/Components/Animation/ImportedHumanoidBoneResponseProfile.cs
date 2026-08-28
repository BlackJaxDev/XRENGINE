using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Ordered Unity muscle responses that compose one avatar bone's local pose.
/// Responses retain Unity HumanTrait order, including parent-twist propagation.
/// </summary>
public sealed class ImportedHumanoidBoneResponseProfile
{
    public string BoneName { get; set; } = string.Empty;
    public ImportedHumanoidMuscleResponse[] Responses { get; set; } = [];

    public Quaternion Evaluate(ReadOnlySpan<float> muscles, float muscleInputScale)
    {
        Quaternion result = Quaternion.Identity;
        foreach (ImportedHumanoidMuscleResponse response in Responses)
        {
            int index = (int)response.Muscle;
            if ((uint)index >= (uint)muscles.Length)
                continue;

            Quaternion responseRotation = response.Evaluate(muscles[index] * muscleInputScale);
            result = Quaternion.Normalize(result * responseRotation);
        }

        return result;
    }
}
