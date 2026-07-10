namespace XREngine;

public readonly record struct RvcVisibilityPassPlan(
    RvcVisibilityTargetContract Targets,
    RvcAttributeReconstructionContract Reconstruction,
    RvcHzbRejectionContract Hzb,
    ERvcVisibilityExecutionLane PrimaryLane,
    ERvcVisibilityExecutionLane FallbackLane,
    bool RenderWideBeforeInset,
    bool SeedInsetHzbFromWideDepth)
{
    public static RvcVisibilityPassPlan Default => new(
        RvcVisibilityTargetContract.Default,
        RvcAttributeReconstructionContract.Full,
        RvcHzbRejectionContract.Conservative,
        ERvcVisibilityExecutionLane.HardwareRaster,
        ERvcVisibilityExecutionLane.ForwardPlusFallback,
        RenderWideBeforeInset: true,
        SeedInsetHzbFromWideDepth: true);
}
