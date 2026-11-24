using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Generates per-vertex tangents from positions, normals, UV0, and triangle indices.
/// </summary>
/// <remarks>
/// Each corner adds its UV tangent, weighted by the corner angle, to a group. Corners share a tangent only when
/// position, normal, UV, and handedness match, so UV seams and mirrored UVs keep separate tangents. The result is
/// orthogonal to the normal and has the bitangent sign in <c>W</c>. A vertex without a usable UV tangent gets a
/// tangent that is perpendicular to its normal.
/// </remarks>
internal static class MeshTangentGenerator
{
    private readonly record struct CornerKey(Vector3 Position, Vector3 Normal, Vector2 Uv, int Handedness);

    /// <summary>Writes one tangent for each vertex into <paramref name="destination"/>.</summary>
    /// <param name="positions">The vertex positions.</param>
    /// <param name="normals">The vertex normals, one for each position.</param>
    /// <param name="texCoords">The texture coordinates. UV0 of a vertex is at <c>vertex * texCoordStride</c>.</param>
    /// <param name="texCoordStride">The number of texture coordinate sets for each vertex.</param>
    /// <param name="triangleIndices">The triangle list indices.</param>
    /// <param name="destination">The tangents. It must hold at least one value for each position.</param>
    /// <returns>False when no triangle has a usable UV tangent. <paramref name="destination"/> is then not changed.</returns>
    public static bool GenerateFromUv0(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> texCoords,
        int texCoordStride,
        IReadOnlyList<int> triangleIndices,
        Span<Vector4> destination)
    {
        ArgumentNullException.ThrowIfNull(triangleIndices);
        int vertexCount = positions.Length;
        if (normals.Length < vertexCount || texCoordStride <= 0 || texCoords.Length < vertexCount * texCoordStride || destination.Length < vertexCount)
            throw new ArgumentException("The normals, texture coordinates, and destination must cover every position.");
        if (triangleIndices.Count % 3 != 0)
            throw new ArgumentException("Triangle indices must have a multiple of three entries.", nameof(triangleIndices));

        Dictionary<CornerKey, Vector3> groups = new(vertexCount);
        float[] signedWeights = new float[vertexCount];

        for (int triangle = 0; triangle < triangleIndices.Count; triangle += 3)
        {
            int i0 = triangleIndices[triangle];
            int i1 = triangleIndices[triangle + 1];
            int i2 = triangleIndices[triangle + 2];
            if ((uint)i0 >= (uint)vertexCount || (uint)i1 >= (uint)vertexCount || (uint)i2 >= (uint)vertexCount)
                throw new ArgumentOutOfRangeException(nameof(triangleIndices), "A triangle index is outside the vertex range.");

            Vector3 p0 = positions[i0];
            Vector3 p1 = positions[i1];
            Vector3 p2 = positions[i2];
            Vector3 edge1 = p1 - p0;
            Vector3 edge2 = p2 - p0;
            if (Vector3.Cross(edge1, edge2).LengthSquared() == 0.0f)
                continue;

            Vector2 uv0 = texCoords[i0 * texCoordStride];
            Vector2 uv1 = texCoords[i1 * texCoordStride];
            Vector2 uv2 = texCoords[i2 * texCoordStride];
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

            AddCorner(i0, edge1, edge2, triangleTangent, triangleBitangent, positions, normals, texCoords, texCoordStride, groups, signedWeights);
            AddCorner(i1, p2 - p1, p0 - p1, triangleTangent, triangleBitangent, positions, normals, texCoords, texCoordStride, groups, signedWeights);
            AddCorner(i2, p0 - p2, p1 - p2, triangleTangent, triangleBitangent, positions, normals, texCoords, texCoordStride, groups, signedWeights);
        }

        if (groups.Count == 0)
            return false;

        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            Vector3 normal = normals[vertex];
            if (!IsUsableNormal(normal))
            {
                destination[vertex] = new Vector4(Vector3.UnitX, 1.0f);
                continue;
            }

            Vector3 unitNormal = Vector3.Normalize(normal);
            int handedness = signedWeights[vertex] < 0.0f ? -1 : 1;
            CornerKey key = new(positions[vertex], normal, texCoords[vertex * texCoordStride], handedness);
            Vector3 tangent = groups.TryGetValue(key, out Vector3 sum)
                ? sum - unitNormal * Vector3.Dot(unitNormal, sum)
                : Vector3.Zero;
            tangent = !IsFinite(tangent) || tangent.LengthSquared() <= 1.0e-20f
                ? FallbackTangent(unitNormal)
                : Vector3.Normalize(tangent);
            destination[vertex] = new Vector4(tangent, handedness);
        }

        return true;
    }

    private static void AddCorner(
        int index,
        Vector3 firstEdge,
        Vector3 secondEdge,
        Vector3 triangleTangent,
        Vector3 triangleBitangent,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> texCoords,
        int texCoordStride,
        Dictionary<CornerKey, Vector3> groups,
        float[] signedWeights)
    {
        Vector3 normal = normals[index];
        if (!IsUsableNormal(normal))
            return;

        Vector3 unitNormal = Vector3.Normalize(normal);
        Vector3 tangent = triangleTangent - unitNormal * Vector3.Dot(unitNormal, triangleTangent);
        if (tangent.LengthSquared() <= 1.0e-20f)
            return;

        tangent = Vector3.Normalize(tangent);
        int handedness = Vector3.Dot(Vector3.Cross(unitNormal, tangent), triangleBitangent) < 0.0f ? -1 : 1;
        float weight = MathF.Atan2(Vector3.Cross(firstEdge, secondEdge).Length(), Vector3.Dot(firstEdge, secondEdge));
        if (!float.IsFinite(weight) || weight <= 0.0f)
            return;

        CornerKey key = new(positions[index], normal, texCoords[index * texCoordStride], handedness);
        groups.TryGetValue(key, out Vector3 previous);
        groups[key] = previous + tangent * weight;
        signedWeights[index] += handedness * weight;
    }

    private static bool IsUsableNormal(Vector3 normal)
        => IsFinite(normal) && float.IsFinite(normal.LengthSquared()) && normal.LengthSquared() > 1.0e-20f;

    private static Vector3 FallbackTangent(Vector3 normal)
    {
        Vector3 axis = MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(axis, normal));
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
