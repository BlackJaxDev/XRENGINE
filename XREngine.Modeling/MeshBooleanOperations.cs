using System.Numerics;

namespace XREngine.Modeling;

/// <summary>
/// High-level API for performing boolean (CSG) operations on modeling meshes.
/// Supports union, intersection, difference, and symmetric difference between
/// <see cref="EditableMesh"/>, <see cref="ModelingMeshDocument"/>, or raw position/index buffers.
/// 
/// Results can be consumed directly as position/index data, or converted back
/// into <see cref="EditableMesh"/> / <see cref="ModelingMeshDocument"/> for
/// further editing and export.
/// </summary>
public static class MeshBooleanOperations
{
    #region EditableMesh overloads

    /// <summary>
    /// Performs a boolean operation between two editable meshes.
    /// </summary>
    public static EditableMesh Boolean(
        EditableMesh meshA,
        EditableMesh meshB,
        EBooleanOperation operation,
        Matrix4x4? transformA = null,
        Matrix4x4? transformB = null)
    {
        ArgumentNullException.ThrowIfNull(meshA);
        ArgumentNullException.ThrowIfNull(meshB);

        (List<Vector3> posA, List<int> idxA) = meshA.Bake();
        (List<Vector3> posB, List<int> idxB) = meshB.Bake();

        (List<Vector3> positions, _, List<int> indices) =
            CsgMesh.Boolean(posA, idxA, posB, idxB, operation, transformA, transformB);

        return new EditableMesh(positions, indices);
    }

    /// <summary>
    /// Performs a boolean union (A ∪ B) of two editable meshes.
    /// </summary>
    public static EditableMesh Union(EditableMesh a, EditableMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Union, transformA, transformB);

    /// <summary>
    /// Performs a boolean intersection (A ∩ B) of two editable meshes.
    /// </summary>
    public static EditableMesh Intersect(EditableMesh a, EditableMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Intersect, transformA, transformB);

    /// <summary>
    /// Performs a boolean difference (A \ B) — subtracts B from A.
    /// </summary>
    public static EditableMesh Difference(EditableMesh a, EditableMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.Difference, transformA, transformB);

    /// <summary>
    /// Performs a symmetric difference (A ⊕ B) — volume in either but not both.
    /// </summary>
    public static EditableMesh SymmetricDifference(EditableMesh a, EditableMesh b, Matrix4x4? transformA = null, Matrix4x4? transformB = null)
        => Boolean(a, b, EBooleanOperation.SymmetricDifference, transformA, transformB);

    #endregion

    #region ModelingMeshDocument overloads

    /// <summary>
    /// Performs a boolean operation between two modeling documents.
    /// Returns a new document with positions, normals, and indices populated.
    /// </summary>
    public static ModelingMeshDocument Boolean(
        ModelingMeshDocument docA,
        ModelingMeshDocument docB,
        EBooleanOperation operation,
        Matrix4x4? transformA = null,
        Matrix4x4? transformB = null)
    {
        ArgumentNullException.ThrowIfNull(docA);
        ArgumentNullException.ThrowIfNull(docB);

        (List<Vector3> positions, List<Vector3> normals, List<int> indices) =
            CsgMesh.Boolean(
                docA.Positions, docA.TriangleIndices,
                docB.Positions, docB.TriangleIndices,
                operation, transformA, transformB);

        return new ModelingMeshDocument
        {
            Positions = positions,
            Normals = normals,
            TriangleIndices = indices,
            Metadata = new ModelingMeshMetadata
            {
                SourcePrimitiveType = ModelingPrimitiveType.Triangles
            }
        };
    }

    #endregion

    #region Raw buffer overloads

    /// <summary>
    /// Performs a boolean operation between two indexed triangle meshes
    /// defined by raw position and index buffers.
    /// </summary>
    public static (List<Vector3> Positions, List<Vector3> Normals, List<int> Indices) Boolean(
        IReadOnlyList<Vector3> positionsA, IReadOnlyList<int> indicesA,
        IReadOnlyList<Vector3> positionsB, IReadOnlyList<int> indicesB,
        EBooleanOperation operation,
        Matrix4x4? transformA = null,
        Matrix4x4? transformB = null)
        => CsgMesh.Boolean(positionsA, indicesA, positionsB, indicesB, operation, transformA, transformB);

    #endregion

    #region Multi-mesh operations

    /// <summary>
    /// Sequentially unions multiple meshes together into a single result.
    /// Each mesh can have an associated transform.
    /// </summary>
    public static EditableMesh UnionAll(IReadOnlyList<(EditableMesh Mesh, Matrix4x4? Transform)> meshes)
        => CombineAll(meshes, EBooleanOperation.Union);

    /// <summary>
    /// Sequentially intersects multiple meshes together.
    /// </summary>
    public static EditableMesh IntersectAll(IReadOnlyList<(EditableMesh Mesh, Matrix4x4? Transform)> meshes)
        => CombineAll(meshes, EBooleanOperation.Intersect);

    /// <summary>
    /// Sequentially performs a boolean operation across all provided meshes.
    /// The first mesh is used as the initial accumulator.
    /// </summary>
    public static EditableMesh CombineAll(
        IReadOnlyList<(EditableMesh Mesh, Matrix4x4? Transform)> meshes,
        EBooleanOperation operation)
    {
        if (meshes.Count == 0)
            throw new ArgumentException("At least one mesh is required.", nameof(meshes));

        if (meshes.Count == 1)
            return meshes[0].Mesh.Clone();

        // Bake all meshes upfront to avoid re-baking during iteration.
        List<(List<Vector3> Positions, List<int> Indices, Matrix4x4? Transform)> baked = new(meshes.Count);
        foreach ((EditableMesh mesh, Matrix4x4? transform) in meshes)
        {
            (List<Vector3> pos, List<int> idx) = mesh.Bake();
            baked.Add((pos, idx, transform));
        }

        // Start with the first mesh (apply its transform during the first boolean op).
        List<Vector3> accumPositions = baked[0].Positions;
        List<int> accumIndices = baked[0].Indices;
        Matrix4x4? accumTransform = baked[0].Transform;

        for (int i = 1; i < baked.Count; i++)
        {
            (List<Vector3> positions, List<Vector3> _, List<int> indices) =
                CsgMesh.Boolean(
                    accumPositions, accumIndices,
                    baked[i].Positions, baked[i].Indices,
                    operation,
                    accumTransform,
                    baked[i].Transform);

            accumPositions = positions;
            accumIndices = indices;
            accumTransform = null; // Already applied.
        }

        return new EditableMesh(accumPositions, accumIndices);
    }

    /// <summary>
    /// Sequentially unions raw mesh buffers together.
    /// </summary>
    public static (List<Vector3> Positions, List<Vector3> Normals, List<int> Indices) UnionAll(
        IReadOnlyList<(IReadOnlyList<Vector3> Positions, IReadOnlyList<int> Indices, Matrix4x4? Transform)> meshes)
        => CombineAll(meshes, EBooleanOperation.Union);

    /// <summary>
    /// Sequentially performs a boolean operation across raw mesh buffers.
    /// </summary>
    public static (List<Vector3> Positions, List<Vector3> Normals, List<int> Indices) CombineAll(
        IReadOnlyList<(IReadOnlyList<Vector3> Positions, IReadOnlyList<int> Indices, Matrix4x4? Transform)> meshes,
        EBooleanOperation operation)
    {
        if (meshes.Count == 0)
            throw new ArgumentException("At least one mesh is required.", nameof(meshes));

        if (meshes.Count == 1)
        {
            // Pass through a single mesh (apply its transform if present).
            List<Vector3> positions = [.. meshes[0].Positions];
            List<int> indices = [.. meshes[0].Indices];
            if (meshes[0].Transform is { } firstTransform)
            {
                for (int i = 0; i < positions.Count; i++)
                    positions[i] = Vector3.Transform(positions[i], firstTransform);
            }
            List<Vector3> normals = RecalculateNormals(positions, indices);
            return (positions, normals, indices);
        }

        IReadOnlyList<Vector3> accumPositions = meshes[0].Positions;
        IReadOnlyList<int> accumIndices = meshes[0].Indices;
        Matrix4x4? accumTransform = meshes[0].Transform;
        List<Vector3> resultNormals = [];

        for (int i = 1; i < meshes.Count; i++)
        {
            (List<Vector3> positions, List<Vector3> normals, List<int> indices) =
                CsgMesh.Boolean(
                    accumPositions, accumIndices,
                    meshes[i].Positions, meshes[i].Indices,
                    operation,
                    accumTransform,
                    meshes[i].Transform);

            accumPositions = positions;
            accumIndices = indices;
            resultNormals = normals;
            accumTransform = null;
        }

        return ([.. accumPositions], resultNormals, [.. accumIndices]);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Recalculates face normals for an indexed triangle mesh.
    /// Each vertex gets the normalized average of all adjacent face normals.
    /// </summary>
    private static List<Vector3> RecalculateNormals(List<Vector3> positions, List<int> indices)
    {
        Vector3[] normals = new Vector3[positions.Count];

        for (int i = 0; i < indices.Count; i += 3)
        {
            int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            Vector3 faceNormal = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
            normals[i0] += faceNormal;
            normals[i1] += faceNormal;
            normals[i2] += faceNormal;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            if (normals[i].LengthSquared() > float.Epsilon)
                normals[i] = Vector3.Normalize(normals[i]);
            else
                normals[i] = Vector3.UnitY;
        }

        return [.. normals];
    }

    #endregion
}
