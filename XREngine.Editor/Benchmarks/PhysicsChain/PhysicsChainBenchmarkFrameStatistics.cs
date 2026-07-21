namespace XREngine.Editor.Benchmarks.PhysicsChain;

/// <summary>
/// Summary of a completed steady-state frame-time sample set.
/// Percentiles use the nearest-rank definition.
/// </summary>
public readonly record struct PhysicsChainBenchmarkFrameStatistics(
    int SampleCount,
    double MinimumMilliseconds,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds)
{
    public static PhysicsChainBenchmarkFrameStatistics Calculate(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one frame-time sample is required.", nameof(samples));

        double[] sorted = new double[samples.Count];
        double total = 0.0;
        for (int i = 0; i < samples.Count; ++i)
        {
            double sample = samples[i];
            if (!double.IsFinite(sample) || sample < 0.0)
                throw new ArgumentOutOfRangeException(nameof(samples), "Frame-time samples must be finite and non-negative.");

            sorted[i] = sample;
            total += sample;
        }

        Array.Sort(sorted);
        return new PhysicsChainBenchmarkFrameStatistics(
            sorted.Length,
            sorted[0],
            total / sorted.Length,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1]);
    }

    private static double Percentile(double[] sortedSamples, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(sortedSamples.Length * percentile) - 1, 0, sortedSamples.Length - 1);
        return sortedSamples[index];
    }
}
