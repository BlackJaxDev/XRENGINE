namespace XREngine.Rendering.Compute;

/// <summary>
/// Retained evidence for output page ring stalls. The first snapshot is kept for the
/// process lifetime; the latest snapshot is refreshed while a stall continues.
/// </summary>
/// <param name="EpisodeCount">Number of stall episodes that reached the snapshot threshold.</param>
/// <param name="ConsecutiveBusyAttempts">Busy acquisition attempts since the last successful page reuse.</param>
/// <param name="First">Snapshot of the first episode, or <see langword="null"/>.</param>
/// <param name="Latest">Most recent snapshot, or <see langword="null"/>.</param>
public readonly record struct PhysicsChainGpuOutputPageStallDiagnostics(
    long EpisodeCount,
    long ConsecutiveBusyAttempts,
    PhysicsChainGpuOutputPageStallSnapshot? First,
    PhysicsChainGpuOutputPageStallSnapshot? Latest);
