namespace XREngine.Rendering;

/// <summary>
/// Cumulative split of the Advanced resident-scene publication in the world swap.
/// </summary>
/// <remarks>
/// <para>
/// The publication time minus the row-gate time and the commit time is the work before the gate decision: the
/// publication request check, the boundary capacity check and the tombstone reclaim. A publication that stops
/// before the gate decision, for example because the database is faulted, is in neither stage.
/// </para>
/// <para>
/// Only the collect thread records these values, and only in the world swap section. A publication outside the
/// serial swap, for example a standalone GPU-scene swap or a scene capture, records nothing.
/// </para>
/// </remarks>
/// <param name="RowGatePasses">The number of unchanged-row gate calls that found every row input unchanged.</param>
/// <param name="RowGateFailures">The number of unchanged-row gate calls that found a changed row input or no valid baseline.</param>
/// <param name="RowGateMilliseconds">The unchanged-row gate time.</param>
/// <param name="UnchangedRowPublications">
/// The number of publications that the unchanged-row path completed after the gate passed. A gate pass that the
/// unchanged-row path cannot publish continues to the full plan, and is not in this count.
/// </param>
/// <param name="CommitCalls">
/// The number of publications that reached the gate decision. The publications in which the gate did not run
/// because the resident tables grew are this count minus the gate passes and the gate failures.
/// </param>
/// <param name="CommitMilliseconds">
/// The time after the gate decision: the unchanged-row publication or the full plan with its commit, and the scratch release.
/// </param>
/// <param name="PlanMilliseconds">The whole-scene plan time of the full publications. Part of <paramref name="CommitMilliseconds"/>.</param>
/// <param name="ApplyMilliseconds">The transaction apply time of the full publications. Part of <paramref name="CommitMilliseconds"/>.</param>
/// <param name="SealMilliseconds">
/// The seal time of the full publications: snapshot copies, captures, commit and identity delivery. Part of
/// <paramref name="CommitMilliseconds"/>.
/// </param>
/// <param name="GlobalPreflightMilliseconds">The global-resource preflight time of every publication path.</param>
/// <param name="IdentityDeliveryMilliseconds">The identity delivery time after each commit. Part of <paramref name="SealMilliseconds"/>.</param>
public readonly record struct CollectVisibleSwapAdvancedPublicationTimings(
    long RowGatePasses,
    long RowGateFailures,
    double RowGateMilliseconds,
    long UnchangedRowPublications,
    long CommitCalls,
    double CommitMilliseconds,
    double PlanMilliseconds,
    double ApplyMilliseconds,
    double SealMilliseconds,
    double GlobalPreflightMilliseconds,
    double IdentityDeliveryMilliseconds);
