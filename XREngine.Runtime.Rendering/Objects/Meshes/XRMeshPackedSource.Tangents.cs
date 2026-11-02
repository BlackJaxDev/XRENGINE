using System.Numerics;

namespace XREngine.Rendering;

public sealed partial class XRMeshPackedSource
{
    private readonly record struct TangentCornerKey(Vector3 Position, Vector3 Normal, Vector2 Uv, int Handedness);

    /// <summary>
    /// Generates UV0 tangents for a packed triangle mesh that has no source tangents.
    /// Corners share a tangent only when position, normal, UV, and handedness match.
    /// </summary>
    public void GenerateTangentsFromUv0(IReadOnlyList<int> triangleIndices)
    {
        ArgumentNullException.ThrowIfNull(triangleIndices);
        if (HasTangents || !HasNormals || TexCoordSetCount == 0 || VertexCount == 0)
            return;
        if (triangleIndices.Count % 3 != 0)
            throw new ArgumentException("Triangle indices must have a multiple of three entries.", nameof(triangleIndices));

        Dictionary<TangentCornerKey, Vector3> groups = new(VertexCount);
        float[] signedWeights = new float[VertexCount];

        for (int triangle = 0; triangle < triangleIndices.Count; triangle += 3)
        {
            int i0 = triangleIndices[triangle];
            int i1 = triangleIndices[triangle + 1];
            int i2 = triangleIndices[triangle + 2];
            if ((uint)i0 >= (uint)VertexCount || (uint)i1 >= (uint)VertexCount || (uint)i2 >= (uint)VertexCount)
                throw new ArgumentOutOfRangeException(nameof(triangleIndices), "A triangle index is outside the packed source.");

            Vector3 p0 = _positions[i0];
            Vector3 p1 = _positions[i1];
            Vector3 p2 = _positions[i2];
            Vector3 edge1 = p1 - p0;
            Vector3 edge2 = p2 - p0;
            if (Vector3.Cross(edge1, edge2).LengthSquared() == 0.0f)
                continue;

            Vector2 uv0 = GetTexCoord(i0, 0);
            Vector2 uv1 = GetTexCoord(i1, 0);
            Vector2 uv2 = GetTexCoord(i2, 0);
            Vector2 delta1 = uv1 - uv0;
            Vector2 delta2 = uv2 - uv0;
            float determinant = delta1.X * delta2.Y - delta1.Y * delta2.X;
            if (!float.IsFinite(determinant) || MathF.Abs(determinant) <= 1.0e-20f)
                continue;

            float inverseDeterminant = 1.0f / determinant;
            Vector3 triangleTangent = (edge1 * delta2.Y - edge2 * delta1.Y) * inverseDeterminant;
            Vector3 triangleBitangent = (edge2 * delta1.X - edge1 * delta2.X) * inverseDeterminant;
            if (!IsFinite(triangleTangent) || !IsFinite(triangleBitangent))
                continue;

            AddCorner(i0, edge1, edge2, triangleTangent, triangleBitangent, groups, signedWeights);
            AddCorner(i1, p2 - p1, p0 - p1, triangleTangent, triangleBitangent, groups, signedWeights);
            AddCorner(i2, p0 - p2, p1 - p2, triangleTangent, triangleBitangent, groups, signedWeights);
        }

        if (groups.Count == 0)
            return;

        Vector4[] generated = new Vector4[_positions.Length];
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            Vector3 normal = _normals[vertex];
            if (!IsFinite(normal) || !float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() <= 1.0e-20f)
            {
                generated[vertex] = new Vector4(Vector3.UnitX, 1.0f);
                continue;
            }

            normal = Vector3.Normalize(normal);
            int handedness = signedWeights[vertex] < 0.0f ? -1 : 1;
            TangentCornerKey key = new(_positions[vertex], _normals[vertex], GetTexCoord(vertex, 0), handedness);
            Vector3 tangent = groups.TryGetValue(key, out Vector3 sum)
                ? sum - normal * Vector3.Dot(normal, sum)
                : Vector3.Zero;
            if (!IsFinite(tangent) || tangent.LengthSquared() <= 1.0e-20f)
                tangent = FallbackTangent(normal);
            else
                tangent = Vector3.Normalize(tangent);
            generated[vertex] = new Vector4(tangent, handedness);
        }

        _tangents = generated;
        HasTangents = true;
    }

    private void AddCorner(
        int index,
        Vector3 firstEdge,
        Vector3 secondEdge,
        Vector3 triangleTangent,
        Vector3 triangleBitangent,
        Dictionary<TangentCornerKey, Vector3> groups,
        float[] signedWeights)
    {
        Vector3 normal = _normals[index];
        if (!IsFinite(normal) || !float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() <= 1.0e-20f)
            return;
        normal = Vector3.Normalize(normal);
        Vector3 tangent = triangleTangent - normal * Vector3.Dot(normal, triangleTangent);
        if (tangent.LengthSquared() <= 1.0e-20f)
            return;
        tangent = Vector3.Normalize(tangent);
        int handedness = Vector3.Dot(Vector3.Cross(normal, tangent), triangleBitangent) < 0.0f ? -1 : 1;
        float weight = MathF.Atan2(Vector3.Cross(firstEdge, secondEdge).Length(),
            Vector3.Dot(firstEdge, secondEdge));
        if (!float.IsFinite(weight) || weight <= 0.0f)
            return;

        TangentCornerKey key = new(_positions[index], _normals[index], GetTexCoord(index, 0), handedness);
        groups.TryGetValue(key, out Vector3 previous);
        groups[key] = previous + tangent * weight;
        signedWeights[index] += handedness * weight;
    }

    private static Vector3 FallbackTangent(Vector3 normal)
    {
        Vector3 axis = MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(axis, normal));
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
