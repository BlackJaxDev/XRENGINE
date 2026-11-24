using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable metadata supplied while registering one geometry row.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGeometryRegistration(
    uint VertexCount,
    uint IndexCount,
    uint VertexStride,
    EPrimitiveType PrimitiveTopology,
    ulong VertexLayoutId,
    Vector4 BoundsSphere,
    Vector4 BoundsMin,
    Vector4 BoundsMax,
    uint MaterialSectionFirst,
    uint MaterialSectionCount,
    uint MeshletFirst,
    uint MeshletCount,
    uint CookedLayoutVersion)
{
    /// <summary>
    /// The level-0 meshlet count of a cluster hierarchy geometry, or zero. The level-0 meshlets are
    /// the first meshlets of the geometry.
    /// </summary>
    public uint ClusterLevel0Count { get; init; }

    /// <summary>
    /// The word offset of the cluster hierarchy tables in the triangle-word stream of a cluster
    /// hierarchy geometry (see <see cref="AdvancedPublishedMeshletLayout"/>), or zero.
    /// </summary>
    public uint ClusterHierarchyWordOffset { get; init; }

    /// <summary>
    /// The number of level cover indices that follow the <see cref="IndexCount"/> canonical
    /// indices in the index span of a skinned cluster hierarchy geometry, or zero. The database
    /// stores both lists in one index arena range, so the cover indices move with the canonical
    /// indices when the arena compacts. See <see cref="AdvancedGeometryRecord.LevelCoverIndexBase"/>.
    /// </summary>
    public uint LevelCoverIndexCount { get; init; }

    /// <summary>
    /// The bits that the database copies into <see cref="AdvancedGeometryRecord.Flags"/>. A
    /// registration with <see cref="EAdvancedGeometryFlags.StreamedPages"/> must be a cluster
    /// hierarchy geometry whose triangle-word stream holds the page block.
    /// </summary>
    public EAdvancedGeometryFlags GeometryFlags { get; init; }

    public static AdvancedGeometryRegistration Create(
        uint vertexCount,
        uint indexCount,
        uint vertexStride,
        EPrimitiveType primitiveTopology,
        ulong vertexLayoutId,
        Vector4 boundsSphere,
        Vector4 boundsMin,
        Vector4 boundsMax,
        uint materialSectionFirst = 0u,
        uint materialSectionCount = 1u,
        uint meshletFirst = 0u,
        uint meshletCount = 0u)
        => new(
            vertexCount,
            indexCount,
            vertexStride,
            primitiveTopology,
            vertexLayoutId,
            boundsSphere,
            boundsMin,
            boundsMax,
            materialSectionFirst,
            materialSectionCount,
            meshletFirst,
            meshletCount,
            AdvancedGeometryCookedLayout.CurrentVersion);
}
