namespace XREngine;

public readonly record struct RvcQualityToleranceSet(
    RvcQualityTolerance Foveal,
    RvcQualityTolerance GuardBand,
    RvcQualityTolerance MidField,
    RvcQualityTolerance Periphery)
{
    public static RvcQualityToleranceSet Default => new(
        new(ERvcFoveationRegion.Foveal, 1.0f / 255.0f, 0.995f, 0.010f),
        new(ERvcFoveationRegion.GuardBand, 2.0f / 255.0f, 0.990f, 0.015f),
        new(ERvcFoveationRegion.MidField, 4.0f / 255.0f, 0.975f, 0.030f),
        new(ERvcFoveationRegion.Periphery, 8.0f / 255.0f, 0.940f, 0.060f));

    public RvcQualityTolerance ForRegion(ERvcFoveationRegion region)
        => region switch
        {
            ERvcFoveationRegion.Foveal => Foveal,
            ERvcFoveationRegion.GuardBand => GuardBand,
            ERvcFoveationRegion.MidField => MidField,
            _ => Periphery,
        };
}
