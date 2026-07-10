namespace XREngine;

public readonly record struct RvcSaccadeQualityPolicy(
    bool Enabled,
    float VelocityThresholdDegreesPerSecond,
    ERvcShadeletDensity DuringSaccadeMaxDensity,
    bool LandQualityAtPredictedEndpoint)
{
    public static RvcSaccadeQualityPolicy ConservativeDisabled => new(
        Enabled: false,
        VelocityThresholdDegreesPerSecond: 300.0f,
        ERvcShadeletDensity.Rate4x4,
        LandQualityAtPredictedEndpoint: true);
}
