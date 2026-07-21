namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Deterministic lazy sweep planner with stable sharding across machines.
/// Cold-start, churn, and steady-state runs are always separate work items.
/// </summary>
public static class PhysicsChainBenchmarkSweep
{
    public static long GetWorkItemCount(PhysicsChainBenchmarkRunPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        return checked((long)PhysicsChainBenchmarkRequiredMatrix.CaseCount
            * policy.MatchedRunCount
            * Enum.GetValues<PhysicsChainBenchmarkMeasurementKind>().Length);
    }

    public static IEnumerable<PhysicsChainBenchmarkWorkItem> Enumerate(
        PhysicsChainBenchmarkRunPolicy policy,
        int shardIndex = 0,
        int shardCount = 1)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        if ((uint)shardIndex >= (uint)shardCount)
            throw new ArgumentOutOfRangeException(nameof(shardIndex));

        PhysicsChainBenchmarkMeasurementKind[] measurementKinds = Enum.GetValues<PhysicsChainBenchmarkMeasurementKind>();
        long stableIndex = 0L;
        foreach (PhysicsChainBenchmarkCase matrixCase in PhysicsChainBenchmarkRequiredMatrix.Enumerate())
        {
            for (int matchedRun = 0; matchedRun < policy.MatchedRunCount; ++matchedRun)
            {
                for (int kindIndex = 0; kindIndex < measurementKinds.Length; ++kindIndex)
                {
                    if (stableIndex % shardCount == shardIndex)
                        yield return new PhysicsChainBenchmarkWorkItem(
                            stableIndex,
                            matrixCase,
                            measurementKinds[kindIndex],
                            matchedRun);
                    ++stableIndex;
                }
            }
        }
    }
}
