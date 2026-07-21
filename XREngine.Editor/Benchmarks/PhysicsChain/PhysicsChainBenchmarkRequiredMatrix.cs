namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Lazily enumerates every required scale axis without materializing the full
/// Cartesian product. Launchers may shard by the stable zero-based index.
/// </summary>
public static class PhysicsChainBenchmarkRequiredMatrix
{
    private static readonly PhysicsChainBenchmarkTopology[] Topologies = [PhysicsChainBenchmarkTopology.Linear, PhysicsChainBenchmarkTopology.Branched];
    private static readonly PhysicsChainBenchmarkColliderScenario[] ColliderScenarios = [PhysicsChainBenchmarkColliderScenario.None, PhysicsChainBenchmarkColliderScenario.TwoSimple, PhysicsChainBenchmarkColliderScenario.FiveMixed, PhysicsChainBenchmarkColliderScenario.LargeBroadphase];
    private static readonly PhysicsChainBenchmarkColliderOwnership[] ColliderOwnerships = [PhysicsChainBenchmarkColliderOwnership.Shared, PhysicsChainBenchmarkColliderOwnership.Unique];
    private static readonly PhysicsChainBenchmarkActivityProfile[] ActivityProfiles = [PhysicsChainBenchmarkActivityProfile.Active100, PhysicsChainBenchmarkActivityProfile.Active50, PhysicsChainBenchmarkActivityProfile.Active10, PhysicsChainBenchmarkActivityProfile.SleepingOffscreenHeavy];
    private static readonly PhysicsChainBenchmarkRenderingMode[] RenderingModes = [PhysicsChainBenchmarkRenderingMode.None, PhysicsChainBenchmarkRenderingMode.PaletteAndBounds, PhysicsChainBenchmarkRenderingMode.IdenticalInstancedMeshes, PhysicsChainBenchmarkRenderingMode.DiverseSkinnedRenderers];
    private static readonly PhysicsChainBenchmarkExecutionMode[] ExecutionModes = [PhysicsChainBenchmarkExecutionMode.CpuStrict, PhysicsChainBenchmarkExecutionMode.GpuStrictZeroReadback, PhysicsChainBenchmarkExecutionMode.CpuQualityTiered, PhysicsChainBenchmarkExecutionMode.GpuQualityTiered];
    private static readonly PhysicsChainBenchmarkReadbackMode[] ReadbackModes = [PhysicsChainBenchmarkReadbackMode.Disabled, PhysicsChainBenchmarkReadbackMode.SparseSockets, PhysicsChainBenchmarkReadbackMode.SparseWholeChains, PhysicsChainBenchmarkReadbackMode.DiagnosticFullSync];
    private static readonly PhysicsChainBenchmarkRenderBackend[] RenderBackends = [PhysicsChainBenchmarkRenderBackend.OpenGL, PhysicsChainBenchmarkRenderBackend.Vulkan];

    public static int CaseCount { get; } = checked(
        PhysicsChainBenchmarkMatrix.ChainCounts.Length
        * PhysicsChainBenchmarkMatrix.DynamicSegmentCounts.Length
        * Topologies.Length
        * ColliderScenarios.Length
        * ColliderOwnerships.Length
        * ActivityProfiles.Length
        * RenderingModes.Length
        * ExecutionModes.Length
        * ReadbackModes.Length
        * RenderBackends.Length
        * PhysicsChainBenchmarkMatrix.FixedSimulationRates.Length);

    public static IEnumerable<PhysicsChainBenchmarkCase> Enumerate()
    {
        int[] chainCounts = PhysicsChainBenchmarkMatrix.ChainCounts.ToArray();
        int[] segmentCounts = PhysicsChainBenchmarkMatrix.DynamicSegmentCounts.ToArray();
        int[] rates = PhysicsChainBenchmarkMatrix.FixedSimulationRates.ToArray();
        foreach (int chainCount in chainCounts)
        foreach (int segmentCount in segmentCounts)
        foreach (PhysicsChainBenchmarkTopology topology in Topologies)
        foreach (PhysicsChainBenchmarkColliderScenario colliders in ColliderScenarios)
        foreach (PhysicsChainBenchmarkColliderOwnership ownership in ColliderOwnerships)
        foreach (PhysicsChainBenchmarkActivityProfile activity in ActivityProfiles)
        foreach (PhysicsChainBenchmarkRenderingMode rendering in RenderingModes)
        foreach (PhysicsChainBenchmarkExecutionMode execution in ExecutionModes)
        foreach (PhysicsChainBenchmarkReadbackMode readback in ReadbackModes)
        foreach (PhysicsChainBenchmarkRenderBackend backend in RenderBackends)
        foreach (int rate in rates)
            yield return new PhysicsChainBenchmarkCase(
                chainCount,
                segmentCount,
                topology,
                colliders,
                ownership,
                activity,
                rendering,
                execution,
                readback,
                backend,
                rate);
    }
}
