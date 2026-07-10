namespace XREngine;

public readonly record struct RvcWideInsetCompositionPolicy(
    ERvcShadeletDensity WideUnderInsetDensity,
    float BlendGuardBandDegrees,
    bool DisablePerViewSpecularUnderInset,
    bool WideViewOnlyNeedsPlausibleBlendData)
{
    public static RvcWideInsetCompositionPolicy Default => new(
        ERvcShadeletDensity.Rate8x8,
        BlendGuardBandDegrees: 1.0f,
        DisablePerViewSpecularUnderInset: true,
        WideViewOnlyNeedsPlausibleBlendData: true);
}
