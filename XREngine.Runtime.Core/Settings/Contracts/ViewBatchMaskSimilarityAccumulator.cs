namespace XREngine;

/// <summary>
/// Allocation-free accumulator for exact candidate masks in a planned batch.
/// </summary>
public struct ViewBatchMaskSimilarityAccumulator
{
    private readonly ulong _activeViewMask;
    private readonly int _viewCount;
    private uint _candidateCount;
    private uint _unionCount;
    private uint _intersectionCount;
    private ulong _splitGeometryWork;

    public ViewBatchMaskSimilarityAccumulator(ulong activeViewMask)
    {
        if (activeViewMask == 0UL)
            throw new ArgumentOutOfRangeException(nameof(activeViewMask));
        _activeViewMask = activeViewMask;
        _viewCount = checked((int)ulong.PopCount(activeViewMask));
    }

    public void Add(ulong exactCandidateViewMask)
    {
        _candidateCount++;
        ulong relevantMask = exactCandidateViewMask & _activeViewMask;
        if (relevantMask == 0UL)
            return;

        _unionCount++;
        int visibleViewCount = checked((int)ulong.PopCount(relevantMask));
        _splitGeometryWork += (uint)visibleViewCount;
        if (relevantMask == _activeViewMask)
            _intersectionCount++;
    }

    public ViewBatchMaskSimilarity Build()
        => new(
            _activeViewMask,
            _candidateCount,
            _unionCount,
            _intersectionCount,
            (ulong)_unionCount * (uint)_viewCount,
            _splitGeometryWork);
}
