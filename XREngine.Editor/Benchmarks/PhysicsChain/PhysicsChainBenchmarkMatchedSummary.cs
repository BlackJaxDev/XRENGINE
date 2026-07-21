namespace XREngine.Editor.Benchmarks.PhysicsChain;

public sealed record PhysicsChainBenchmarkMatchedSummary
{
    public required PhysicsChainBenchmarkCase MatrixCase { get; init; }
    public required PhysicsChainBenchmarkMeasurementKind MeasurementKind { get; init; }
    public required int RunCount { get; init; }
    public required double MeanP50Milliseconds { get; init; }
    public required double MeanP95Milliseconds { get; init; }
    public required double MeanP99Milliseconds { get; init; }
    public required double MaximumMilliseconds { get; init; }
    public required double P95CoefficientOfVariation { get; init; }
}
