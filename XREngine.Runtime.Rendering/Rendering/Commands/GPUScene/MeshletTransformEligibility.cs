using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Classifies model transforms for the production meshlet cone-culling path.
/// </summary>
internal static class MeshletTransformEligibility
{
    /// <summary>
    /// Returns whether the transform preserves an orthogonal, positive-handed
    /// basis with one uniform finite scale.
    /// </summary>
    internal static bool HasUniformPositiveScale(in Matrix4x4 matrix)
    {
        Vector3 x = new(matrix.M11, matrix.M12, matrix.M13);
        Vector3 y = new(matrix.M21, matrix.M22, matrix.M23);
        Vector3 z = new(matrix.M31, matrix.M32, matrix.M33);
        float sx = x.LengthSquared();
        float sy = y.LengthSquared();
        float sz = z.LengthSquared();
        const float minimumAxisLengthSquared = 1.0e-12f;
        const float relativeTolerance = 1.0e-4f;
        if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(sz) ||
            sx <= minimumAxisLengthSquared || sy <= minimumAxisLengthSquared || sz <= minimumAxisLengthSquared)
        {
            return false;
        }

        float maxScale = Math.Max(sx, Math.Max(sy, sz));
        float tolerance = maxScale * relativeTolerance;
        if (Math.Abs(sx - sy) > tolerance || Math.Abs(sx - sz) > tolerance)
            return false;

        // Equal axis lengths also admit shear. Cone axes can use the model
        // matrix directly only when its basis remains orthogonal.
        if (Math.Abs(Vector3.Dot(x, y)) > tolerance ||
            Math.Abs(Vector3.Dot(x, z)) > tolerance ||
            Math.Abs(Vector3.Dot(y, z)) > tolerance)
        {
            return false;
        }

        return Vector3.Dot(Vector3.Cross(x, y), z) > 0.0f;
    }
}
