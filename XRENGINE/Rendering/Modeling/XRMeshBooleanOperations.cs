using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Modeling;

namespace XREngine.Rendering.Modeling;

/// <summary>
/// Provides boolean (CSG) operations on <see cref="XRMesh"/> instances.
/// Supports union, intersection, difference, and symmetric difference.
/// 
/// Typical usage:
/// <code>
/// XRMesh result = XRMeshBooleanOperations.Union(meshA, meshB);
/// </code>
/// 
/// You can also combine multiple <see cref="ShapeMeshComponent"/> shapes:
/// <code>
/// XRMesh baked = XRMeshBooleanOperations.BakeShapes(
///     new[] { (meshA, transformA), (meshB, transformB) },
///     EBooleanOperation.Union);
/// </code>
/// </summary>
public static class XRMeshBooleanOperations
{
    #region Two-mesh operations

    /// <summary>
    /// Performs a boolean operation between two <see cref="XRMesh"/> instances.
    /// </summary>
    /// <param name="meshA">The first (left-hand) operand mesh.</param>
    /// <param name="meshB">The second (right-hand) operand mesh.</param>
    /// <param name="operation">The boolean set operation to perform.</param>
    /// <param name="transformA">Optional world transform for mesh A.</param>
    /// <param name="transformB">Optional world transform for mesh B.</param>
    /// <returns>A new <see cref="XRMesh"/> containing the boolean result.</returns>
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

    /// <summary>Union: A ∪ B — combined volume of both meshes.</summary>
    public static XRMesh Union(XRMesh a, XRMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Union, transformA, transformB);

    /// <summary>Intersection: A ∩ B — only the shared volume.</summary>
    public static XRMesh Intersect(XRMesh a, XRMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Intersect, transformA, transformB);

    /// <summary>Difference: A \ B — mesh A with mesh B's volume subtracted.</summary>
    public static XRMesh Difference(XRMesh a, XRMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Difference, transformA, transformB);

    /// <summary>Symmetric difference: (A \ B) ∪ (B \ A).</summary>
    public static XRMesh SymmetricDifference(XRMesh a, XRMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.SymmetricDifference, transformA, transformB);

    #endregion

    #region Multi-mesh baking

    /// <summary>
    /// Sequentially applies a boolean operation across multiple meshes,
    /// each with an optional world-space transform, producing a single baked <see cref="XRMesh"/>.
    /// 
    /// This is the primary entry point for combining multiple shape meshes
    /// (e.g., from <c>ShapeMeshComponent</c> instances) into one result.
    /// </summary>
    /// <param name="meshes">Meshes and their optional transforms. At least one is required.</param>
    /// <param name="operation">The boolean operation applied between consecutive meshes.</param>
    /// <returns>A single <see cref="XRMesh"/> representing the combined result.</returns>
    public static XRMesh BakeShapes(
        IReadOnlyList<(XRMesh Mesh, Matrix4x4? Transform)> meshes,
        EBooleanOperation operation = EBooleanOperation.Union)
    {
        if (meshes.Count == 0)
            throw new ArgumentException("At least one mesh is required.", nameof(meshes));

        // Extract raw buffers from all XRMesh instances.
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

    /// <summary>
    /// Union all provided meshes into a single baked mesh.
    /// </summary>
    public static XRMesh UnionAll(IReadOnlyList<(XRMesh Mesh, Matrix4x4? Transform)> meshes)
        => BakeShapes(meshes, EBooleanOperation.Union);

    /// <summary>
    /// Intersect all provided meshes into a single baked mesh.
    /// </summary>
    public static XRMesh IntersectAll(IReadOnlyList<(XRMesh Mesh, Matrix4x4? Transform)> meshes)
        => BakeShapes(meshes, EBooleanOperation.Intersect);

    #endregion

    #region Helpers

    /// <summary>
    /// Extracts positions and triangle indices from an <see cref="XRMesh"/>.
    /// </summary>
    private static (List<Vector3> Positions, List<int> Indices) ExtractMeshData(XRMesh mesh)
    {
        int vertexCount = mesh.VertexCount;
        List<Vector3> positions = new(vertexCount);
        for (uint i = 0; i < (uint)vertexCount; i++)
            positions.Add(mesh.GetPosition(i));

        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        return (positions, [.. indices]);
    }

    /// <summary>
    /// Builds an <see cref="XRMesh"/> from raw position, normal, and index data.
    /// </summary>
    private static XRMesh BuildResultMesh(List<Vector3> positions, List<Vector3> normals, List<int> indices)
    {
        // Build Vertex array with positions and normals.
        Vertex[] vertices = new Vertex[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            vertices[i] = new Vertex(
                positions[i],
                i < normals.Count ? normals[i] : Vector3.UnitY);
        }

        // Convert to VertexTriangles for the XRMesh constructor.
        List<VertexTriangle> triangles = new(indices.Count / 3);
        for (int i = 0; i < indices.Count; i += 3)
        {
            int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            if (i0 < vertices.Length && i1 < vertices.Length && i2 < vertices.Length)
                triangles.Add(new VertexTriangle(vertices[i0], vertices[i1], vertices[i2]));
        }

        return XRMesh.Create(triangles);
    }

    #endregion
}
