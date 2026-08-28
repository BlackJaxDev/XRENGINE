using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Measured zero-to-endpoint rotation response for one Unity humanoid muscle on
/// one avatar bone, converted into XRENGINE local-bone coordinates.
/// </summary>
public sealed class ImportedHumanoidMuscleResponse
{
    public EHumanoidValue Muscle { get; set; }
    public Quaternion NegativeRotation { get; set; } = Quaternion.Identity;
    public Quaternion PositiveRotation { get; set; } = Quaternion.Identity;

    public Quaternion Evaluate(float amount)
    {
        if (!float.IsFinite(amount) || MathF.Abs(amount) <= 1e-7f)
            return Quaternion.Identity;

        Quaternion endpoint = amount >= 0.0f ? PositiveRotation : NegativeRotation;
        return ScaleShortestRotation(endpoint, MathF.Abs(amount));
    }

    /// <summary>
    /// Scales the shortest identity-to-endpoint arc without clamping the factor.
    /// Imported Unity muscle curves can legitimately overshoot their nominal
    /// [-1, 1] authoring range, so endpoint calibration must extrapolate too.
    /// </summary>
    internal static Quaternion ScaleShortestRotation(Quaternion rotation, float factor)
    {
        rotation = Quaternion.Normalize(rotation);
        if (rotation.W < 0.0f)
            rotation = new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);

        float halfAngle = MathF.Acos(Math.Clamp(rotation.W, -1.0f, 1.0f));
        float sinHalfAngle = MathF.Sin(halfAngle);
        if (MathF.Abs(sinHalfAngle) <= 1.0e-7f)
            return Quaternion.Identity;

        float scaledHalfAngle = halfAngle * factor;
        float axisScale = MathF.Sin(scaledHalfAngle) / sinHalfAngle;
        return Quaternion.Normalize(new Quaternion(
            rotation.X * axisScale,
            rotation.Y * axisScale,
            rotation.Z * axisScale,
            MathF.Cos(scaledHalfAngle)));
    }
}
