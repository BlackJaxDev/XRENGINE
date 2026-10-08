namespace XREngine.Rendering.Compute;

/// <summary>
/// Records the output page ring when no page could be acquired for a sustained run of attempts.
/// </summary>
/// <param name="RenderFrame">Dispatcher frame index when the snapshot was taken.</param>
/// <param name="ConsecutiveBusyAttempts">Busy acquisition attempts since the last successful page reuse.</param>
/// <param name="ProducerEpoch">Latest committed producer epoch.</param>
/// <param name="PublishedPageIndex">Published page index, or -1.</param>
/// <param name="HistoryPageIndex">History page index, or -1.</param>
/// <param name="Pages">State of every live and retired page.</param>
public sealed record PhysicsChainGpuOutputPageStallSnapshot(
    long RenderFrame,
    long ConsecutiveBusyAttempts,
    uint ProducerEpoch,
    int PublishedPageIndex,
    int HistoryPageIndex,
    PhysicsChainGpuOutputPageState[] Pages);
