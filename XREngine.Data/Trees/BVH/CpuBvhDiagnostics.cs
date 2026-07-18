namespace XREngine.Data.Trees;

public interface ICpuBvhVisitor<in T>
{
    void Visit(T item);
}

public enum CpuBvhUpdateKind
{
    Clean,
    BoundsRefit,
    LocalRestructure,
    PartialRebuild,
    TopologyRebuild,
    QualityRebuild,
}

public enum CpuBvhRebuildReason
{
    None,
    InitialBuild,
    ExplicitRemake,
    ItemAddedOrRemoved,
    BoundsClassificationChanged,
    SnapshotCatchUp,
    SahDegradation,
    RootBoundsGrowth,
    RefitAge,
}

/// <summary>
/// Immutable diagnostic sample for a published CPU scene-BVH generation.
/// Time fields use <see cref="System.Diagnostics.Stopwatch"/> ticks.
/// </summary>
public readonly record struct CpuBvhDiagnostics(
    long Generation,
    long TopologyRevision,
    long BoundsRevision,
    long PayloadRevision,
    CpuBvhUpdateKind LastUpdate,
    CpuBvhRebuildReason LastRebuildReason,
    long TopologyBuildCount,
    long BoundsRefitCount,
    long PartialRebuildCount,
    long QualityRebuildCount,
    long RefittedLeafCount,
    long RefittedAncestorCount,
    long LocalRotationCount,
    long VisitedInternalNodeCount,
    long VisitedLeafCount,
    long VisitedPrimitiveCount,
    long FrustumPlaneTestCount,
    long FrustumPlaneMaskEliminationCount,
    long TraversalStackOverflowCount,
    int NodeCount,
    int LeafCount,
    int ItemCount,
    int UnboundedItemCount,
    int MaxDepth,
    float AverageDepth,
    float AverageLeafOccupancy,
    float NormalizedSahCost,
    float BaselineNormalizedSahCost,
    float AverageSiblingOverlap,
    float RootVolumeGrowth,
    float DirtyFraction,
    int ConsecutiveRefitCount,
    int PendingMutationCount,
    long DroppedMutationCount,
    long MutationLockWaitTicks,
    long MutationLockHoldTicks,
    long SnapshotPublicationTicks);
