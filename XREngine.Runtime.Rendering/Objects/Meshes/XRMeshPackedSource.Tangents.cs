using System.Numerics;

namespace XREngine.Rendering;

public sealed partial class XRMeshPackedSource
{
    /// <summary>
    /// Generates UV0 tangents for a packed triangle mesh that has no source tangents.
    /// Corners share a tangent only when position, normal, UV, and handedness match.
    /// </summary>
    public void GenerateTangentsFromUv0(IReadOnlyList<int> triangleIndices)
    {
        ArgumentNullException.ThrowIfNull(triangleIndices);
        if (HasTangents || !HasNormals || TexCoordSetCount == 0 || VertexCount == 0)
            return;

        Vector4[] generated = new Vector4[_positions.Length];
        if (!MeshTangentGenerator.GenerateFromUv0(
            _positions.AsSpan(0, VertexCount),
            _normals.AsSpan(0, VertexCount),
            _texCoords.AsSpan(0, VertexCount * TexCoordSetCount),
            TexCoordSetCount,
            triangleIndices,
            generated))
        {
            return;
        }

        _tangents = generated;
        HasTangents = true;
    }
}
