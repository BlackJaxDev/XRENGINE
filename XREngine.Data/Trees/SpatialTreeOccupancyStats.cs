namespace XREngine.Data.Trees;

public readonly record struct SpatialTreeOccupancyStats(
    int NodeCount,
    int ItemCount,
    int RootItemCount,
    int MaxNodeItemCount,
    int MaxDepth,
    int UnboundedItemCount);
