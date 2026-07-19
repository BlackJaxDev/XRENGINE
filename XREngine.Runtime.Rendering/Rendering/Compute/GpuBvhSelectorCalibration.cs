using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Immutable, serializable-in-shape threshold table used by runtime selection.
/// Missing buckets remain disabled until representative measurements supply a
/// crossover; an unmeasured GPU BVH path is not promoted by command count alone.
/// </summary>
public sealed class GpuBvhSelectorCalibration
{
    /// <summary>Threshold that keeps an uncalibrated bucket on flat GPU culling.</summary>
    public const uint UncalibratedCommandThreshold = uint.MaxValue;

    private readonly IReadOnlyDictionary<GpuBvhSelectorBucket, uint> _thresholds;

    public GpuBvhSelectorCalibration(
        IReadOnlyDictionary<GpuBvhSelectorBucket, uint>? thresholds = null,
        uint fallbackCommandThreshold = UncalibratedCommandThreshold)
    {
        _thresholds = thresholds is null
            ? new Dictionary<GpuBvhSelectorBucket, uint>()
            : new Dictionary<GpuBvhSelectorBucket, uint>(thresholds);
        FallbackCommandThreshold = Math.Max(1u, fallbackCommandThreshold);
    }

    /// <summary>The deterministic uncalibrated policy.</summary>
    public static GpuBvhSelectorCalibration Default { get; } = new();

    /// <summary>The threshold used when the capture has no matching bucket.</summary>
    public uint FallbackCommandThreshold { get; }

    /// <summary>Per-backend/view/visibility crossover thresholds.</summary>
    public IReadOnlyDictionary<GpuBvhSelectorBucket, uint> Thresholds => _thresholds;

    /// <summary>Returns the measured threshold or the conservative fallback.</summary>
    public uint GetCommandThreshold(GpuBvhSelectorBucket bucket)
        => _thresholds.TryGetValue(bucket, out uint threshold)
            ? threshold
            : FallbackCommandThreshold;

    /// <summary>Returns whether the calibrated policy recommends BVH traversal.</summary>
    public bool ShouldUseBvh(GpuBvhSelectorBucket bucket, uint commandCount)
        => commandCount >= GetCommandThreshold(bucket);
}
