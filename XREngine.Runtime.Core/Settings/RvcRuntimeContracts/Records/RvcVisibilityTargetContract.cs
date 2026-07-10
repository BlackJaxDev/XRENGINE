namespace XREngine;

public readonly record struct RvcVisibilityTargetContract(
    ERvcVisibilityPayloadFormat PayloadFormat,
    ERvcVisibilityExecutionLane AllowedLanes,
    bool PerViewDepthTarget,
    bool PerViewVisibilityTarget,
    bool PerViewVelocityTarget,
    bool BackendNeutralIdentity)
{
    public static RvcVisibilityTargetContract Default => new(
        ERvcVisibilityPayloadFormat.Rg32UintIdentity64,
        ERvcVisibilityExecutionLane.HardwareRaster |
        ERvcVisibilityExecutionLane.MeshletCompute |
        ERvcVisibilityExecutionLane.MeshShader |
        ERvcVisibilityExecutionLane.ForwardPlusFallback,
        PerViewDepthTarget: true,
        PerViewVisibilityTarget: true,
        PerViewVelocityTarget: true,
        BackendNeutralIdentity: true);
}
