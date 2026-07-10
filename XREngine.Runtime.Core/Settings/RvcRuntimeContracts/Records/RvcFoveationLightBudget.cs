namespace XREngine;

public readonly record struct RvcFoveationLightBudget(
    int FovealExactLights,
    int GuardBandExactLights,
    int MidFieldExactLights,
    int PeripheryExactLights,
    int PeripheryAggregateLights)
{
    public static RvcFoveationLightBudget Default => new(
        FovealExactLights: 64,
        GuardBandExactLights: 48,
        MidFieldExactLights: 24,
        PeripheryExactLights: 8,
        PeripheryAggregateLights: 4);

    public int ExactLightBudgetForRegion(ERvcFoveationRegion region)
        => region switch
        {
            ERvcFoveationRegion.Foveal => FovealExactLights,
            ERvcFoveationRegion.GuardBand => GuardBandExactLights,
            ERvcFoveationRegion.MidField => MidFieldExactLights,
            _ => PeripheryExactLights,
        };
}
