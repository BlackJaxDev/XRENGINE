namespace XREngine;

public readonly record struct RvcShadeletDensityPolicy(
    ERvcShadeletDensity Fovea,
    ERvcShadeletDensity GuardBand,
    ERvcShadeletDensity MidField,
    ERvcShadeletDensity Periphery,
    bool ForceNearFieldAndUiTo1x1,
    float FullResNearDistanceMeters)
{
    public static RvcShadeletDensityPolicy Default => new(
        ERvcShadeletDensity.Rate1x1,
        ERvcShadeletDensity.Rate1x1,
        ERvcShadeletDensity.Rate2x2,
        ERvcShadeletDensity.Rate4x4,
        ForceNearFieldAndUiTo1x1: true,
        FullResNearDistanceMeters: 1.5f);

    public ERvcShadeletDensity Resolve(ERvcFoveationRegion region, bool isUiOrHand, float distanceMeters)
    {
        if (ForceNearFieldAndUiTo1x1 && (isUiOrHand || distanceMeters <= FullResNearDistanceMeters))
            return ERvcShadeletDensity.Rate1x1;

        return region switch
        {
            ERvcFoveationRegion.Foveal => Fovea,
            ERvcFoveationRegion.GuardBand => GuardBand,
            ERvcFoveationRegion.MidField => MidField,
            _ => Periphery,
        };
    }
}
