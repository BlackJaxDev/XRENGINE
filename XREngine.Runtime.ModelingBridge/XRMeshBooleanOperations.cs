using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Modeling;

namespace XREngine.Rendering.Modeling;

public static class XRMeshBooleanOperations
{
    public static XRMesh Boolean(
        XRMesh meshA,
        XRMesh meshB,
        EBooleanOperation operation,
        Matrix4x4? transformA = null,
        Matrix4x4? transformB = null)
    {
        ArgumentNullException.ThrowIfNull(meshA);
        ArgumentNullException.ThrowIfNull(meshB);

        (List<Vector3> posA, List<int> idxA) = ExtractMeshData(meshA);
        (List<Vector3> posB, List<int> idxB) = ExtractMeshData(meshB);

        (List<Vector3> positions, List<Vector3> normals, List<int> indices) =
            MeshBooleanOperations.Boolean(posA, idxA, posB, idxB, operation, transformA, transformB);

        return BuildResultMesh(positions, normals, indices);
    }

    public static XRMesh Union(XRMesh a, XRMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Union, transformA, transformB);

    public static XRMesh Intersect(XRMesh a, XRMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Intersect, transformA, transformB);

    public static XRMesh Difference(XRMesh a, XRMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Difference, transformA, transformB);

    public static XRMesh SymmetricDifference(XRMesh a, XRMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.SymmetricDifference, transformA, transformB);

    public static XRMesh BakeShapes(
        IReadOnlyList<(XRMesh Mesh, Matrix4x4? Transform)> meshes,
        EBooleanOperation operation = EBooleanOperation.Union)
    {
        if (meshes.Count == 0)
            throw new ArgumentException("At least one mesh is required.", nameof(meshes));

        List<(IReadOnlyList<Vector3> Positions, IReadOnlyList<int> Indices, Matrix4x4? Transform)> raw = new(meshes.Count);
        foreach ((XRMesh mesh, Matrix4x4? transform) in meshes)
        {
            (List<Vector3> pos, List<int> idx) = ExtractMeshData(mesh);
            raw.Add((pos, idx, transform));
        }

        (List<Vector3> positions, List<Vector3> normals, List<int> indices) =
            MeshBooleanOperations.CombineAll(raw, operation);

        return BuildResultMesh(positions, normals, indices);
    }

    public static XRMesh UnionAll(IReadOnlyList<(XRMesh Mesh, Matrix4x4? Transform)> meshes)
        => BakeShapes(meshes, EBooleanOperation.Union);

    public static XRMesh IntersectAll(IReadOnlyList<(XRMesh Mesh, Matrix4x4? Transform)> meshes)
        => BakeShapes(meshes, EBooleanOperation.Intersect);

    private static (List<Vector3> Positions, List<int> Indices) ExtractMeshData(XRMesh mesh)
    {
        int vertexCount = mesh.VertexCount;
        List<Vector3> positions = new(vertexCount);
        for (uint i = 0; i < (uint)vertexCount; i++)
            positions.Add(mesh.GetPosition(i));

        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        return (positions, [.. indices]);
    }

    private static XRMesh BuildResultMesh(List<Vector3> positions, List<Vector3> normals, List<int> indices)
    {
        Vertex[] vertices = new Vertex[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            vertices[i] = new Vertex(
                positions[i],
                i < normals.Count ? normals[i] : Vector3.UnitY);
        }

        List<VertexTriangle> triangles = new(indices.Count / 3);
        for (int i = 0; i < indices.Count; i += 3)
        {
            int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            if (i0 < vertices.Length && i1 < vertices.Length && i2 < vertices.Length)
                triangles.Add(new VertexTriangle(vertices[i0], vertices[i1], vertices[i2]));
        }

        return XRMesh.Create(triangles);
    }
}