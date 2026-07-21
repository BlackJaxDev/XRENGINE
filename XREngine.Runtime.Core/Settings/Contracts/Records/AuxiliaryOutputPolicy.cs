namespace XREngine;

public readonly record struct AuxiliaryOutputPolicy(
    ulong StableOutputKey,
    float ScreenCoverage,
    float MaximumUpdatesPerSecond,
    uint MaximumContentAgeFrames,
    float ResolutionScale,
    byte RecursionLimit,
    bool RequiresIndependentCamera,
    bool EnablePostProcess,
    bool CacheLastResult)
{
    public AuxiliaryOutputPolicy Validate()
    {
        if (StableOutputKey == 0UL)
            throw new ArgumentOutOfRangeException(nameof(StableOutputKey));
        if (!float.IsFinite(ScreenCoverage) || ScreenCoverage < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(ScreenCoverage));
        if (!float.IsFinite(MaximumUpdatesPerSecond) || MaximumUpdatesPerSecond < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(MaximumUpdatesPerSecond));
        if (!float.IsFinite(ResolutionScale) || ResolutionScale <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(ResolutionScale));
        return this;
    }
}
