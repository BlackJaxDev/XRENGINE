namespace XREngine.Execution;

/// <summary>
/// Allocation-free scheduling summary produced while a pooled batch is sealed.
/// Only initially independent migratable items participate in the profitability
/// estimate; dependent work is conservatively ignored until a later generation.
/// </summary>
internal readonly record struct RenderWorkDispatchProfile(
    int MigratableItemCount,
    int IndependentMigratableItemCount,
    long IndependentEstimatedCost,
    int MaximumIndependentEstimatedCost,
    int CapPinnedItemCount,
    bool RequiresBackgroundLane);
