namespace XREngine;

/// <summary>
/// Exact visibility similarity and geometry-work estimates for one view batch.
/// Counts are candidate-view tests, not CPU readbacks of GPU production data.
/// </summary>
public readonly record struct ViewBatchMaskSimilarity(
    ulong ActiveViewMask,
    uint CandidateCount,
    uint UnionCount,
    uint IntersectionCount,
    ulong CombinedGeometryWork,
    ulong SplitGeometryWork)
{
    public int ViewCount => checked((int)ulong.PopCount(ActiveViewMask));
    public float JaccardSimilarity
        => UnionCount == 0u ? 1.0f : (float)IntersectionCount / UnionCount;
    public ulong SavedGeometryWork
        => CombinedGeometryWork > SplitGeometryWork
            ? CombinedGeometryWork - SplitGeometryWork
            : 0UL;
}
