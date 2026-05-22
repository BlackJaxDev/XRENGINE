namespace XREngine.Data.Trees;

/// <summary>
/// Per-frame occupancy snapshot for CPU render visibility structures.
/// </summary>
public readonly record struct SpatialTreeOccupancyStats(
    int NodeCount,
    int ItemCount,
    int RootItemCount,
    int MaxNodeItemCount,
    int MaxDepth,
    int UnboundedItemCount);
