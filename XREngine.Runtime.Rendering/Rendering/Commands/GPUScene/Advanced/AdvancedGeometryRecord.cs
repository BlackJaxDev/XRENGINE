using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Canonical immutable geometry row used by visibility and shading.
/// </summary>
[XREngine.Rendering.Shaders.GpuRecord("XRAdvancedGeometryRecord")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedGeometryRecord
{
    public AdvancedBufferReference CurrentVertexData;
    public AdvancedBufferReference PreviousVertexData;
    public AdvancedBufferReference IndexData;
    /// <summary>Fixed 80-byte <see cref="AdvancedMeshletDescriptor"/> records.</summary>
    public AdvancedBufferReference MeshletDescriptors;
    /// <summary>Meshlet-local vertex indices as unsigned 32-bit words.</summary>
    public AdvancedBufferReference MeshletVertexIndices;
    /// <summary>Triangle bytes packed into padded unsigned 32-bit words.</summary>
    public AdvancedBufferReference MeshletTriangleWords;
    public AdvancedGpuHandle FallbackGeometry;
    public uint VertexBase;
    public uint VertexCount;
    public uint IndexBase;
    public uint IndexCount;
    public uint MeshletFirst;
    public uint MeshletCount;
    public ulong VertexLayoutId;
    /// <summary>
    /// For a cluster hierarchy geometry, the level-0 meshlet count (see
    /// <see cref="ClusterLevel0Count"/>); zero for other geometry.
    /// </summary>
    public uint ReservedLayout0;
    /// <summary>
    /// For a cluster hierarchy geometry, the word offset of the hierarchy tables in
    /// <see cref="MeshletTriangleWords"/> (see <see cref="ClusterHierarchyWordOffset"/>).
    /// </summary>
    public uint ReservedLayout1;
    public Vector4 BoundsSphere;
    public Vector4 BoundsMin;
    public Vector4 BoundsMax;
    public uint MaterialSectionFirst;
    public uint MaterialSectionCount;
    public EPrimitiveType PrimitiveTopology;
    public EAdvancedGeometrySource Source;
    public EAdvancedGeometryResidency Residency;
    public EAdvancedMissingGeometryBehavior MissingBehavior;
    public uint CookedLayoutVersion;
    public uint Flags;

    public readonly bool IsResident
        => Residency == EAdvancedGeometryResidency.Resident;

    /// <summary>
    /// True when the geometry holds a cluster hierarchy: <see cref="MeshletCount"/> counts the
    /// meshlets of every level, and the level-0 meshlets come first.
    /// </summary>
    public readonly bool HasClusterHierarchy => ReservedLayout0 != 0u;

    /// <summary>The level-0 meshlet count of a cluster hierarchy geometry, or zero.</summary>
    public readonly uint ClusterLevel0Count => ReservedLayout0;

    /// <summary>
    /// The word offset, relative to the start of <see cref="MeshletTriangleWords"/>, of the
    /// cluster records of a cluster hierarchy geometry. The group records follow the cluster
    /// records (see <see cref="AdvancedPublishedMeshletLayout"/>).
    /// </summary>
    public readonly uint ClusterHierarchyWordOffset => ReservedLayout1;

    /// <summary>
    /// The meshlets that a draw without cluster cut selection uses: the level-0 meshlets of a
    /// cluster hierarchy geometry, or all meshlets otherwise.
    /// </summary>
    public readonly uint DrawMeshletCount
        => ReservedLayout0 != 0u ? ReservedLayout0 : MeshletCount;

    /// <summary>
    /// Source-compatibility alias for code which only needs to know whether a
    /// meshlet payload exists. New consumers must bind all three meshlet streams.
    /// </summary>
    public readonly AdvancedBufferReference MeshletData => MeshletDescriptors;

    /// <summary>
    /// The first index arena element of the level cover indices of a skinned cluster hierarchy
    /// geometry. <see cref="IndexData"/> holds the <see cref="IndexCount"/> canonical indices and
    /// then the level covers; the canonical draw uses only the first part.
    /// </summary>
    public readonly uint LevelCoverIndexBase => IndexBase + IndexCount;

    /// <summary>
    /// The number of level cover indices after the canonical indices in <see cref="IndexData"/>,
    /// or zero when the geometry has no level covers.
    /// </summary>
    public readonly uint LevelCoverIndexCount
        => IndexData.ElementCount > IndexCount ? IndexData.ElementCount - IndexCount : 0u;

    /// <summary>
    /// True when an indexed draw of this geometry may use the index range: the canonical index
    /// list, or a range inside the level covers (the cover of a skinned draw whose deformation
    /// job deformed only a coarse prefix of the vertices).
    /// </summary>
    public readonly bool IsDrawIndexRange(uint firstIndex, uint indexCount)
    {
        if (firstIndex == IndexBase && indexCount == IndexCount)
            return true;
        uint coverBase = LevelCoverIndexBase;
        return indexCount != 0u &&
            firstIndex >= coverBase &&
            (ulong)(firstIndex - coverBase) + indexCount <= LevelCoverIndexCount;
    }
}
