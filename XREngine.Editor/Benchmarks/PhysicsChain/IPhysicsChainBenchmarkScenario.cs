namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>Engine/editor bridge driven by the allocation-bounded benchmark runner.</summary>
public interface IPhysicsChainBenchmarkScenario
{
    void Setup(
        in PhysicsChainBenchmarkCase matrixCase,
        PhysicsChainBenchmarkMeasurementKind measurementKind,
        in PhysicsChainBenchmarkDeterministicScenario deterministicScenario);
    PhysicsChainBenchmarkSettleSnapshot RunFrame();
    PhysicsChainBenchmarkScenarioMetrics CaptureMetrics();
    void Teardown();
}
