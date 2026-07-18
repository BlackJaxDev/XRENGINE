namespace XREngine.Data.Trees;

/// <summary>
/// Construction, snapshot, and quality-maintenance policy for a CPU scene BVH.
/// </summary>
public sealed class CpuBvhOptions
{
    public int LeafCapacity { get; init; } = 8;
    public int SahBinCount { get; init; } = 16;
    public int SnapshotCount { get; init; } = 4;
    public int MaxPendingMutations { get; init; } = 1_048_576;
    public int LocalRotationBudget { get; init; } = 32;
    public int PartialRebuildRefitCount { get; init; } = 128;
    public int FullRebuildRefitCount { get; init; } = 512;
    public float RotationImprovementThreshold { get; init; } = 0.995f;
    public float PartialRebuildSahRatio { get; init; } = 1.35f;
    public float FullRebuildSahRatio { get; init; } = 1.75f;
    public float FullRebuildRootGrowthRatio { get; init; } = 3.0f;
    public bool EnableLocalRestructuring { get; init; } = true;

    internal void Validate()
    {
        if (LeafCapacity is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(LeafCapacity), "Leaf capacity must be between 1 and 64.");
        if (SahBinCount is < 4 or > 64)
            throw new ArgumentOutOfRangeException(nameof(SahBinCount), "SAH bin count must be between 4 and 64.");
        if (SnapshotCount is < 2 or > 16)
            throw new ArgumentOutOfRangeException(nameof(SnapshotCount), "At least two bounded snapshot slots are required.");
        if (MaxPendingMutations < 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingMutations));
        if (LocalRotationBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(LocalRotationBudget));
        if (PartialRebuildRefitCount < 1 || FullRebuildRefitCount < PartialRebuildRefitCount)
            throw new ArgumentOutOfRangeException(nameof(FullRebuildRefitCount));
        if (RotationImprovementThreshold is <= 0.0f or >= 1.0f)
            throw new ArgumentOutOfRangeException(nameof(RotationImprovementThreshold));
        if (PartialRebuildSahRatio <= 1.0f || FullRebuildSahRatio < PartialRebuildSahRatio)
            throw new ArgumentOutOfRangeException(nameof(FullRebuildSahRatio));
        if (FullRebuildRootGrowthRatio <= 1.0f)
            throw new ArgumentOutOfRangeException(nameof(FullRebuildRootGrowthRatio));
    }
}
