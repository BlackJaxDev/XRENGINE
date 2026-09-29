using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Local bone translation, unit rotation and positive scale in the engine's row-vector convention.</summary>
public readonly record struct BrowserBoneTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public static BrowserBoneTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    internal void Validate()
    {
        if (!Finite(Translation) || !Finite(Scale) || Scale.X is < 0.001f or > 100 ||
            Scale.Y is < 0.001f or > 100 || Scale.Z is < 0.001f or > 100 ||
            !float.IsFinite(Rotation.LengthSquared()) || MathF.Abs(Rotation.LengthSquared() - 1) > 0.001f ||
            Translation.LengthSquared() > 100_000_000)
            throw new ArgumentException("Bone TRS requires bounded finite translation, positive scale and a unit quaternion.");
    }

    internal Matrix4x4 ToMatrix() => Matrix4x4.CreateScale(Scale) *
        Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);

    internal static BrowserBoneTransform Interpolate(in BrowserBoneTransform from, in BrowserBoneTransform to, float weight) =>
        new(Vector3.Lerp(from.Translation, to.Translation, weight),
            Quaternion.Normalize(Quaternion.Slerp(from.Rotation, to.Rotation, weight)),
            Vector3.Lerp(from.Scale, to.Scale, weight));

    internal static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static bool Finite(in Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
