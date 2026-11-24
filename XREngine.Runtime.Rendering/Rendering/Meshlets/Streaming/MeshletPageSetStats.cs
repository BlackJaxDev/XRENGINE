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

    /// <summary>
    /// Measures the totals of a page set from its tables and page images, for a set that was read
    /// back from a page file instead of cooked.
    /// </summary>
    public static MeshletPageSetStats Measure(MeshletPageTable table, byte[][] pageImages)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(pageImages);
        long pageVertices = 0L;
        long pageBytes = 0L;
        long rootPageBytes = 0L;
        int maxDependencies = 0;
        for (int page = 0; page < pageImages.Length; page++)
        {
            byte[] image = pageImages[page];
            pageVertices += MeshletPageHeader.Read(image).VertexCount;
            pageBytes += image.Length;
            if (table.IsRootPage(page))
                rootPageBytes += image.Length;
            maxDependencies = Math.Max(maxDependencies, (int)table.Pages[page].DependencyCount);
        }

        return new MeshletPageSetStats(
            table.PageCount,
            table.RootPageCount,
            table.ClusterCount,
            table.Groups.Length,
            pageVertices,
            pageBytes,
            rootPageBytes,
            table.Dependencies.Length,
            maxDependencies);
    }

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
