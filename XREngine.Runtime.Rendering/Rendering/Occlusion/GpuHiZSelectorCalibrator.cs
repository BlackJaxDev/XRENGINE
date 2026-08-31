using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Rendering.Occlusion;

/// <summary>Builds conservative, offline-only Hi-Z selector evidence from paired GPU timings.</summary>
public static class GpuHiZSelectorCalibrator
{
    public static GpuHiZSelectorCalibration Calibrate(
        IEnumerable<GpuHiZMatchedCrossoverSample> samples,
        in GpuHiZCrossoverRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(samples);
        requirements.Validate();
        GpuHiZCrossoverRequirements calibratedRequirements = requirements;

        Dictionary<GpuHiZCalibrationBucket, GpuHiZSelectorDecision> decisions = [];
        foreach (IGrouping<GpuHiZCalibrationBucket, GpuHiZMatchedCrossoverSample> bucketSamples in samples
            .Where(static sample => sample.IsValid)
            .GroupBy(static sample => sample.Bucket))
        {
            GpuHiZSelectorProfile[] profiles = bucketSamples
                .GroupBy(static sample => sample.Candidate)
                .Select(BuildProfile)
                .Where(static profile => profile is not null)
                .Cast<GpuHiZSelectorProfile>()
                .ToArray();

            GpuHiZSelectorProfile[] qualified = profiles
                .Where(profile => profile.Meets(calibratedRequirements))
                .ToArray();
            if (qualified.Length == 1)
            {
                GpuHiZSelectorProfile profile = qualified[0];
                decisions[bucketSamples.Key] = new(profile.Candidate, EGpuHiZSelectorDecisionReason.Selected, profile);
                continue;
            }

            EGpuHiZSelectorDecisionReason reason = qualified.Length > 1
                ? EGpuHiZSelectorDecisionReason.AmbiguousMeasuredWins
                : profiles.Length == 0
                    ? EGpuHiZSelectorDecisionReason.Uncalibrated
                    : profiles.Any(profile => profile.CompletedMatchedFrames >= calibratedRequirements.MinimumCompletedMatchedFrames)
                        ? EGpuHiZSelectorDecisionReason.NoMeasuredWin
                        : EGpuHiZSelectorDecisionReason.InsufficientConfidence;
            decisions[bucketSamples.Key] = new(EGpuHiZCandidateMode.Disabled, reason, null);
        }

        return new(decisions);
    }

    private static GpuHiZSelectorProfile? BuildProfile(
        IGrouping<EGpuHiZCandidateMode, GpuHiZMatchedCrossoverSample> candidateSamples)
    {
        GpuHiZMatchedCrossoverSample[] samples = candidateSamples.ToArray();
        if (samples.Length == 0 ||
            !HasSingleValue(samples, static sample => sample.ParityProofSource) ||
            !HasSingleValue(samples, static sample => sample.MatchedCohortFingerprint) ||
            !HasSingleValue(samples, static sample => sample.TimestampScope))
            return null;

        double[] disabled = samples.Select(static sample => sample.DisabledGpuNanoseconds).Order().ToArray();
        double[] candidate = samples.Select(static sample => sample.CandidateGpuNanoseconds).Order().ToArray();
        double[] savings = samples.Select(static sample => sample.DisabledGpuNanoseconds - sample.CandidateGpuNanoseconds).ToArray();
        return new(
            candidateSamples.Key,
            samples[0].ParityProofSource,
            samples[0].MatchedCohortFingerprint,
            samples[0].TimestampScope,
            checked((uint)samples.Aggregate(0UL, static (total, sample) => total + sample.CompletedMatchedFrames)),
            checked((uint)savings.Count(static saving => saving > 0.0)),
            Median(disabled),
            Median(candidate),
            savings.Min());
    }

    private static bool HasSingleValue(
        IReadOnlyList<GpuHiZMatchedCrossoverSample> samples,
        Func<GpuHiZMatchedCrossoverSample, string> selector)
    {
        string value = selector(samples[0]);
        for (int i = 1; i < samples.Count; ++i)
            if (!string.Equals(value, selector(samples[i]), StringComparison.Ordinal))
                return false;
        return true;
    }

    private static double Median(double[] ordered)
    {
        int middle = ordered.Length / 2;
        return (ordered.Length & 1) == 0
            ? (ordered[middle - 1] + ordered[middle]) * 0.5
            : ordered[middle];
    }
}
