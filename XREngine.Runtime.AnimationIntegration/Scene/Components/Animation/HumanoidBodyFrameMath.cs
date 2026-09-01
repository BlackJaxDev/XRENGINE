using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>Finite, exact-transform guards shared by Body-frame staging and commit.</summary>
internal static class HumanoidBodyFrameMath
{
    public static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    public static bool IsRigid(Matrix4x4 value, float tolerance = 2e-4f)
    {
        if (!IsFinite(value)
            || MathF.Abs(value.M14) > tolerance || MathF.Abs(value.M24) > tolerance
            || MathF.Abs(value.M34) > tolerance || MathF.Abs(value.M44 - 1.0f) > tolerance)
            return false;

        Vector3 x = new(value.M11, value.M12, value.M13);
        Vector3 y = new(value.M21, value.M22, value.M23);
        Vector3 z = new(value.M31, value.M32, value.M33);
        return MathF.Abs(x.LengthSquared() - 1.0f) <= tolerance
            && MathF.Abs(y.LengthSquared() - 1.0f) <= tolerance
            && MathF.Abs(z.LengthSquared() - 1.0f) <= tolerance
            && MathF.Abs(Vector3.Dot(x, y)) <= tolerance
            && MathF.Abs(Vector3.Dot(x, z)) <= tolerance
            && MathF.Abs(Vector3.Dot(y, z)) <= tolerance
            && Vector3.Dot(Vector3.Cross(x, y), z) > 0.0f;
    }

    /// <summary>
    /// Decomposition alone can discard shear. A staged local must reproduce the
    /// complete candidate matrix before any concrete transform is changed.
    /// </summary>
    public static bool TryDecomposeExactTrs(
        Matrix4x4 value, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
    {
        scale = Vector3.One;
        rotation = Quaternion.Identity;
        translation = Vector3.Zero;
        if (!IsFinite(value)
            || !Matrix4x4.Decompose(value, out scale, out rotation, out translation)
            || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 1e-12f)
            return false;

        rotation = Quaternion.Normalize(rotation);
        Matrix4x4 reconstructed = Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
        return ApproximatelyEqual(value, reconstructed);
    }

    public static bool ApproximatelyEqual(Matrix4x4 a, Matrix4x4 b, float tolerance = 2e-5f)
        => Near(a.M11, b.M11, tolerance) && Near(a.M12, b.M12, tolerance) && Near(a.M13, b.M13, tolerance) && Near(a.M14, b.M14, tolerance)
        && Near(a.M21, b.M21, tolerance) && Near(a.M22, b.M22, tolerance) && Near(a.M23, b.M23, tolerance) && Near(a.M24, b.M24, tolerance)
        && Near(a.M31, b.M31, tolerance) && Near(a.M32, b.M32, tolerance) && Near(a.M33, b.M33, tolerance) && Near(a.M34, b.M34, tolerance)
        && Near(a.M41, b.M41, tolerance) && Near(a.M42, b.M42, tolerance) && Near(a.M43, b.M43, tolerance) && Near(a.M44, b.M44, tolerance);

    private static bool Near(float a, float b, float tolerance)
        => float.IsFinite(a) && float.IsFinite(b)
        && MathF.Abs(a - b) <= tolerance * MathF.Max(1.0f, MathF.Max(MathF.Abs(a), MathF.Abs(b)));
}
