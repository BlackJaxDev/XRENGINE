using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Holds the geometry inputs that the prepared unchanged-row walk compared
/// for one run of rows with the same mesh.
/// </summary>
/// <remarks>
/// Every value comes from the outputs of the row evaluation, and the prepare
/// never reads the mesh again. The consume reads the live mesh at the swap and
/// compares it with these values. The swap-time gate compares the same values
/// with the plan, and a passed row has the plan values. Thus an accepted
/// witness proves that the swap-time gate would pass these comparisons. This
/// includes an in-place edit of the triangle list that changes its count but
/// does not advance the geometry revision.
/// </remarks>
internal struct AdvancedPreparedMeshWitness
{
    /// <summary>The mesh of the rows.</summary>
    public XRMesh? Mesh;

    /// <summary>The vertex count that the rows compared.</summary>
    public int VertexCount;

    /// <summary>The index count that the rows compared.</summary>
    public int IndexCount;

    /// <summary>The geometry revision that the rows compared.</summary>
    public long GeometryRevision;

    /// <summary>The primitive type that the rows compared.</summary>
    public EPrimitiveType PrimitiveType;

    /// <summary>The triangle count that the rows compared.</summary>
    public int TriangleCount;
}
