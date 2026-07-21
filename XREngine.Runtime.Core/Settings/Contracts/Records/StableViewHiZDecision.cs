using System.Numerics;

namespace XREngine;

/// <summary>
/// A resolved Hi-Z source. Bounds are always frustum-tested with the exact
/// logical-view matrix, then projected with <see cref="SamplingViewProjection"/>.
/// </summary>
public readonly record struct StableViewHiZDecision(
    ulong StableViewKey,
    ulong SamplingHistoryKey,
    uint LogicalViewId,
    uint SamplingLogicalViewId,
    EHiZOcclusionDisposition Disposition,
    EHiZHistoryInvalidation Invalidation,
    Matrix4x4 ExactFrustumViewProjection,
    Matrix4x4 SamplingViewProjection)
{
    public bool MaySample
        => Disposition is EHiZOcclusionDisposition.SampleOwnHistory
            or EHiZOcclusionDisposition.SampleValidatedOuterEyeHistory;
}
