using System;

namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Explicit evidence requirements supplied by the offline benchmark owner. No
/// synthetic performance threshold is assumed by the renderer.
/// </summary>
public readonly record struct GpuHiZCrossoverRequirements(
    uint MinimumCompletedMatchedFrames,
    uint MinimumPairedWinSamples,
    double MinimumAbsoluteSavingsNanoseconds,
    double MinimumRelativeSavings)
{
    public void Validate()
    {
        if (MinimumCompletedMatchedFrames == 0u ||
            MinimumPairedWinSamples == 0u ||
            !double.IsFinite(MinimumAbsoluteSavingsNanoseconds) ||
            MinimumAbsoluteSavingsNanoseconds < 0.0 ||
            !double.IsFinite(MinimumRelativeSavings) ||
            MinimumRelativeSavings < 0.0)
            throw new ArgumentOutOfRangeException(nameof(MinimumCompletedMatchedFrames));
    }
}
