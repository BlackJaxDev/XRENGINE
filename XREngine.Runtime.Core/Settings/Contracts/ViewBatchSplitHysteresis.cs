namespace XREngine;

/// <summary>
/// Fixed-capacity persistent split state keyed by structural batch identity.
/// </summary>
public sealed class ViewBatchSplitHysteresis
{
    private readonly Entry[] _entries;
    private readonly ViewBatchSplitPolicy _policy;
    private uint _evaluationGeneration;

    public ViewBatchSplitHysteresis(
        int batchCapacity,
        ViewBatchSplitPolicy? policy = null)
    {
        if (batchCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(batchCapacity));
        _entries = new Entry[batchCapacity];
        _policy = (policy ?? ViewBatchSplitPolicy.Default).Validate();
    }

    public ViewBatchSplitDecision Evaluate(
        ulong stableBatchKey,
        in ViewBatchMaskSimilarity similarity)
    {
        if (stableBatchKey == 0UL)
            throw new ArgumentOutOfRangeException(nameof(stableBatchKey));

        _evaluationGeneration++;
        int index = Find(stableBatchKey);
        if (index < 0)
            index = FindReplacement();

        EViewBatchTopology previous = _entries[index].Valid
            ? _entries[index].Topology
            : EViewBatchTopology.Combined;
        double savedCost = similarity.SavedGeometryWork * _policy.GeometryWorkCost;
        double enterCost = _policy.AddedSubmissionCost * _policy.SplitEnterRatio;
        double exitCost = _policy.AddedSubmissionCost * _policy.SplitExitRatio;

        EViewBatchTopology topology;
        EViewBatchSplitReason reason;
        if (similarity.CandidateCount < _policy.MinimumCandidateSamples)
        {
            topology = previous;
            reason = EViewBatchSplitReason.InsufficientSamples;
        }
        else if (previous == EViewBatchTopology.Combined && savedCost > enterCost)
        {
            topology = EViewBatchTopology.SplitPerView;
            reason = EViewBatchSplitReason.SavedGeometryExceedsSubmissionCost;
        }
        else if (previous == EViewBatchTopology.SplitPerView && savedCost >= exitCost)
        {
            topology = EViewBatchTopology.SplitPerView;
            reason = EViewBatchSplitReason.SplitHysteresisRetained;
        }
        else if (previous == EViewBatchTopology.SplitPerView)
        {
            topology = EViewBatchTopology.Combined;
            reason = EViewBatchSplitReason.CombinedHysteresisRestored;
        }
        else
        {
            topology = EViewBatchTopology.Combined;
            reason = EViewBatchSplitReason.CombinedPreferred;
        }

        _entries[index] = new(stableBatchKey, topology, _evaluationGeneration, true);
        return new(
            stableBatchKey,
            topology,
            reason,
            similarity,
            savedCost,
            _policy.AddedSubmissionCost);
    }

    private int Find(ulong stableBatchKey)
    {
        for (int i = 0; i < _entries.Length; i++)
            if (_entries[i].Valid && _entries[i].StableBatchKey == stableBatchKey)
                return i;
        return -1;
    }

    private int FindReplacement()
    {
        int oldest = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (!_entries[i].Valid)
                return i;
            if (_entries[i].LastEvaluationGeneration < _entries[oldest].LastEvaluationGeneration)
                oldest = i;
        }
        return oldest;
    }

    private readonly record struct Entry(
        ulong StableBatchKey,
        EViewBatchTopology Topology,
        uint LastEvaluationGeneration,
        bool Valid);
}
