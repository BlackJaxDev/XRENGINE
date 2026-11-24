using System.Runtime.InteropServices;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Commands;

/// <summary>
/// The sizes of the meshlet streams that the Advanced scene publishes for one meshlet payload.
/// A payload without a cluster hierarchy publishes its meshlets, vertex indices and triangle words
/// as they are. A cluster hierarchy payload publishes the meshlets of every level, and the
/// triangle-word stream of the geometry also holds the hierarchy tables after the triangle words:
/// the stream header (<see cref="StreamHeaderWords"/>), the skin header (<see cref="SkinHeaderWords"/>),
/// one cluster record (<see cref="ClusterRecordWords"/> words) per meshlet, one group record
/// (<see cref="GroupRecordWords"/> words) per group, for a skinned payload the skin tables of
/// <see cref="AdvancedClusterSkinTables"/>, and, when the geometry draws from geometry pages, the page
/// block (see <see cref="Pages"/>). The geometry record stores the level-0 count and the word offset
/// of the cluster records (see <see cref="AdvancedGeometryRecord"/>), so a reader of the cluster and
/// group records does not depend on the other tables.
/// </summary>
/// <param name="DescriptorCount">Meshlet descriptors of every level.</param>
/// <param name="VertexIndexCount">Meshlet-local vertex index words of every level.</param>
/// <param name="TriangleWordCount">Packed triangle words plus the hierarchy table words.</param>
/// <param name="ClusterHierarchyWordOffset">
/// The word offset of the cluster records in the triangle-word stream of the geometry: the packed
/// triangle word count plus the stream header and the skin header. Zero without a hierarchy.
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

    /// <summary>
    /// Words of the stream header, which sits right before the skin header: the offset of the page
    /// block relative to the first cluster record, or zero when the geometry does not draw from
    /// geometry pages. Shaders find it two words before the cluster records.
    /// </summary>
    public const int StreamHeaderWords = 1;

    /// <summary>
    /// Words of the page block header: the page count, the cluster count, the first page of the
    /// geometry in the page table of <see cref="AdvancedGeometryPagePool"/>, and the group count.
    /// </summary>
    public const int PageBlockHeaderWords = 4;

    /// <summary>The position of the first-page word in the page block header.</summary>
    public const int PageBlockFirstPageWord = 2;

    /// <summary>
    /// The geometry pages that the geometry draws from, or null. With pages, the triangle-word
    /// stream ends with the page block at <see cref="PageBlockWordOffset"/>: the page block header,
    /// then one word per cluster in published meshlet order with
    /// <c>(page &lt;&lt; 8) | page-local cluster</c>, then one word per group in published group
    /// order with the geometry-local page that holds the group. The cut reads the group map to test
    /// whether the finer group of a cluster is resident. The page images are not in the stream. They go
    /// to slots of the geometry page pool, and the page table maps page <c>firstPage + page</c> to
    /// its slot. The geometry record of such a geometry has
    /// <see cref="EAdvancedGeometryFlags.StreamedPages"/>.
    /// </summary>
    public MeshletPageSet? Pages { get; init; }

    /// <summary>The word offset of the page block relative to the first cluster record, or zero.</summary>
    public uint PageBlockWordOffset { get; init; }

    /// <summary>
    /// The index of the first-page word in the triangle-word stream of the geometry. The publisher
    /// writes the first page of the reserved page table range there after <see cref="Pack"/>,
    /// which writes <see cref="AdvancedGeometryPagePool.NotResident"/>. Valid only with
    /// <see cref="Pages"/>.
    /// </summary>
    public int FirstPageWordIndex
        => checked((int)(ClusterHierarchyWordOffset + PageBlockWordOffset) + PageBlockFirstPageWord);

    /// <summary>The geometry flags of the record that this layout registers.</summary>
    public EAdvancedGeometryFlags GeometryFlags
        => Pages is null ? EAdvancedGeometryFlags.None : EAdvancedGeometryFlags.StreamedPages;

    /// <summary>
    /// Computes the published stream sizes of a payload that has meshlets. A cluster hierarchy
    /// payload with attached geometry pages (<see cref="MeshletPayload.StreamingPages"/>) also gets
    /// the page block while <see cref="AdvancedVirtualGeometryPolicy.GeometryPagesEnabled"/> is on.
    /// </summary>
    public static AdvancedPublishedMeshletLayout For(MeshletPayload payload)
    {
        int triangleWordCount = checked((int)(((long)payload.TriangleIndices.Length + 3L) / 4L));
        if (!payload.HasClusterHierarchy)
            return new(payload.Meshlets.Length, payload.VertexIndices.Length, triangleWordCount, 0u, 0u);

        AdvancedClusterSkinTables? skins = AdvancedClusterSkinTables.For(payload);
        int recordWordCount = checked(
            payload.ClusterLods.Length * ClusterRecordWords +
            payload.LodGroups.Length * GroupRecordWords +
            (skins is null ? 0 : AdvancedClusterSkinTables.SkinWordCount(payload, skins)));
        MeshletPageSet? pages = AdvancedVirtualGeometryPolicy.GeometryPagesEnabled &&
            payload.StreamingPages is { } attached &&
            attached.Table.ClusterCount == payload.ClusterLods.Length
                ? attached
                : null;
        int pageBlockWordCount = pages is null ? 0 : PageBlockWordCount(pages);
        return new(
            payload.Meshlets.Length,
            payload.VertexIndices.Length,
            checked(triangleWordCount + StreamHeaderWords + SkinHeaderWords + recordWordCount + pageBlockWordCount),
            (uint)checked(triangleWordCount + StreamHeaderWords + SkinHeaderWords),
            (uint)payload.ClusterLevel0Count)
        {
            Pages = pages,
            PageBlockWordOffset = pages is null ? 0u : (uint)recordWordCount,
        };
    }

    /// <summary>The words of the page block of a page set: the header, the cluster map and the group map.</summary>
    private static int PageBlockWordCount(MeshletPageSet pages)
        => checked(PageBlockHeaderWords + pages.Table.ClusterCount + pages.Table.Groups.Length);

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
            : (int)ClusterHierarchyWordOffset - SkinHeaderWords - StreamHeaderWords;
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

        // The stream header comes first, then the skin header, then the cluster records.
        triangleWords[packedTriangleWordCount] = PageBlockWordOffset;
        int skinHeaderWord = packedTriangleWordCount + StreamHeaderWords;
        if (Pages is not null)
            PackPageBlock(Pages, hierarchy[(int)PageBlockWordOffset..]);

        AdvancedClusterSkinTables? skins = AdvancedClusterSkinTables.For(payload);
        if (skins is null)
        {
            triangleWords[skinHeaderWord] = 0u;
            return;
        }

        // Skin tables: offsets are relative to the first cluster record.
        uint skinRecordBase = (uint)(clusterWords.Length + groupWords.Length);
        uint boneWordBase = checked(skinRecordBase + (uint)(payload.ClusterSkins.Length * AdvancedClusterSkinTables.ClusterSkinRecordWords));
        uint groupBoneBase = checked(boneWordBase + (uint)payload.ClusterBones.Length);
        triangleWords[skinHeaderWord] = skinRecordBase;

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

    /// <summary>
    /// Writes the page block of <see cref="Pages"/>: the header with a
    /// <see cref="AdvancedGeometryPagePool.NotResident"/> first page, the cluster map and the group map.
    /// <paramref name="block"/> starts at the page block, which is <see cref="PageBlockWordOffset"/>
    /// words after the first cluster record.
    /// </summary>
    private static void PackPageBlock(MeshletPageSet pages, Span<uint> block)
    {
        MeshletPageTable table = pages.Table;
        int pageCount = table.PageCount;
        int clusterCount = table.ClusterCount;
        block[0] = (uint)pageCount;
        block[1] = (uint)clusterCount;
        block[PageBlockFirstPageWord] = AdvancedGeometryPagePool.NotResident;
        block[3] = (uint)table.Groups.Length;
        Span<uint> clusterMap = block.Slice(PageBlockHeaderWords, clusterCount);
        Span<uint> groupMap = block.Slice(PageBlockHeaderWords + clusterCount, table.Groups.Length);
        for (int group = 0; group < groupMap.Length; group++)
            groupMap[group] = (uint)table.GetGroupPage(group);

        // The page table lists the clusters in page order; ClusterOrder maps each paged position
        // back to the published (payload) meshlet index.
        for (int page = 0; page < pageCount; page++)
        {
            MeshletPageRecord record = table.Pages[page];
            for (uint local = 0u; local < record.ClusterCount; local++)
            {
                uint source = table.ClusterOrder[(int)(record.FirstCluster + local)];
                clusterMap[(int)source] = ((uint)page << 8) | local;
            }
        }
    }
}
