using System.Runtime.InteropServices;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Commands;

/// <summary>
/// The sizes of the meshlet streams that the Advanced scene publishes for one meshlet payload.
/// A payload without a cluster hierarchy publishes its meshlets, vertex indices and triangle words
/// as they are. A cluster hierarchy payload publishes the meshlets of every level, and the
/// triangle-word stream of the geometry also holds the hierarchy tables after the triangle words:
/// the skin header (<see cref="SkinHeaderWords"/>), one cluster record (<see cref="ClusterRecordWords"/>
/// words) per meshlet, one group record (<see cref="GroupRecordWords"/> words) per group, and for a
/// skinned payload the skin tables of <see cref="AdvancedClusterSkinTables"/>. The geometry record
/// stores the level-0 count and the word offset of the cluster records (see
/// <see cref="AdvancedGeometryRecord"/>), so a reader of the cluster and group records does not
/// depend on the skin tables.
/// </summary>
/// <param name="DescriptorCount">Meshlet descriptors of every level.</param>
/// <param name="VertexIndexCount">Meshlet-local vertex index words of every level.</param>
/// <param name="TriangleWordCount">Packed triangle words plus the hierarchy table words.</param>
/// <param name="ClusterHierarchyWordOffset">
/// The word offset of the cluster records in the triangle-word stream of the geometry: the packed
/// triangle word count plus the skin header. Zero without a hierarchy.
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

    /// <summary>
    /// Words of the skin header, which sits right before the cluster records: the offset of the
    /// cluster skin records relative to the first cluster record, or zero without skinning tables
    /// (see <see cref="AdvancedClusterSkinTables"/>).
    /// </summary>
    public const int SkinHeaderWords = 1;

    /// <summary>Computes the published stream sizes of a payload that has meshlets.</summary>
    public static AdvancedPublishedMeshletLayout For(MeshletPayload payload)
    {
        int triangleWordCount = checked((int)(((long)payload.TriangleIndices.Length + 3L) / 4L));
        if (!payload.HasClusterHierarchy)
            return new(payload.Meshlets.Length, payload.VertexIndices.Length, triangleWordCount, 0u, 0u);

        AdvancedClusterSkinTables? skins = AdvancedClusterSkinTables.For(payload);
        int hierarchyWordCount = checked(
            SkinHeaderWords +
            payload.ClusterLods.Length * ClusterRecordWords +
            payload.LodGroups.Length * GroupRecordWords +
            (skins is null ? 0 : AdvancedClusterSkinTables.SkinWordCount(payload, skins)));
        return new(
            payload.Meshlets.Length,
            payload.VertexIndices.Length,
            checked(triangleWordCount + hierarchyWordCount),
            (uint)checked(triangleWordCount + SkinHeaderWords),
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
        int packedTriangleWordCount = ClusterLevel0Count == 0u
            ? TriangleWordCount
            : (int)ClusterHierarchyWordOffset - SkinHeaderWords;
        AdvancedGpuScenePublisher.PackMeshletTriangleWords(
            payload.TriangleIndices.AsSpan(),
            triangleWords[..packedTriangleWordCount]);
        if (ClusterLevel0Count == 0u)
            return;

        Span<uint> hierarchy = triangleWords[(int)ClusterHierarchyWordOffset..];
        ReadOnlySpan<uint> clusterWords = MemoryMarshal.Cast<MeshletClusterLodRecord, uint>(payload.ClusterLods.AsSpan());
        ReadOnlySpan<uint> groupWords = MemoryMarshal.Cast<MeshletLodGroupRecord, uint>(payload.LodGroups.AsSpan());
        clusterWords.CopyTo(hierarchy);
        Span<uint> groups = hierarchy.Slice(clusterWords.Length, groupWords.Length);
        groupWords.CopyTo(groups);

        AdvancedClusterSkinTables? skins = AdvancedClusterSkinTables.For(payload);
        if (skins is null)
        {
            triangleWords[packedTriangleWordCount] = 0u;
            return;
        }

        // Skin tables: offsets are relative to the first cluster record.
        uint skinRecordBase = (uint)(clusterWords.Length + groupWords.Length);
        uint boneWordBase = checked(skinRecordBase + (uint)(payload.ClusterSkins.Length * AdvancedClusterSkinTables.ClusterSkinRecordWords));
        uint groupBoneBase = checked(boneWordBase + (uint)payload.ClusterBones.Length);
        triangleWords[packedTriangleWordCount] = skinRecordBase;

        for (int group = 0; group < payload.LodGroups.Length; group++)
        {
            int word = group * GroupRecordWords;
            groups[word + 6] = groupBoneBase + skins.GroupBoneFirst[group];
            groups[word + 7] = skins.GroupBoneCounts[group];
        }

        Span<uint> skinRecords = hierarchy.Slice((int)skinRecordBase, payload.ClusterSkins.Length * AdvancedClusterSkinTables.ClusterSkinRecordWords);
        for (int cluster = 0; cluster < payload.ClusterSkins.Length; cluster++)
        {
            MeshletClusterSkinRecord skin = payload.ClusterSkins[cluster];
            skinRecords[cluster * 2] = boneWordBase + skin.FirstBone;
            skinRecords[cluster * 2 + 1] = skin.BoneCount | ((uint)skin.Flags << 16);
        }

        Span<uint> boneWords = hierarchy[(int)boneWordBase..];
        for (int i = 0; i < payload.ClusterBones.Length; i++)
            boneWords[i] = payload.ClusterBones[i];
        skins.GroupBones.AsSpan().CopyTo(boneWords[payload.ClusterBones.Length..]);
    }
}
