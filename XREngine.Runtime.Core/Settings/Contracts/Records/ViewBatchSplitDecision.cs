namespace XREngine;

public readonly record struct ViewBatchSplitDecision(
    ulong StableBatchKey,
    EViewBatchTopology Topology,
    EViewBatchSplitReason Reason,
    ViewBatchMaskSimilarity Similarity,
    double SavedGeometryCost,
    double AddedSubmissionCost);
