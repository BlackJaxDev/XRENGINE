using System.Runtime.InteropServices;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Commands;

/// <summary>
/// The sizes of the meshlet streams that the Advanced scene publishes for one meshlet payload.
/// A payload without a cluster hierarchy publishes its meshlets, vertex indices and triangle words
/// as they are. A cluster hierarchy payload publishes the meshlets of every level, and the
/// triangle-word stream of the geometry also holds the hierarchy tables after the triangle words:
/// first one cluster record (<see cref="ClusterRecordWords"/> words) per meshlet, then one group
/// record (<see cref="GroupRecordWords"/> words) per group. The geometry record stores the
/// level-0 count and the word offset of the tables (see <see cref="AdvancedGeometryRecord"/>).
/// </summary>
/// <param name="DescriptorCount">Meshlet descriptors of every level.</param>
/// <param name="VertexIndexCount">Meshlet-local vertex index words of every level.</param>
/// <param name="TriangleWordCount">Packed triangle words plus the hierarchy table words.</param>
/// <param name="ClusterHierarchyWordOffset">
/// The word offset of the cluster records in the triangle-word stream of the geometry, which is
/// the packed triangle word count. Zero without a hierarchy.
/// </param>
/// <param name="ClusterLevel0Count">The level-0 meshlet count, or zero without a hierarchy.</param>
internal readonly record struct AdvancedPublishedMeshletLayout(
    int DescriptorCount,
    int VertexIndexCount,
    int TriangleWordCount,
    uint ClusterHierarchyWordOffset,
    uint ClusterLevel0Count)
{
    /// <summary>Words of one <see cref="MeshletClusterLodRecord"/>: group, refined group.</summary>
    public const int ClusterRecordWords = 2;
    /// <summary>
    /// Words of one <see cref="MeshletLodGroupRecord"/>: sphere center and radius, error, depth and
    /// two reserved words. The float values are stored as their bits.
    /// </summary>
    public const int GroupRecordWords = 8;

    /// <summary>Computes the published stream sizes of a payload that has meshlets.</summary>
    public static AdvancedPublishedMeshletLayout For(MeshletPayload payload)
    {
        int triangleWordCount = checked((int)(((long)payload.TriangleIndices.Length + 3L) / 4L));
        if (!payload.HasClusterHierarchy)
            return new(payload.Meshlets.Length, payload.VertexIndices.Length, triangleWordCount, 0u, 0u);

        int hierarchyWordCount = checked(
            payload.ClusterLods.Length * ClusterRecordWords +
            payload.LodGroups.Length * GroupRecordWords);
        return new(
            payload.Meshlets.Length,
            payload.VertexIndices.Length,
            checked(triangleWordCount + hierarchyWordCount),
            (uint)triangleWordCount,
            (uint)payload.ClusterLevel0Count);
    }

    /// <summary>
    /// Writes the published streams of a payload into destination spans with the sizes from
    /// <see cref="For"/>: the converted descriptors, the vertex indices, and the packed triangle
    /// words followed by the hierarchy tables.
    /// </summary>
    public void Pack(
        MeshletPayload payload,
        Span<AdvancedMeshletDescriptor> descriptors,
        Span<uint> vertexIndices,
        Span<uint> triangleWords)
    {
        AdvancedGpuScenePublisher.ConvertMeshletDescriptors(payload.Meshlets.AsSpan(), descriptors);
        payload.VertexIndices.AsSpan().CopyTo(vertexIndices);
        int packedTriangleWordCount = ClusterLevel0Count == 0u ? TriangleWordCount : (int)ClusterHierarchyWordOffset;
        AdvancedGpuScenePublisher.PackMeshletTriangleWords(
            payload.TriangleIndices.AsSpan(),
            triangleWords[..packedTriangleWordCount]);
        if (ClusterLevel0Count == 0u)
            return;

        Span<uint> hierarchy = triangleWords[packedTriangleWordCount..];
        ReadOnlySpan<uint> clusterWords = MemoryMarshal.Cast<MeshletClusterLodRecord, uint>(payload.ClusterLods.AsSpan());
        ReadOnlySpan<uint> groupWords = MemoryMarshal.Cast<MeshletLodGroupRecord, uint>(payload.LodGroups.AsSpan());
        clusterWords.CopyTo(hierarchy);
        groupWords.CopyTo(hierarchy[clusterWords.Length..]);
    }
}
