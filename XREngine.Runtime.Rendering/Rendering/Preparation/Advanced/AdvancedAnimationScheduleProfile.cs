using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Allocation-free render-pose cadence policy. Intervals are measured in
/// engine frames; zero authored intervals are normalized to one.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedAnimationScheduleProfile(
    float FullRateProjectedDiameter,
    float MediumRateProjectedDiameter,
    float LowRateProjectedDiameter,
    uint FullRateInterval,
    uint MediumRateInterval,
    uint LowRateInterval,
    uint OffscreenInterval,
    uint VisibilityGraceFrames,
    uint MaximumStalePoseFrames)
{
    public static AdvancedAnimationScheduleProfile Default => new(
        FullRateProjectedDiameter: 0.18f,
        MediumRateProjectedDiameter: 0.06f,
        LowRateProjectedDiameter: 0.015f,
        FullRateInterval: 1u,
        MediumRateInterval: 2u,
        LowRateInterval: 4u,
        OffscreenInterval: 12u,
        VisibilityGraceFrames: 3u,
        MaximumStalePoseFrames: 24u);

    public uint ResolveInterval(float projectedDiameter, bool inVisibilityGrace)
    {
        if (!inVisibilityGrace)
            return Math.Max(1u, OffscreenInterval);
        if (projectedDiameter >= FullRateProjectedDiameter)
            return Math.Max(1u, FullRateInterval);
        if (projectedDiameter >= MediumRateProjectedDiameter)
            return Math.Max(1u, MediumRateInterval);
        if (projectedDiameter >= LowRateProjectedDiameter)
            return Math.Max(1u, LowRateInterval);
        return Math.Max(1u, OffscreenInterval);
    }
}
