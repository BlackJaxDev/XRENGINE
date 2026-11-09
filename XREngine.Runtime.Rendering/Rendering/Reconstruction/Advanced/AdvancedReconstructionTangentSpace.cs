using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// CPU reference for inverse-transpose normals and mirrored MikkTSpace frames.
/// </summary>
public static class AdvancedReconstructionTangentSpace
{
    public static bool TryCreate(
        Matrix4x4 world,
        Vector3 localPosition0,
        Vector3 localPosition1,
        Vector3 localPosition2,
        Vector3 localNormal,
        Vector3 localTangent,
        float localHandedness,
        out AdvancedReconstructionTangentFrame frame)
    {
        frame = default;
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            return false;

        Vector3 edge0 = Vector3.TransformNormal(localPosition1 - localPosition0, world);
        Vector3 edge1 = Vector3.TransformNormal(localPosition2 - localPosition0, world);
        if (!TryGeometricNormal(edge0, edge1, out Vector3 geometric))
            return false;

        Matrix4x4 inverseTranspose = Matrix4x4.Transpose(inverse);
        Vector3 shading =
            Vector3.TransformNormal(localNormal, inverseTranspose);
        if (!TryNormalize(shading, out shading))
            shading = geometric;

        Vector3 tangent = BuildOrthogonalTangent(shading, Vector3.TransformNormal(localTangent, world));

        bool mirrored = world.GetDeterminant() < 0.0f;
        float handedness =
            (localHandedness < 0.0f ? -1.0f : 1.0f) *
            (mirrored ? -1.0f : 1.0f);
        Vector3 bitangent =
            Vector3.Cross(shading, tangent) * handedness;
        if (!TryNormalize(bitangent, out bitangent))
            return false;

        frame = new AdvancedReconstructionTangentFrame(
            geometric,
            shading,
            tangent,
            bitangent,
            handedness,
            mirrored);
        return true;
    }

    private static bool TryGeometricNormal(Vector3 edge0, Vector3 edge1, out Vector3 normal)
    {
        normal = default;
        if (!IsFinite(edge0) || !IsFinite(edge1))
            return false;
        float scale0 = MaximumAbsoluteComponent(edge0);
        float scale1 = MaximumAbsoluteComponent(edge1);
        if (scale0 == 0.0f || scale1 == 0.0f)
            return false;

        // Match the shader. World size must not reject a raster-visible triangle.
        Vector3 cross = Vector3.Cross(edge0 / scale0, edge1 / scale1);
        float crossScale = MaximumAbsoluteComponent(cross);
        if (crossScale == 0.0f || !IsFinite(cross))
            return false;
        normal = Vector3.Normalize(cross / crossScale);
        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float MaximumAbsoluteComponent(Vector3 value)
        => MathF.Max(MathF.Max(MathF.Abs(value.X), MathF.Abs(value.Y)), MathF.Abs(value.Z));

    private static Vector3 BuildOrthogonalTangent(Vector3 normal, Vector3 sourceTangent)
    {
        // Match the GPU frame reconstruction. Gram-Schmidt subtraction can
        // leave a parallel rounding residual whose normalized cross is zero.
        Vector3 perpendicular = Vector3.Cross(normal, sourceTangent);
        float perpendicularLengthSquared = perpendicular.LengthSquared();
        float tangentLengthSquared = sourceTangent.LengthSquared();
        if (!float.IsFinite(tangentLengthSquared) || !float.IsFinite(perpendicularLengthSquared) ||
            perpendicularLengthSquared <= MathF.Max(1.0e-20f, tangentLengthSquared * 1.0e-12f))
            return BuildFallbackTangent(normal);
        perpendicular /= MathF.Sqrt(perpendicularLengthSquared);
        return TryNormalize(Vector3.Cross(perpendicular, normal), out Vector3 tangent)
            ? tangent : BuildFallbackTangent(normal);
    }

    private static Vector3 BuildFallbackTangent(Vector3 normal)
    {
        Vector3 axis = MathF.Abs(normal.Z) < 0.999f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        Vector3 tangent = Vector3.Cross(axis, normal);
        return Vector3.Normalize(tangent);
    }

    private static bool TryNormalize(
        Vector3 value,
        out Vector3 normalized)
    {
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-20f)
        {
            normalized = default;
            return false;
        }

        normalized = value / MathF.Sqrt(lengthSquared);
        return true;
    }
}
