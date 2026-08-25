using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Measured zero-to-endpoint rotation response for one Unity humanoid muscle on
/// one avatar bone, converted into XRENGINE local-bone coordinates.
/// </summary>
public sealed class UnityHumanoidMuscleResponse
{
    public EHumanoidValue Muscle { get; set; }
    public Quaternion NegativeRotation { get; set; } = Quaternion.Identity;
    public Quaternion PositiveRotation { get; set; } = Quaternion.Identity;

    public Quaternion Evaluate(float amount)
    {
        if (!float.IsFinite(amount) || MathF.Abs(amount) <= 1e-7f)
            return Quaternion.Identity;

        float normalizedAmount = Math.Clamp(amount, -1.0f, 1.0f);
        Quaternion endpoint = normalizedAmount >= 0.0f ? PositiveRotation : NegativeRotation;
        return Quaternion.Normalize(Quaternion.Slerp(
            Quaternion.Identity,
            endpoint,
            MathF.Abs(normalizedAmount)));
    }
}
