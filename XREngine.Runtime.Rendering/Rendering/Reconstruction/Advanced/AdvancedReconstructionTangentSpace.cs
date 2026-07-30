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

        Vector3 world0 = Vector3.Transform(localPosition0, world);
        Vector3 world1 = Vector3.Transform(localPosition1, world);
        Vector3 world2 = Vector3.Transform(localPosition2, world);
        Vector3 edge0 = world1 - world0;
        Vector3 edge1 = world2 - world0;
        Vector3 geometric = Vector3.Cross(edge0, edge1);
        if (!TryNormalize(geometric, out geometric))
            return false;

        Matrix4x4 inverseTranspose = Matrix4x4.Transpose(inverse);
        Vector3 shading =
            Vector3.TransformNormal(localNormal, inverseTranspose);
        if (!TryNormalize(shading, out shading))
            shading = geometric;

        Vector3 tangent = Vector3.TransformNormal(localTangent, world);
        tangent -= shading * Vector3.Dot(shading, tangent);
        if (!TryNormalize(tangent, out tangent))
            tangent = BuildFallbackTangent(shading);

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
