namespace XREngine.RenderBench;

/// <summary>Nearest-rank percentiles and sample standard deviation for one measured metric.</summary>
public sealed record RenderBenchMetricStatistics(
    int N,
    double P50,
    double P90,
    double P95,
    double P99,
    double Worst,
    double Mean,
    double StandardDeviation,
    double MedianAbsoluteDeviation)
{
    public static RenderBenchMetricStatistics? FromNanoseconds(ReadOnlySpan<long> samples)
    {
        if (samples.IsEmpty)
            return null;
        double[] values = new double[samples.Length];
        for (int index = 0; index < samples.Length; index++)
            values[index] = samples[index] / 1_000_000.0;
        return FromMilliseconds(values);
    }

    public static RenderBenchMetricStatistics? FromNanoseconds(ReadOnlySpan<double> samples)
    {
        if (samples.IsEmpty)
            return null;
        double[] values = new double[samples.Length];
        for (int index = 0; index < samples.Length; index++)
            values[index] = samples[index] / 1_000_000.0;
        return FromMilliseconds(values);
    }

    private static RenderBenchMetricStatistics? FromMilliseconds(double[] values)
    {
        if (values.Any(static value => !double.IsFinite(value) || value < 0))
            return null;
        Array.Sort(values);
        double mean = 0;
        double squaredDeviation = 0;
        for (int index = 0; index < values.Length; index++)
        {
            double delta = values[index] - mean;
            mean += delta / (index + 1);
            squaredDeviation += delta * (values[index] - mean);
        }
        double median = Percentile(values, 0.5);
        double[] deviations = new double[values.Length];
        for (int index = 0; index < values.Length; index++)
            deviations[index] = Math.Abs(values[index] - median);
        Array.Sort(deviations);
        return new RenderBenchMetricStatistics(
            values.Length, median, Percentile(values, 0.9), Percentile(values, 0.95),
            Percentile(values, 0.99), values[^1], mean,
            values.Length > 1 ? Math.Sqrt(squaredDeviation / (values.Length - 1)) : 0,
            Percentile(deviations, 0.5));
    }

    private static double Percentile(double[] sorted, double fraction)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
}
