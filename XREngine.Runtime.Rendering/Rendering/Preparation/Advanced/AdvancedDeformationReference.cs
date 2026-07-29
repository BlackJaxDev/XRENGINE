using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Deterministic CPU oracle for tests and capture diagnostics. It deliberately
/// mirrors the production blendshape-then-skinning order.
/// </summary>
public static class AdvancedDeformationReference
{
    public static AdvancedReferenceVertex Deform(
        in AdvancedReferenceVertex source,
        ReadOnlySpan<AdvancedReferenceBlendshape> blendshapes,
        ReadOnlySpan<AdvancedReferenceBoneInfluence> influences,
        ReadOnlySpan<Matrix4x4> skinMatrices)
    {
        Vector3 position = source.Position;
        Vector3 normal = source.Normal;
        Vector3 tangent = source.Tangent;
        for (int shapeIndex = 0; shapeIndex < blendshapes.Length; shapeIndex++)
        {
            AdvancedReferenceBlendshape shape = blendshapes[shapeIndex];
            position += shape.PositionDelta * shape.Weight;
            normal += shape.NormalDelta * shape.Weight;
            tangent += shape.TangentDelta * shape.Weight;
        }

        if (influences.IsEmpty)
        {
            return new AdvancedReferenceVertex(
                position,
                NormalizeOrZero(normal),
                NormalizeOrZero(tangent));
        }

        Vector3 skinnedPosition = Vector3.Zero;
        Vector3 skinnedNormal = Vector3.Zero;
        Vector3 skinnedTangent = Vector3.Zero;
        float totalWeight = 0.0f;
        for (int influenceIndex = 0;
             influenceIndex < influences.Length;
             influenceIndex++)
        {
            AdvancedReferenceBoneInfluence influence =
                influences[influenceIndex];
            if (influence.Weight <= 0.0f ||
                influence.BoneIndex >= (uint)skinMatrices.Length)
            {
                continue;
            }

            Matrix4x4 matrix = skinMatrices[(int)influence.BoneIndex];
            skinnedPosition +=
                Vector3.Transform(position, matrix) * influence.Weight;
            skinnedNormal +=
                Vector3.TransformNormal(normal, matrix) * influence.Weight;
            skinnedTangent +=
                Vector3.TransformNormal(tangent, matrix) * influence.Weight;
            totalWeight += influence.Weight;
        }

        if (totalWeight <= 0.0f)
        {
            return new AdvancedReferenceVertex(
                position,
                NormalizeOrZero(normal),
                NormalizeOrZero(tangent));
        }

        float inverseWeight = 1.0f / totalWeight;
        return new AdvancedReferenceVertex(
            skinnedPosition * inverseWeight,
            NormalizeOrZero(skinnedNormal),
            NormalizeOrZero(skinnedTangent));
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
        => value.LengthSquared() > 0.0f
            ? Vector3.Normalize(value)
            : Vector3.Zero;
}
