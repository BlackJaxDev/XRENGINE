using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Packs the clusters of one cluster hierarchy payload into fixed-size geometry pages for
/// geometry streaming, and builds the resident tables that the cut and the streaming manager read
/// (see <see cref="MeshletPageTable"/>).
/// </summary>
/// <remarks>
/// <para>
/// The builder runs after the hierarchy cook and does not change the payload. It does these steps:
/// </para>
/// <list type="number">
/// <item>Collects the clusters of each group and checks that each group fits in one page.</item>
/// <item>Puts the root groups (groups with <see cref="MeshletLodGroupRecord.SimplifiedError"/>
/// equal to <see cref="float.MaxValue"/>) into root pages of their own. A root group stays drawn at
/// every distance, so its page stays resident. A page that mixed root groups with other groups
/// would keep those groups resident too.</item>
/// <item>Sorts the other groups of each level by the Morton code of their sphere center and fills
/// pages in that order. A page holds groups of one level only. This keeps a page spatially compact
/// and keeps its dependency list short.</item>
/// <item>Puts the clusters in page order, with the clusters of one group together.</item>
/// <item>Lists the dependencies of each page: the pages that hold a parent of one of its groups.
/// A parent of group H is the group of any cluster whose refined group is H.</item>
/// <item>Writes the GPU image of each page. A static page gets its own copy of the vertices that
/// its clusters use, and its vertex indices point to that copy. A skinned topology page keeps
/// source vertex indices, because the deformation output supplies its vertices.</item>
/// <item>Checks the result with <see cref="Validate"/>.</item>
/// </list>
/// <para>
/// With the portable meshlet limits (64 vertices and 124 triangles for each cluster) and the group
/// size of the hierarchy cook (at most about 21 clusters), a static group needs at most about
/// 99 KiB, so every group of a runtime-compatible payload fits in a 128 KiB page.
/// </para>
/// </remarks>
public static class MeshletPageBuilder
{
    /// <summary>
    /// The version of the page cook. The page file key includes it, so a change to the packing
    /// order or the page contents makes old page files stale.
    /// </summary>
    public const int CookVersion = 1;

    private const int MortonBitsPerAxis = 10;

    /// <summary>
    /// Builds the pages of a cluster hierarchy payload. A payload with cluster skinning tables gets
    /// skinned topology pages; other payloads get static pages with page-local vertices.
    /// </summary>
    /// <param name="payload">The cluster hierarchy payload.</param>
    /// <param name="packedVertices">
    /// For a static payload, the packed vertex of each source vertex. Empty for a skinned payload.
    /// </param>
    /// <exception cref="InvalidDataException">The payload cannot be paged, or the result fails validation.</exception>
    public static MeshletPageSet Build(MeshletPayload payload, ReadOnlySpan<AdvancedDeformedVertex> packedVertices)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.HasClusterHierarchy)
            throw new ArgumentException("Only a cluster hierarchy payload can be paged.", nameof(payload));

        return Build(
            payload.Meshlets.AsSpan(),
            payload.VertexIndices.AsSpan(),
            payload.TriangleIndices.AsSpan(),
            payload.ClusterLods.AsSpan(),
            payload.LodGroups.AsSpan(),
            payload.SourceVertexCount,
            packedVertices,
            skinnedTopology: payload.HasClusterSkinning);
    }

    /// <summary>
    /// Builds the pages of a cluster hierarchy from its payload streams.
    /// </summary>
    /// <param name="meshlets">The meshlet descriptors of every level, in payload order.</param>
    /// <param name="vertexIndices">The source vertex index of each meshlet vertex.</param>
    /// <param name="triangleBytes">The local triangle bytes of every meshlet.</param>
    /// <param name="clusterLods">The hierarchy record of each meshlet.</param>
    /// <param name="groups">The hierarchy groups.</param>
    /// <param name="vertexCount">The source vertex count of the mesh.</param>
    /// <param name="packedVertices">
    /// For static pages, the packed vertex of each source vertex (<paramref name="vertexCount"/>
    /// entries). Empty for skinned topology pages.
    /// </param>
    /// <param name="skinnedTopology">True to build skinned topology pages without page-local vertices.</param>
    /// <exception cref="InvalidDataException">The hierarchy cannot be paged, or the result fails validation.</exception>
    public static MeshletPageSet Build(
        ReadOnlySpan<CpuMeshletDescriptor> meshlets,
        ReadOnlySpan<uint> vertexIndices,
        ReadOnlySpan<byte> triangleBytes,
        ReadOnlySpan<MeshletClusterLodRecord> clusterLods,
        ReadOnlySpan<MeshletLodGroupRecord> groups,
        int vertexCount,
        ReadOnlySpan<AdvancedDeformedVertex> packedVertices,
        bool skinnedTopology)
    {
        CheckInputs(meshlets, vertexIndices, triangleBytes, clusterLods, groups, vertexCount, packedVertices, skinnedTopology);

        // Clusters of each group, in payload order: group g owns
        // groupClusters[groupClusterStart[g] .. groupClusterStart[g + 1]).
        int[] groupClusterStart = new int[groups.Length + 1];
        for (int m = 0; m < clusterLods.Length; m++)
            groupClusterStart[clusterLods[m].Group + 1]++;
        for (int g = 0; g < groups.Length; g++)
        {
            if (groupClusterStart[g + 1] == 0)
                throw new InvalidDataException($"Cluster hierarchy group {g} has no clusters.");
            groupClusterStart[g + 1] += groupClusterStart[g];
        }
        int[] groupClusters = new int[clusterLods.Length];
        int[] cursor = (int[])groupClusterStart.Clone();
        for (int m = 0; m < clusterLods.Length; m++)
            groupClusters[cursor[clusterLods[m].Group]++] = m;

        // The vertex index and triangle bytes of each group, and for static pages the distinct
        // source vertices of each group (groupVertices[groupVertexStart[g] .. groupVertexStart[g + 1])).
        int[] groupVertexRefs = new int[groups.Length];
        int[] groupTriangleBytes = new int[groups.Length];
        int[] groupVertexStart = new int[groups.Length + 1];
        List<uint> groupVertices = new(skinnedTopology ? 0 : vertexIndices.Length);
        int[]? vertexStamp = skinnedTopology ? null : new int[vertexCount];
        for (int g = 0; g < groups.Length; g++)
        {
            groupVertexStart[g] = groupVertices.Count;
            for (int i = groupClusterStart[g]; i < groupClusterStart[g + 1]; i++)
            {
                CpuMeshletDescriptor meshlet = meshlets[groupClusters[i]];
                groupVertexRefs[g] += (int)meshlet.VertexCount;
                groupTriangleBytes[g] += MeshletPageFormat.ClusterTriangleBytes(meshlet.TriangleCount);
                if (vertexStamp is null)
                    continue;
                foreach (uint vertex in vertexIndices.Slice((int)meshlet.VertexOffset, (int)meshlet.VertexCount))
                {
                    if (vertexStamp[vertex] == g + 1)
                        continue;
                    vertexStamp[vertex] = g + 1;
                    groupVertices.Add(vertex);
                }
            }

            int clusterCount = groupClusterStart[g + 1] - groupClusterStart[g];
            int distinctVertices = groupVertices.Count - groupVertexStart[g];
            long bytes = MeshletPageFormat.ComputePageBytes(clusterCount, groupVertexRefs[g], groupTriangleBytes[g], distinctVertices);
            if (clusterCount > MeshletPageFormat.MaxClustersPerPage || bytes > MeshletPageFormat.PageBytes)
                throw new InvalidDataException(
                    $"Cluster hierarchy group {g} needs {bytes} bytes and {clusterCount} clusters. A page holds " +
                    $"{MeshletPageFormat.PageBytes} bytes and {MeshletPageFormat.MaxClustersPerPage} clusters.");
        }
        groupVertexStart[groups.Length] = groupVertices.Count;

        int[] groupOrder = SortGroupsForPacking(groups);

        // Fill the pages in packing order. A page closes when the next group is of another bucket
        // (root or a different level) or does not fit.
        List<int> pageGroupStart = [];
        List<bool> pageIsRoot = [];
        int[] pageGroups = new int[groups.Length];
        int[]? pageVertexStamp = skinnedTopology ? null : new int[vertexCount];
        int pageClusters = 0;
        int pageVertexRefs = 0;
        int pageTriangleBytes = 0;
        int pageVertices = 0;
        int pageBucket = int.MinValue;
        for (int i = 0; i < groupOrder.Length; i++)
        {
            int g = groupOrder[i];
            int bucket = BucketOf(groups[g]);
            int groupClusterCount = groupClusterStart[g + 1] - groupClusterStart[g];
            int newVertices = CountNewPageVertices(g, pageGroupStart.Count, groupVertexStart, groupVertices, pageVertexStamp);
            bool fits = pageGroupStart.Count > 0 &&
                bucket == pageBucket &&
                pageClusters + groupClusterCount <= MeshletPageFormat.MaxClustersPerPage &&
                MeshletPageFormat.ComputePageBytes(
                    pageClusters + groupClusterCount,
                    pageVertexRefs + groupVertexRefs[g],
                    pageTriangleBytes + groupTriangleBytes[g],
                    pageVertices + newVertices) <= MeshletPageFormat.PageBytes;
            if (!fits)
            {
                pageGroupStart.Add(i);
                pageIsRoot.Add(IsRoot(groups[g]));
                pageBucket = bucket;
                pageClusters = 0;
                pageVertexRefs = 0;
                pageTriangleBytes = 0;
                pageVertices = 0;
                newVertices = CountNewPageVertices(g, pageGroupStart.Count, groupVertexStart, groupVertices, pageVertexStamp);
            }

            pageGroups[i] = g;
            pageClusters += groupClusterCount;
            pageVertexRefs += groupVertexRefs[g];
            pageTriangleBytes += groupTriangleBytes[g];
            pageVertices += newVertices;
            MarkPageVertices(g, pageGroupStart.Count, groupVertexStart, groupVertices, pageVertexStamp);
        }
        int pageCount = pageGroupStart.Count;
        pageGroupStart.Add(groupOrder.Length);
        int rootPageCount = 0;
        while (rootPageCount < pageCount && pageIsRoot[rootPageCount])
            rootPageCount++;

        // Cluster order: page by page, group by group, each group's clusters in payload order.
        int[] groupPage = new int[groups.Length];
        uint[] clusterOrder = new uint[clusterLods.Length];
        MeshletPageRecord[] pages = new MeshletPageRecord[pageCount];
        int write = 0;
        for (int p = 0; p < pageCount; p++)
        {
            int firstCluster = write;
            for (int i = pageGroupStart[p]; i < pageGroupStart[p + 1]; i++)
            {
                int g = pageGroups[i];
                groupPage[g] = p;
                for (int c = groupClusterStart[g]; c < groupClusterStart[g + 1]; c++)
                    clusterOrder[write++] = (uint)groupClusters[c];
            }
            pages[p] = new MeshletPageRecord((uint)firstCluster, (uint)(write - firstCluster), 0u, 0u);
        }

        MeshletClusterLodRecord[] clusterRecords = new MeshletClusterLodRecord[clusterLods.Length];
        for (int i = 0; i < clusterOrder.Length; i++)
            clusterRecords[i] = clusterLods[(int)clusterOrder[i]];

        MeshletLodGroupRecord[] pagedGroups = new MeshletLodGroupRecord[groups.Length];
        for (int g = 0; g < groups.Length; g++)
            pagedGroups[g] = groups[g] with { Reserved0 = (uint)groupPage[g] };

        uint[] dependencies = BuildDependencies(clusterLods, groupPage, pages);

        MeshletPageTable table = new()
        {
            Kind = skinnedTopology ? EMeshletPageFlags.SkinnedTopology : EMeshletPageFlags.None,
            RootPageCount = rootPageCount,
            ClusterOrder = clusterOrder,
            ClusterRecords = clusterRecords,
            Groups = pagedGroups,
            Pages = pages,
            Dependencies = dependencies,
        };

        byte[][] images = new byte[pageCount][];
        int[] localVertex = skinnedTopology ? [] : new int[vertexCount];
        int[] localVertexStamp = skinnedTopology ? [] : new int[vertexCount];
        long pageVertexTotal = 0;
        long pageBytes = 0;
        long rootPageBytes = 0;
        int maxDependencies = 0;
        for (int p = 0; p < pageCount; p++)
        {
            images[p] = WritePageImage(table, p, meshlets, vertexIndices, triangleBytes, packedVertices, localVertex, localVertexStamp);
            pageVertexTotal += MeshletPageHeader.Read(images[p]).VertexCount;
            pageBytes += images[p].Length;
            if (p < rootPageCount)
                rootPageBytes += images[p].Length;
            maxDependencies = Math.Max(maxDependencies, (int)pages[p].DependencyCount);
        }

        MeshletPageSet set = new(
            table,
            images,
            new MeshletPageSetStats(
                pageCount,
                rootPageCount,
                clusterLods.Length,
                groups.Length,
                pageVertexTotal,
                pageBytes,
                rootPageBytes,
                dependencies.Length,
                maxDependencies));
        Validate(set, meshlets, vertexIndices, triangleBytes, clusterLods, groups, packedVertices);
        return set;
    }

    /// <summary>
    /// Checks the rules of a page set against the payload streams that it was built from:
    /// <list type="bullet">
    /// <item>The cluster order is a permutation, and the cluster and group records match the payload.</item>
    /// <item>Every group is in exactly one page, with its clusters together. A root page holds only
    /// root groups, and a non-root page holds groups of one level.</item>
    /// <item>Each page has a contiguous cluster range of at most
    /// <see cref="MeshletPageFormat.MaxClustersPerPage"/> clusters and an image of at most
    /// <see cref="MeshletPageFormat.PageBytes"/> bytes.</item>
    /// <item>The dependencies of each page are exactly the pages that hold a parent of one of its
    /// groups, sorted. Root pages have no dependencies. Every dependency is a page of a coarser
    /// level or a root page, so the graph has no cycles and every path ends at a root page.</item>
    /// <item>Every page image has a valid header and in-range offsets, and every decoded triangle
    /// names the same source vertices, in the same order, as the payload triangle.</item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidDataException">The set breaks a rule. The message names the first broken rule.</exception>
    public static void Validate(
        MeshletPageSet set,
        ReadOnlySpan<CpuMeshletDescriptor> meshlets,
        ReadOnlySpan<uint> vertexIndices,
        ReadOnlySpan<byte> triangleBytes,
        ReadOnlySpan<MeshletClusterLodRecord> clusterLods,
        ReadOnlySpan<MeshletLodGroupRecord> groups,
        ReadOnlySpan<AdvancedDeformedVertex> packedVertices)
    {
        ArgumentNullException.ThrowIfNull(set);
        MeshletPageTable table = set.Table;
        bool skinned = (table.Kind & EMeshletPageFlags.SkinnedTopology) != 0;
        int clusterCount = clusterLods.Length;
        int pageCount = table.Pages.Length;

        if (table.ClusterOrder.Length != clusterCount || table.ClusterRecords.Length != clusterCount)
            Fail("The page tables do not have one entry for each cluster.");
        if (table.Groups.Length != groups.Length)
            Fail("The page tables do not have one record for each group.");
        if (set.PageImages.Length != pageCount)
            Fail("The page set does not have one image for each page.");
        if (pageCount == 0 || table.RootPageCount < 1 || table.RootPageCount > pageCount)
            Fail("The page set has no root page.");

        bool[] seen = new bool[clusterCount];
        for (int i = 0; i < clusterCount; i++)
        {
            uint source = table.ClusterOrder[i];
            if (source >= (uint)clusterCount || seen[source])
                Fail($"The cluster order is not a permutation at entry {i}.");
            seen[source] = true;
            if (table.ClusterRecords[i] != clusterLods[(int)source])
                Fail($"Cluster record {i} does not match payload meshlet {source}.");
        }

        for (int g = 0; g < groups.Length; g++)
        {
            MeshletLodGroupRecord paged = table.Groups[g];
            if (paged with { Reserved0 = groups[g].Reserved0 } != groups[g])
                Fail($"Group record {g} does not match the payload group.");
            if (paged.Reserved0 >= (uint)pageCount)
                Fail($"Group {g} names page {paged.Reserved0}, which is out of range.");
        }

        // Contiguous cluster ranges, groups in exactly one page with their clusters together, and
        // one bucket for each page.
        int[] pageLevel = new int[pageCount];
        int[] groupLastCluster = new int[groups.Length];
        Array.Fill(groupLastCluster, -1);
        uint expectedFirst = 0u;
        for (int p = 0; p < pageCount; p++)
        {
            MeshletPageRecord page = table.Pages[p];
            if (page.FirstCluster != expectedFirst)
                Fail($"Page {p} does not start where the previous page ends.");
            if (page.ClusterCount == 0u || page.ClusterCount > MeshletPageFormat.MaxClustersPerPage)
                Fail($"Page {p} has {page.ClusterCount} clusters.");
            expectedFirst += page.ClusterCount;
            if ((ulong)expectedFirst > (ulong)clusterCount)
                Fail($"Page {p} ends past the last cluster.");
            if (set.PageImages[p] is null || set.PageImages[p].Length > MeshletPageFormat.PageBytes)
                Fail($"The image of page {p} is missing or larger than a page.");

            bool root = table.IsRootPage(p);
            int level = int.MinValue;
            for (uint i = page.FirstCluster; i < page.FirstCluster + page.ClusterCount; i++)
            {
                int group = table.ClusterRecords[i].Group;
                if (table.GetGroupPage(group) != p)
                    Fail($"Cluster {i} is in page {p}, but its group {group} names page {table.GetGroupPage(group)}.");
                if (groupLastCluster[group] >= 0 && groupLastCluster[group] != (int)i - 1)
                    Fail($"The clusters of group {group} are not together.");
                groupLastCluster[group] = (int)i;

                MeshletLodGroupRecord record = groups[group];
                if (IsRoot(record) != root)
                    Fail($"Page {p} mixes root and non-root groups, or is out of the root page range.");
                int groupLevel = root ? int.MaxValue : record.Depth;
                if (level != int.MinValue && level != groupLevel)
                    Fail($"Page {p} holds groups of more than one level.");
                level = groupLevel;
            }
            pageLevel[p] = level;
        }
        if (expectedFirst != (uint)clusterCount)
            Fail("The pages do not hold every cluster.");

        // Dependencies: exactly the parent pages, sorted, and always toward a coarser level.
        uint[] expected = BuildDependencies(clusterLods, GroupPages(table), table.Pages, writeRanges: false, out uint[] expectedStart);
        for (int p = 0; p < pageCount; p++)
        {
            MeshletPageRecord page = table.Pages[p];
            if ((ulong)page.FirstDependency + page.DependencyCount > (ulong)table.Dependencies.Length)
                Fail($"The dependency range of page {p} is out of range.");
            ReadOnlySpan<uint> actual = table.GetDependencies(p);
            ReadOnlySpan<uint> wanted = expected.AsSpan((int)expectedStart[p], (int)(expectedStart[p + 1] - expectedStart[p]));
            if (!actual.SequenceEqual(wanted))
                Fail($"Page {p} does not list exactly the pages of the parents of its groups.");
            if (table.IsRootPage(p) && actual.Length != 0)
                Fail($"Root page {p} has dependencies.");
            foreach (uint dependency in actual)
                if (dependency >= (uint)pageCount || pageLevel[dependency] <= pageLevel[p])
                    Fail($"Page {p} depends on page {dependency}, which is not coarser.");
        }

        for (int p = 0; p < pageCount; p++)
            ValidatePageImage(set, p, skinned, meshlets, vertexIndices, triangleBytes, packedVertices);
    }

    /// <summary>
    /// Checks the inputs of <see cref="Build(ReadOnlySpan{CpuMeshletDescriptor}, ReadOnlySpan{uint}, ReadOnlySpan{byte}, ReadOnlySpan{MeshletClusterLodRecord}, ReadOnlySpan{MeshletLodGroupRecord}, int, ReadOnlySpan{AdvancedDeformedVertex}, bool)"/>.
    /// </summary>
    private static void CheckInputs(
        ReadOnlySpan<CpuMeshletDescriptor> meshlets,
        ReadOnlySpan<uint> vertexIndices,
        ReadOnlySpan<byte> triangleBytes,
        ReadOnlySpan<MeshletClusterLodRecord> clusterLods,
        ReadOnlySpan<MeshletLodGroupRecord> groups,
        int vertexCount,
        ReadOnlySpan<AdvancedDeformedVertex> packedVertices,
        bool skinnedTopology)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        if (meshlets.IsEmpty || groups.IsEmpty)
            throw new InvalidDataException("A page cook needs at least one meshlet and one group.");
        if (clusterLods.Length != meshlets.Length)
            throw new InvalidDataException("A page cook needs one hierarchy record for each meshlet.");
        if (skinnedTopology ? !packedVertices.IsEmpty : packedVertices.Length != vertexCount)
            throw new ArgumentException(
                skinnedTopology
                    ? "Skinned topology pages take no packed vertices."
                    : "Static pages need one packed vertex for each source vertex.",
                nameof(packedVertices));

        for (int m = 0; m < meshlets.Length; m++)
        {
            CpuMeshletDescriptor meshlet = meshlets[m];
            if (meshlet.TriangleCount == 0u || meshlet.TriangleCount > MeshletPageFormat.MaxTrianglesPerCluster)
                throw new InvalidDataException($"Meshlet {m} has {meshlet.TriangleCount} triangles.");
            if ((ulong)meshlet.VertexOffset + meshlet.VertexCount > (ulong)vertexIndices.Length)
                throw new InvalidDataException($"Meshlet {m} has a vertex range outside the vertex index stream.");
            if ((ulong)meshlet.TriangleOffset + meshlet.TriangleCount * 3UL > (ulong)triangleBytes.Length)
                throw new InvalidDataException($"Meshlet {m} has a triangle range outside the triangle stream.");
            foreach (uint vertex in vertexIndices.Slice((int)meshlet.VertexOffset, (int)meshlet.VertexCount))
                if (vertex >= (uint)vertexCount)
                    throw new InvalidDataException($"Meshlet {m} refers to vertex {vertex}, which is out of range.");
            foreach (byte local in triangleBytes.Slice((int)meshlet.TriangleOffset, (int)meshlet.TriangleCount * 3))
                if (local >= meshlet.VertexCount)
                    throw new InvalidDataException($"Meshlet {m} has a triangle corner outside its vertices.");

            MeshletClusterLodRecord record = clusterLods[m];
            if ((uint)record.Group >= (uint)groups.Length)
                throw new InvalidDataException($"Meshlet {m} names group {record.Group}, which is out of range.");
            if (record.RefinedGroup < -1 || record.RefinedGroup >= groups.Length)
                throw new InvalidDataException($"Meshlet {m} names refined group {record.RefinedGroup}, which is out of range.");
            if (record.RefinedGroup >= 0 && IsRoot(groups[record.RefinedGroup]))
                throw new InvalidDataException($"Meshlet {m} was made from root group {record.RefinedGroup}.");
        }

        for (int g = 0; g < groups.Length; g++)
            if (groups[g].Depth < 0)
                throw new InvalidDataException($"Cluster hierarchy group {g} has a negative depth.");
    }

    /// <summary>
    /// Returns the groups in packing order: root groups first, then the other groups level by
    /// level. Inside one bucket, groups are sorted by the Morton code of their sphere center, then
    /// by group index, so the order is the same on every machine.
    /// </summary>
    private static int[] SortGroupsForPacking(ReadOnlySpan<MeshletLodGroupRecord> groups)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int g = 0; g < groups.Length; g++)
        {
            Vector3 center = Center(groups[g]);
            min = Vector3.Min(min, center);
            max = Vector3.Max(max, center);
        }

        Vector3 extent = max - min;
        float scale = (1 << MortonBitsPerAxis) - 1;
        Vector3 inverseExtent = new(
            extent.X > 0.0f ? scale / extent.X : 0.0f,
            extent.Y > 0.0f ? scale / extent.Y : 0.0f,
            extent.Z > 0.0f ? scale / extent.Z : 0.0f);

        int[] buckets = new int[groups.Length];
        uint[] codes = new uint[groups.Length];
        int[] order = new int[groups.Length];
        for (int g = 0; g < groups.Length; g++)
        {
            Vector3 q = Vector3.Clamp((Center(groups[g]) - min) * inverseExtent, Vector3.Zero, new Vector3(scale));
            buckets[g] = BucketOf(groups[g]);
            codes[g] = Morton3((uint)q.X, (uint)q.Y, (uint)q.Z);
            order[g] = g;
        }

        Array.Sort(order, (a, b) =>
        {
            int compare = buckets[a].CompareTo(buckets[b]);
            if (compare != 0)
                return compare;
            compare = codes[a].CompareTo(codes[b]);
            return compare != 0 ? compare : a.CompareTo(b);
        });
        return order;
    }

    /// <summary>
    /// Returns the packing bucket of a group: -1 for a root group, otherwise its depth. Root groups
    /// sort first, so the root pages are the first pages.
    /// </summary>
    private static int BucketOf(MeshletLodGroupRecord group)
        => IsRoot(group) ? -1 : group.Depth;

    /// <summary>
    /// Counts the distinct vertices of group <paramref name="group"/> that page
    /// <paramref name="pageStamp"/> does not hold yet. Always zero for skinned topology pages.
    /// </summary>
    private static int CountNewPageVertices(int group, int pageStamp, int[] groupVertexStart, List<uint> groupVertices, int[]? pageVertexStamp)
    {
        if (pageVertexStamp is null)
            return 0;
        int count = 0;
        for (int i = groupVertexStart[group]; i < groupVertexStart[group + 1]; i++)
            if (pageVertexStamp[groupVertices[i]] != pageStamp)
                count++;
        return count;
    }

    /// <summary>Marks the distinct vertices of a group as held by page <paramref name="pageStamp"/>.</summary>
    private static void MarkPageVertices(int group, int pageStamp, int[] groupVertexStart, List<uint> groupVertices, int[]? pageVertexStamp)
    {
        if (pageVertexStamp is null)
            return;
        for (int i = groupVertexStart[group]; i < groupVertexStart[group + 1]; i++)
            pageVertexStamp[groupVertices[i]] = pageStamp;
    }

    /// <summary>
    /// Builds the dependency table and writes the dependency range of each page record. A page
    /// depends on the page of the group of each cluster that was made from one of its groups.
    /// </summary>
    private static uint[] BuildDependencies(ReadOnlySpan<MeshletClusterLodRecord> clusterLods, int[] groupPage, MeshletPageRecord[] pages)
        => BuildDependencies(clusterLods, groupPage, pages, writeRanges: true, out _);

    /// <summary>
    /// Builds the dependency table. <paramref name="pageStart"/> receives the first dependency of
    /// each page and, in its last entry, the table length. When <paramref name="writeRanges"/> is
    /// true, the method also writes the range of each page into <paramref name="pages"/>.
    /// </summary>
    private static uint[] BuildDependencies(
        ReadOnlySpan<MeshletClusterLodRecord> clusterLods,
        int[] groupPage,
        MeshletPageRecord[] pages,
        bool writeRanges,
        out uint[] pageStart)
    {
        // Each edge is (child page << 32) | parent page; sorting groups the edges by child page
        // and sorts the parents of each child.
        List<ulong> edges = new(clusterLods.Length);
        for (int m = 0; m < clusterLods.Length; m++)
        {
            MeshletClusterLodRecord record = clusterLods[m];
            if (record.RefinedGroup < 0)
                continue;
            int child = groupPage[record.RefinedGroup];
            int parent = groupPage[record.Group];
            if (child == parent)
                throw new InvalidDataException($"Meshlet {m} and the group that it was made from are in the same page {child}.");
            edges.Add(((ulong)(uint)child << 32) | (uint)parent);
        }
        edges.Sort();

        List<uint> dependencies = new(edges.Count);
        pageStart = new uint[pages.Length + 1];
        int edge = 0;
        for (int p = 0; p < pages.Length; p++)
        {
            pageStart[p] = (uint)dependencies.Count;
            ulong previous = ulong.MaxValue;
            while (edge < edges.Count && (int)(edges[edge] >> 32) == p)
            {
                if (edges[edge] != previous)
                    dependencies.Add((uint)edges[edge]);
                previous = edges[edge];
                edge++;
            }
            if (writeRanges)
                pages[p] = pages[p] with
                {
                    FirstDependency = pageStart[p],
                    DependencyCount = (uint)dependencies.Count - pageStart[p],
                };
        }
        pageStart[pages.Length] = (uint)dependencies.Count;
        return [.. dependencies];
    }

    /// <summary>Returns the page of each group from <see cref="MeshletLodGroupRecord.Reserved0"/>.</summary>
    private static int[] GroupPages(MeshletPageTable table)
    {
        int[] pages = new int[table.Groups.Length];
        for (int g = 0; g < pages.Length; g++)
            pages[g] = table.GetGroupPage(g);
        return pages;
    }

    /// <summary>
    /// Writes the GPU image of one page (see <see cref="MeshletPageFormat"/>). For a static page,
    /// the page-local vertices are numbered in the order in which the clusters first use them.
    /// </summary>
    private static byte[] WritePageImage(
        MeshletPageTable table,
        int page,
        ReadOnlySpan<CpuMeshletDescriptor> meshlets,
        ReadOnlySpan<uint> vertexIndices,
        ReadOnlySpan<byte> triangleBytes,
        ReadOnlySpan<AdvancedDeformedVertex> packedVertices,
        int[] localVertex,
        int[] localVertexStamp)
    {
        bool skinned = (table.Kind & EMeshletPageFlags.SkinnedTopology) != 0;
        MeshletPageRecord record = table.Pages[page];
        int first = (int)record.FirstCluster;
        int count = (int)record.ClusterCount;

        int vertexRefs = 0;
        int triangleByteCount = 0;
        int pageVertexCount = 0;
        int stamp = page + 1;
        for (int i = first; i < first + count; i++)
        {
            CpuMeshletDescriptor meshlet = meshlets[(int)table.ClusterOrder[i]];
            vertexRefs += (int)meshlet.VertexCount;
            triangleByteCount += MeshletPageFormat.ClusterTriangleBytes(meshlet.TriangleCount);
            if (skinned)
                continue;
            foreach (uint vertex in vertexIndices.Slice((int)meshlet.VertexOffset, (int)meshlet.VertexCount))
            {
                if (localVertexStamp[vertex] == stamp)
                    continue;
                localVertexStamp[vertex] = stamp;
                localVertex[vertex] = pageVertexCount++;
            }
        }

        int descriptorOffset = MeshletPageFormat.HeaderBytes;
        int vertexIndexOffset = descriptorOffset + MeshletPageFormat.AlignSection(count * MeshletPageFormat.DescriptorBytes);
        int triangleOffset = vertexIndexOffset + MeshletPageFormat.AlignSection(vertexRefs * MeshletPageFormat.VertexIndexBytes);
        int vertexOffset = triangleOffset + MeshletPageFormat.AlignSection(triangleByteCount);
        int imageBytes = vertexOffset + pageVertexCount * MeshletPageFormat.PackedVertexBytes;
        if (imageBytes > MeshletPageFormat.PageBytes)
            throw new InvalidDataException($"Page {page} needs {imageBytes} bytes.");

        byte[] image = new byte[imageBytes];
        EMeshletPageFlags flags = table.Kind | (table.IsRootPage(page) ? EMeshletPageFlags.Root : EMeshletPageFlags.None);
        new MeshletPageHeader(
            MeshletPageFormat.Magic,
            MeshletPageFormat.Version,
            flags,
            (ushort)count,
            (ushort)pageVertexCount,
            record.FirstCluster,
            (uint)descriptorOffset,
            (uint)vertexIndexOffset,
            (uint)triangleOffset,
            (uint)vertexOffset).Write(image);

        Span<AdvancedMeshletDescriptor> descriptors = MemoryMarshal.Cast<byte, AdvancedMeshletDescriptor>(
            image.AsSpan(descriptorOffset, count * MeshletPageFormat.DescriptorBytes));
        Span<uint> pageVertexIndices = MemoryMarshal.Cast<byte, uint>(
            image.AsSpan(vertexIndexOffset, vertexRefs * MeshletPageFormat.VertexIndexBytes));
        Span<byte> pageTriangles = image.AsSpan(triangleOffset, triangleByteCount);
        Span<AdvancedDeformedVertex> pageVertices = MemoryMarshal.Cast<byte, AdvancedDeformedVertex>(
            image.AsSpan(vertexOffset, pageVertexCount * MeshletPageFormat.PackedVertexBytes));

        int vertexWrite = 0;
        int triangleWrite = 0;
        for (int k = 0; k < count; k++)
        {
            CpuMeshletDescriptor meshlet = meshlets[(int)table.ClusterOrder[first + k]];
            descriptors[k] = new AdvancedMeshletDescriptor
            {
                BoundsSphere = meshlet.BoundsSphere,
                VertexOffset = (uint)vertexWrite,
                TriangleByteOffset = (uint)triangleWrite,
                VertexCount = meshlet.VertexCount,
                TriangleCount = meshlet.TriangleCount,
                Cone = meshlet.Cone,
                ConeApex = meshlet.ConeApex,
                PackedCone = meshlet.PackedCone,
            };

            ReadOnlySpan<uint> clusterVertices = vertexIndices.Slice((int)meshlet.VertexOffset, (int)meshlet.VertexCount);
            for (int j = 0; j < clusterVertices.Length; j++)
            {
                uint source = clusterVertices[j];
                if (skinned)
                {
                    pageVertexIndices[vertexWrite + j] = source;
                    continue;
                }

                int local = localVertex[source];
                pageVertexIndices[vertexWrite + j] = (uint)local;
                AdvancedDeformedVertex vertex = packedVertices[(int)source];
                vertex.SourceVertex = source;
                pageVertices[local] = vertex;
            }

            triangleBytes.Slice((int)meshlet.TriangleOffset, (int)meshlet.TriangleCount * 3).CopyTo(pageTriangles[triangleWrite..]);
            vertexWrite += clusterVertices.Length;
            triangleWrite += MeshletPageFormat.ClusterTriangleBytes(meshlet.TriangleCount);
        }

        return image;
    }

    /// <summary>
    /// Checks one page image: the header, the section offsets, the offsets of each descriptor, and
    /// that each decoded triangle names the same source vertices as the payload triangle.
    /// </summary>
    private static void ValidatePageImage(
        MeshletPageSet set,
        int page,
        bool skinned,
        ReadOnlySpan<CpuMeshletDescriptor> meshlets,
        ReadOnlySpan<uint> vertexIndices,
        ReadOnlySpan<byte> triangleBytes,
        ReadOnlySpan<AdvancedDeformedVertex> packedVertices)
    {
        MeshletPageTable table = set.Table;
        MeshletPageRecord record = table.Pages[page];
        byte[] image = set.PageImages[page];
        MeshletPageHeader header;
        try
        {
            header = MeshletPageHeader.Read(image);
        }
        catch (InvalidDataException exception)
        {
            Fail($"Page {page}: {exception.Message}");
            return;
        }

        EMeshletPageFlags expectedFlags = table.Kind | (table.IsRootPage(page) ? EMeshletPageFlags.Root : EMeshletPageFlags.None);
        if (header.Flags != expectedFlags || header.ClusterCount != record.ClusterCount || header.FirstCluster != record.FirstCluster)
            Fail($"The header of page {page} does not match its page record.");
        if (skinned && header.VertexCount != 0)
            Fail($"Skinned topology page {page} holds vertices.");

        int count = header.ClusterCount;
        long descriptorEnd = header.DescriptorOffset + (long)count * MeshletPageFormat.DescriptorBytes;
        if (header.DescriptorOffset != MeshletPageFormat.HeaderBytes ||
            header.VertexIndexOffset != MeshletPageFormat.AlignSection((int)descriptorEnd) ||
            header.TriangleOffset < header.VertexIndexOffset ||
            header.VertexOffset < header.TriangleOffset ||
            (header.VertexIndexOffset | header.TriangleOffset | header.VertexOffset) % MeshletPageFormat.SectionAlignment != 0 ||
            header.VertexOffset + (long)header.VertexCount * MeshletPageFormat.PackedVertexBytes != image.Length)
            Fail($"The section offsets of page {page} are not valid.");

        ReadOnlySpan<AdvancedMeshletDescriptor> descriptors = MemoryMarshal.Cast<byte, AdvancedMeshletDescriptor>(
            image.AsSpan((int)header.DescriptorOffset, count * MeshletPageFormat.DescriptorBytes));
        ReadOnlySpan<uint> pageVertexIndices = MemoryMarshal.Cast<byte, uint>(
            image.AsSpan((int)header.VertexIndexOffset, (int)(header.TriangleOffset - header.VertexIndexOffset)));
        ReadOnlySpan<byte> pageTriangles = image.AsSpan((int)header.TriangleOffset, (int)(header.VertexOffset - header.TriangleOffset));
        ReadOnlySpan<AdvancedDeformedVertex> pageVertices = MemoryMarshal.Cast<byte, AdvancedDeformedVertex>(
            image.AsSpan((int)header.VertexOffset, header.VertexCount * MeshletPageFormat.PackedVertexBytes));

        for (int k = 0; k < count; k++)
        {
            int cluster = (int)record.FirstCluster + k;
            CpuMeshletDescriptor source = meshlets[(int)table.ClusterOrder[cluster]];
            AdvancedMeshletDescriptor descriptor = descriptors[k];
            if (descriptor.VertexCount != source.VertexCount ||
                descriptor.TriangleCount != source.TriangleCount ||
                descriptor.BoundsSphere != source.BoundsSphere ||
                (ulong)descriptor.VertexOffset + descriptor.VertexCount > (ulong)pageVertexIndices.Length ||
                (ulong)descriptor.TriangleByteOffset + descriptor.TriangleCount * 3UL > (ulong)pageTriangles.Length)
                Fail($"The descriptor of cluster {cluster} in page {page} is not valid.");

            for (int corner = 0; corner < (int)source.TriangleCount * 3; corner++)
            {
                byte local = pageTriangles[(int)descriptor.TriangleByteOffset + corner];
                if (local >= descriptor.VertexCount)
                    Fail($"Cluster {cluster} in page {page} has a triangle corner outside its vertices.");
                uint pageIndex = pageVertexIndices[(int)descriptor.VertexOffset + local];
                uint decoded;
                if (skinned)
                {
                    decoded = pageIndex;
                }
                else
                {
                    if (pageIndex >= header.VertexCount)
                        Fail($"Cluster {cluster} in page {page} names page vertex {pageIndex}, which is out of range.");
                    AdvancedDeformedVertex vertex = pageVertices[(int)pageIndex];
                    decoded = vertex.SourceVertex;
                    if (decoded >= (uint)packedVertices.Length || !SameVertexData(vertex, packedVertices[(int)decoded]))
                        Fail($"Page vertex {pageIndex} of page {page} does not match its source vertex.");
                }

                uint expected = vertexIndices[(int)source.VertexOffset + triangleBytes[(int)source.TriangleOffset + corner]];
                if (decoded != expected)
                    Fail($"Cluster {cluster} in page {page} decodes to source vertex {decoded} at corner {corner}; the payload has {expected}.");
            }
        }
    }

    /// <summary>
    /// True when two packed vertices hold the same bytes, except <see cref="AdvancedDeformedVertex.SourceVertex"/>.
    /// </summary>
    private static bool SameVertexData(AdvancedDeformedVertex a, AdvancedDeformedVertex b)
    {
        a.SourceVertex = b.SourceVertex;
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref a, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref b, 1)));
    }

    /// <summary>True for a root group: a group that the cook could not simplify further.</summary>
    private static bool IsRoot(MeshletLodGroupRecord group)
        => group.SimplifiedError >= float.MaxValue;

    private static Vector3 Center(MeshletLodGroupRecord group)
        => new(group.SimplifiedSphere.X, group.SimplifiedSphere.Y, group.SimplifiedSphere.Z);

    /// <summary>Interleaves the low 10 bits of three coordinates into a 30-bit Morton code.</summary>
    private static uint Morton3(uint x, uint y, uint z)
        => Spread(x) | (Spread(y) << 1) | (Spread(z) << 2);

    /// <summary>Spreads the low 10 bits of a value so that two zero bits follow each bit.</summary>
    private static uint Spread(uint value)
    {
        value &= 0x3FFu;
        value = (value | (value << 16)) & 0x030000FFu;
        value = (value | (value << 8)) & 0x0300F00Fu;
        value = (value | (value << 4)) & 0x030C30C3u;
        value = (value | (value << 2)) & 0x09249249u;
        return value;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message)
        => throw new InvalidDataException(message);
}
