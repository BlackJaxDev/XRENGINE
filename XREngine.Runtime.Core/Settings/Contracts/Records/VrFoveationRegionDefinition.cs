namespace XREngine;

public readonly record struct VrFoveationRegionDefinition(
    float InnerRadius,
    float GuardRadius,
    float MidRadius,
    float OuterRadius)
{
    public static VrFoveationRegionDefinition FromPreset(EVrFoveationQualityPreset preset)
        => preset switch
        {
            EVrFoveationQualityPreset.Conservative => new(0.38f, 0.52f, 0.74f, 1.00f),
            EVrFoveationQualityPreset.Aggressive => new(0.22f, 0.38f, 0.62f, 1.00f),
            _ => new(0.30f, 0.46f, 0.68f, 1.00f),
        };
}
