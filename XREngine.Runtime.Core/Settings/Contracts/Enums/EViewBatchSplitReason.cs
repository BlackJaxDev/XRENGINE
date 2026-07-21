namespace XREngine;

public enum EViewBatchSplitReason : byte
{
    CombinedPreferred,
    SavedGeometryExceedsSubmissionCost,
    SplitHysteresisRetained,
    CombinedHysteresisRestored,
    InsufficientSamples,
}
