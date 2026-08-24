namespace XREngine.Execution;

/// <summary>
/// Fault information supplied to a batch executor so native artifacts touched
/// by the failed generation can be quarantined instead of submitted.
/// </summary>
public readonly record struct RenderWorkBatchFaultContext(
    long Generation,
    int FrameSlot,
    int FaultingItemIndex,
    int LaneId,
    Exception Exception);
