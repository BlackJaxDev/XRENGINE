using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private readonly object _tangentGenerationLock = new();

    /// <summary>
    /// Adds a <c>Tangent</c> buffer with UV0 tangents when the mesh has none.
    /// </summary>
    /// <remarks>
    /// The mesh must have triangles, normals, and UV0. The mesh renderers react to the new buffer and bind it by name.
    /// Vertices without a usable UV tangent get a tangent that is perpendicular to the normal.
    /// </remarks>
    /// <returns>True when the method added a tangent buffer.</returns>
    public bool GenerateTangentsFromUv0()
    {
        // Renderers that share the mesh can call this at the same time. Only one adds the buffer.
        lock (_tangentGenerationLock)
            return GenerateTangentsFromUv0Locked();
    }

    private bool GenerateTangentsFromUv0Locked()
    {
        int vertexCount = VertexCount;
        List<IndexTriangle>? triangles = _triangles;
        if (HasTangents || !HasNormals || TexCoordCount == 0 || vertexCount == 0 || triangles is null || triangles.Count == 0)
            return false;

        Vector3[] positions = new Vector3[vertexCount];
        Vector3[] normals = new Vector3[vertexCount];
        Vector2[] texCoords = new Vector2[vertexCount];
        for (uint vertex = 0; vertex < (uint)vertexCount; vertex++)
        {
            positions[vertex] = GetPosition(vertex);
            normals[vertex] = GetNormal(vertex);
            texCoords[vertex] = GetTexCoord(vertex, 0);
        }

        int[] indices = new int[triangles.Count * 3];
        for (int triangle = 0; triangle < triangles.Count; triangle++)
        {
            IndexTriangle source = triangles[triangle];
            indices[triangle * 3] = source.Point0;
            indices[triangle * 3 + 1] = source.Point1;
            indices[triangle * 3 + 2] = source.Point2;
        }

        Vector4[] tangents = new Vector4[vertexCount];
        if (!MeshTangentGenerator.GenerateFromUv0(positions, normals, texCoords, 1, indices, tangents))
            return false;

        XRDataBuffer buffer = new(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, false);
        buffer.SetDataRaw<Vector4>(tangents);
        Buffers.Add(ECommonBufferType.Tangent.ToString(), buffer);
        return true;
    }
}
