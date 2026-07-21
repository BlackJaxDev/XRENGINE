namespace XREngine.Editor.Benchmarks.PhysicsChain;

public static class PhysicsChainBenchmarkMatchedRunAggregator
{
    public static PhysicsChainBenchmarkMatchedSummary Summarize(
        IReadOnlyList<PhysicsChainBenchmarkEvidence> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count < 3)
            throw new ArgumentException("At least three matched runs are required.", nameof(runs));

        PhysicsChainBenchmarkEvidence first = runs[0];
        double p50 = 0.0;
        double p95 = 0.0;
        double p99 = 0.0;
        double maximum = 0.0;
        for (int i = 0; i < runs.Count; ++i)
        {
            PhysicsChainBenchmarkEvidence run = runs[i];
            if (run.MatrixCase != first.MatrixCase
                || run.MeasurementKind != first.MeasurementKind
                || run.Environment != first.Environment)
                throw new ArgumentException("Matched runs must use the same case, measurement kind, and environment.", nameof(runs));

            PhysicsChainBenchmarkFrameStatistics statistics = run.Result.FrameStatistics;
            p50 += statistics.P50Milliseconds;
            p95 += statistics.P95Milliseconds;
            p99 += statistics.P99Milliseconds;
            maximum = Math.Max(maximum, statistics.MaximumMilliseconds);
        }

        double meanP95 = p95 / runs.Count;
        double squaredError = 0.0;
        for (int i = 0; i < runs.Count; ++i)
        {
            double delta = runs[i].Result.FrameStatistics.P95Milliseconds - meanP95;
            squaredError += delta * delta;
        }
        double standardDeviation = Math.Sqrt(squaredError / runs.Count);

        return new PhysicsChainBenchmarkMatchedSummary
        {
            MatrixCase = first.MatrixCase,
            MeasurementKind = first.MeasurementKind,
            RunCount = runs.Count,
            MeanP50Milliseconds = p50 / runs.Count,
            MeanP95Milliseconds = meanP95,
            MeanP99Milliseconds = p99 / runs.Count,
            MaximumMilliseconds = maximum,
            P95CoefficientOfVariation = meanP95 > 0.0 ? standardDeviation / meanP95 : 0.0,
        };
    }
}
