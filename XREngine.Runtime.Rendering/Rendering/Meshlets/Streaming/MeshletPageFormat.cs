namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The size rules of the geometry page format. A page is the unit of geometry streaming: a
/// fixed-size block that holds one or more whole cluster groups of one mesh. The GPU page pool
/// stores one page in each slot.
/// </summary>
/// <remarks>
/// A page holds these sections, in this order. Each section starts on a
/// <see cref="SectionAlignment"/>-byte boundary from the start of the page:
/// <list type="number">
/// <item>The <see cref="MeshletPageHeader"/> (<see cref="HeaderBytes"/> bytes).</item>
/// <item>One <see cref="XREngine.Rendering.Commands.AdvancedMeshletDescriptor"/> for each cluster
/// (<see cref="DescriptorBytes"/> bytes). Its <c>VertexOffset</c> is an element index into the
/// vertex index section, and its <c>TriangleByteOffset</c> is a byte offset into the triangle
/// section.</item>
/// <item>The vertex indices (<see cref="VertexIndexBytes"/> bytes each): page-local vertex indices
/// for a static page, or source vertex indices for a skinned topology page.</item>
/// <item>The triangle bytes: three local vertex indices for each triangle. The bytes of each
/// cluster start on a four-byte boundary, the same packing as the meshlet payload.</item>
/// <item>For a static page only, the page-local vertices
/// (<see cref="AdvancedDeformedVertex"/>, <see cref="PackedVertexBytes"/> bytes each).</item>
/// </list>
/// The visibility ID of a streamed cluster has 8 bits for the page-local cluster and 8 bits for
/// the local triangle, so a page holds at most <see cref="MaxClustersPerPage"/> clusters and a
/// cluster at most <see cref="MaxTrianglesPerCluster"/> triangles.
/// </remarks>
public static class MeshletPageFormat
{
    /// <summary>
    /// The version of the page layout and of the page tables. A change to either one changes the
    /// key of the page file, so old page files are not read.
    /// </summary>
    public const int Version = 1;

    /// <summary>The first word of every page header: "XRPG" in little-endian byte order.</summary>
    public const uint Magic = 0x47505258u;

    /// <summary>The size of one page and of one GPU page pool slot.</summary>
    public const int PageBytes = 128 * 1024;

    /// <summary>The largest number of clusters in one page.</summary>
    public const int MaxClustersPerPage = 256;

    /// <summary>The largest number of triangles in one cluster.</summary>
    public const int MaxTrianglesPerCluster = 256;

    /// <summary>The size of the page header.</summary>
    public const int HeaderBytes = 32;

    /// <summary>The size of one cluster descriptor.</summary>
    public const int DescriptorBytes = 80;

    /// <summary>The size of one vertex index.</summary>
    public const int VertexIndexBytes = sizeof(uint);

    /// <summary>The size of one page-local vertex of a static page.</summary>
    public const int PackedVertexBytes = 64;

    /// <summary>The alignment of each section inside a page.</summary>
    public const int SectionAlignment = 16;

    /// <summary>The alignment of each page blob in a page file, for direct reads.</summary>
    public const int FileAlignment = 4096;

    /// <summary>Rounds a byte count up to the section alignment.</summary>
    public static int AlignSection(int bytes)
        => (bytes + (SectionAlignment - 1)) & ~(SectionAlignment - 1);

    /// <summary>
    /// Returns the bytes of the triangle data of one cluster: three bytes for each triangle,
    /// rounded up to a four-byte boundary.
    /// </summary>
    public static int ClusterTriangleBytes(uint triangleCount)
        => checked(((int)triangleCount * 3 + 3) & ~3);

    /// <summary>
    /// Returns the bytes that a page with the given contents uses, from the start of the header to
    /// the end of the last section.
    /// </summary>
    /// <param name="clusterCount">The clusters of the page.</param>
    /// <param name="clusterVertexCount">The sum of the vertex counts of the clusters (the vertex index count).</param>
    /// <param name="triangleByteCount">The sum of <see cref="ClusterTriangleBytes"/> over the clusters.</param>
    /// <param name="pageVertexCount">The page-local vertices (zero for a skinned topology page).</param>
    public static long ComputePageBytes(int clusterCount, int clusterVertexCount, int triangleByteCount, int pageVertexCount)
        => HeaderBytes +
           AlignSection(clusterCount * DescriptorBytes) +
           AlignSection(clusterVertexCount * VertexIndexBytes) +
           AlignSection(triangleByteCount) +
           (long)pageVertexCount * PackedVertexBytes;
}
