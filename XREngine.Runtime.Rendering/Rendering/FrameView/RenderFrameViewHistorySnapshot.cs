namespace XREngine.Rendering;

/// <summary>
/// Read-only scalar diagnostics for one viewport's desktop temporal-history ledger.
/// </summary>
public readonly record struct RenderFrameViewHistorySnapshot(
    ulong Generation,
    bool CommittedHistoryValid,
    ulong CommittedSequence,
    ulong CommittedSourceFrame,
    int PendingCount,
    ulong PendingMinimumSequence,
    ulong PendingMaximumSequence,
    ulong EffectiveCommitCount,
    ulong EffectiveDiscardCount);
