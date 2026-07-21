namespace XREngine.Editor.Benchmarks.PhysicsChain;

public readonly record struct PhysicsChainBenchmarkWorkItem(
    long StableIndex,
    PhysicsChainBenchmarkCase MatrixCase,
    PhysicsChainBenchmarkMeasurementKind MeasurementKind,
    int MatchedRunIndex);
