namespace XREngine.Rendering.Meshlets;

/// <summary>The totals of the page cook of one mesh.</summary>
/// <param name="PageCount">The pages of the mesh.</param>
/// <param name="RootPageCount">The root pages of the mesh.</param>
/// <param name="ClusterCount">The clusters of every level.</param>
/// <param name="GroupCount">The groups of every level.</param>
/// <param name="PageVertexCount">The sum of the page-local vertex counts (zero for a skinned mesh).</param>
/// <param name="PageBytes">The sum of the used bytes of every page image.</param>
/// <param name="RootPageBytes">The sum of the used bytes of the root page images.</param>
/// <param name="DependencyCount">The entries of the dependency table.</param>
/// <param name="MaxDependencies">The most dependencies of one page.</param>
public readonly record struct MeshletPageSetStats(
    int PageCount,
    int RootPageCount,
    int ClusterCount,
    int GroupCount,
    long PageVertexCount,
    long PageBytes,
    long RootPageBytes,
    int DependencyCount,
    int MaxDependencies)
{
    /// <summary>The average share of a page slot that the page images use.</summary>
    public double AverageFill
        => PageCount == 0 ? 0.0 : (double)PageBytes / ((long)PageCount * MeshletPageFormat.PageBytes);

    /// <summary>Adds the totals of two page cooks. <see cref="MaxDependencies"/> takes the larger value.</summary>
    public static MeshletPageSetStats operator +(MeshletPageSetStats a, MeshletPageSetStats b)
        => new(
            a.PageCount + b.PageCount,
            a.RootPageCount + b.RootPageCount,
            a.ClusterCount + b.ClusterCount,
            a.GroupCount + b.GroupCount,
            a.PageVertexCount + b.PageVertexCount,
            a.PageBytes + b.PageBytes,
            a.RootPageBytes + b.RootPageBytes,
            a.DependencyCount + b.DependencyCount,
            Math.Max(a.MaxDependencies, b.MaxDependencies));
}
