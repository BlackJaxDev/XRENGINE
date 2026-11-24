namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The resident metadata of one paged cluster hierarchy mesh. The runtime keeps these tables
/// resident for as long as the mesh is registered, also while its pages are not resident. The cut
/// reads the cluster and group records to find the clusters that a view needs, and the streaming
/// manager reads the page records and dependencies to load pages in a valid order.
/// </summary>
/// <remarks>
/// <para>
/// The clusters are in page order: page <c>p</c> holds the geometry-local clusters
/// [<c>Pages[p].FirstCluster</c>, <c>Pages[p].FirstCluster + Pages[p].ClusterCount</c>), and the
/// clusters of one group are contiguous in their page. <see cref="ClusterOrder"/> maps each
/// geometry-local cluster back to its meshlet in the cluster hierarchy payload.
/// </para>
/// <para>
/// The group order is the same as in the payload, so the group fields of the cluster records keep
/// their values. <see cref="MeshletLodGroupRecord.Reserved0"/> of each group holds the
/// geometry-local page that holds the group.
/// </para>
/// <para>
/// The root pages come first: pages [0, <see cref="RootPageCount"/>) hold only root groups and
/// have no dependencies. Residency is closed toward the roots: when a page is resident, every page
/// in its dependency range is resident.
/// </para>
/// </remarks>
public sealed class MeshletPageTable
{
    /// <summary>Gets <see cref="EMeshletPageFlags.SkinnedTopology"/> for a skinned mesh, or <see cref="EMeshletPageFlags.None"/> for a static mesh.</summary>
    public required EMeshletPageFlags Kind { get; init; }

    /// <summary>Gets the number of root pages. They are the first pages of <see cref="Pages"/>.</summary>
    public required int RootPageCount { get; init; }

    /// <summary>Gets, for each geometry-local cluster, the index of its meshlet in the payload.</summary>
    public required uint[] ClusterOrder { get; init; }

    /// <summary>Gets the hierarchy record of each geometry-local cluster.</summary>
    public required MeshletClusterLodRecord[] ClusterRecords { get; init; }

    /// <summary>Gets the groups, in payload order, with the page of each group in <see cref="MeshletLodGroupRecord.Reserved0"/>.</summary>
    public required MeshletLodGroupRecord[] Groups { get; init; }

    /// <summary>Gets the page records.</summary>
    public required MeshletPageRecord[] Pages { get; init; }

    /// <summary>Gets the dependency table. Each page record names a sorted range of geometry-local page indices in it.</summary>
    public required uint[] Dependencies { get; init; }

    /// <summary>Gets the number of pages.</summary>
    public int PageCount => Pages.Length;

    /// <summary>Gets the number of geometry-local clusters.</summary>
    public int ClusterCount => ClusterRecords.Length;

    /// <summary>Gets the dependencies of one page.</summary>
    public ReadOnlySpan<uint> GetDependencies(int page)
    {
        MeshletPageRecord record = Pages[page];
        return Dependencies.AsSpan((int)record.FirstDependency, (int)record.DependencyCount);
    }

    /// <summary>Gets the page that holds a group.</summary>
    public int GetGroupPage(int group)
        => (int)Groups[group].Reserved0;

    /// <summary>True when the page is a root page.</summary>
    public bool IsRootPage(int page)
        => page < RootPageCount;
}
