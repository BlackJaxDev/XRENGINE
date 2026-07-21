namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// One fully explicit point in the required scale matrix.
/// </summary>
public readonly record struct PhysicsChainBenchmarkCase(
    int ChainCount,
    int DynamicSegmentCount,
    PhysicsChainBenchmarkTopology Topology,
    PhysicsChainBenchmarkColliderScenario ColliderScenario,
    PhysicsChainBenchmarkColliderOwnership ColliderOwnership,
    PhysicsChainBenchmarkActivityProfile ActivityProfile,
    PhysicsChainBenchmarkRenderingMode RenderingMode,
    PhysicsChainBenchmarkExecutionMode ExecutionMode,
    PhysicsChainBenchmarkReadbackMode ReadbackMode,
    PhysicsChainBenchmarkRenderBackend RenderBackend,
    int FixedSimulationRateHz)
{
    public string StableName
        => $"{ChainCount}x{DynamicSegmentCount}-{Topology}-{ColliderScenario}-{ColliderOwnership}-{ActivityProfile}-{RenderingMode}-{ExecutionMode}-{ReadbackMode}-{RenderBackend}-{FixedSimulationRateHz}hz";
}
