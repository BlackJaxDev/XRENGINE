using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Rendering.Compute;

/// <summary>Deterministic calibration of benchmark samples into selector thresholds.</summary>
public static class GpuBvhSelectorCalibrator
{
    /// <summary>
    /// Selects the first command-count sample in the first pair of consecutive
    /// measured sizes where BVH meets the requested flat-time ratio. Requiring
    /// two sizes prevents a single noisy win from promoting the hierarchy.
    /// </summary>
    public static GpuBvhSelectorCalibration Calibrate(
        IEnumerable<GpuBvhSelectorSample> samples,
        uint fallbackCommandThreshold = GpuBvhSelectorCalibration.UncalibratedCommandThreshold,
        double requiredBvhToFlatRatio = 1.0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(requiredBvhToFlatRatio) || requiredBvhToFlatRatio <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(requiredBvhToFlatRatio));

        Dictionary<GpuBvhSelectorBucket, uint> thresholds = [];
        foreach (IGrouping<GpuBvhSelectorBucket, GpuBvhSelectorSample> bucketSamples in samples.GroupBy(sample => sample.Bucket))
        {
            var medians = bucketSamples
                .Where(IsValid)
                .GroupBy(sample => sample.CommandCount)
                .Select(group => new
                {
                    CommandCount = group.Key,
                    Flat = Median(group.Select(sample => sample.FlatNanoseconds)),
                    Bvh = Median(group.Select(sample => sample.BvhNanoseconds)),
                })
                .OrderBy(sample => sample.CommandCount)
                .ToArray();

            for (int i = 0; i + 1 < medians.Length; ++i)
            {
                bool currentWins = medians[i].Bvh <= medians[i].Flat * requiredBvhToFlatRatio;
                bool nextWins = medians[i + 1].Bvh <= medians[i + 1].Flat * requiredBvhToFlatRatio;
                if (!currentWins || !nextWins)
                    continue;

                thresholds[bucketSamples.Key] = medians[i].CommandCount;
                break;
            }
        }

        return new(thresholds, fallbackCommandThreshold);
    }

    private static bool IsValid(GpuBvhSelectorSample sample)
        => sample.CommandCount > 0u &&
           double.IsFinite(sample.FlatNanoseconds) && sample.FlatNanoseconds > 0.0 &&
           double.IsFinite(sample.BvhNanoseconds) && sample.BvhNanoseconds > 0.0;

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        int middle = ordered.Length / 2;
        return (ordered.Length & 1) == 0
            ? (ordered[middle - 1] + ordered[middle]) * 0.5
            : ordered[middle];
    }
}
