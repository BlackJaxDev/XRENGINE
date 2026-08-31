using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Immutable offline GPU Hi-Z crossover artifact. It has no runtime policy side
/// effects: callers must opt into consuming a selected decision explicitly.
/// </summary>
public sealed class GpuHiZSelectorCalibration
{
    private readonly IReadOnlyDictionary<GpuHiZCalibrationBucket, GpuHiZSelectorDecision> _decisions;

    public GpuHiZSelectorCalibration(
        IReadOnlyDictionary<GpuHiZCalibrationBucket, GpuHiZSelectorDecision>? decisions = null)
    {
        Dictionary<GpuHiZCalibrationBucket, GpuHiZSelectorDecision> copied = decisions is null
            ? []
            : new Dictionary<GpuHiZCalibrationBucket, GpuHiZSelectorDecision>(decisions);
        _decisions = new ReadOnlyDictionary<GpuHiZCalibrationBucket, GpuHiZSelectorDecision>(copied);
    }

    public static GpuHiZSelectorCalibration Empty { get; } = new();

    public IReadOnlyDictionary<GpuHiZCalibrationBucket, GpuHiZSelectorDecision> Decisions => _decisions;

    public GpuHiZSelectorDecision Evaluate(in GpuHiZCalibrationBucket bucket)
        => _decisions.TryGetValue(bucket, out GpuHiZSelectorDecision decision)
            ? decision
            : new(EGpuHiZCandidateMode.Disabled, EGpuHiZSelectorDecisionReason.Uncalibrated, null);
}
