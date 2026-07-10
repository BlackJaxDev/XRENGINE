namespace XREngine;

public readonly record struct RvcQualitySettings(
    float FovealRadiusDegrees,
    float GuardBandDegrees,
    float MidFieldRadiusDegrees,
    ERvcShadeletDensity PeripheralMaxRate,
    float ForceFullResNearDistanceMeters,
    ERvcDerivativeStrategy DerivativeStrategy,
    ERvcFovealAntiAliasingPath FovealAntiAliasingPath,
    float ReuseMaxNormalAngleDegrees,
    float ReuseMaxDepthDeltaMeters,
    byte ReuseMaxRoughnessBucketDelta)
{
    public static RvcQualitySettings Defaults => new(
        FovealRadiusDegrees: 5.0f,
        GuardBandDegrees: 8.0f,
        MidFieldRadiusDegrees: 30.0f,
        ERvcShadeletDensity.Rate4x4,
        ForceFullResNearDistanceMeters: 1.5f,
        ERvcDerivativeStrategy.AnalyticFromVisibilityGradients,
        ERvcFovealAntiAliasingPath.VisibilityEdgeAA,
        ReuseMaxNormalAngleDegrees: 5.0f,
        ReuseMaxDepthDeltaMeters: 0.05f,
        ReuseMaxRoughnessBucketDelta: 1);
}
