namespace XREngine;

public readonly record struct RvcHzbRejectionContract(
    bool PreviousDepthMayReject,
    bool RequiresSafeReprojection,
    bool RequiresStaticOrValidatedDynamicObject,
    bool RequiresCurrentFramePostValidation,
    float EdgeDepthAgreementMeters)
{
    public static RvcHzbRejectionContract Conservative => new(
        PreviousDepthMayReject: true,
        RequiresSafeReprojection: true,
        RequiresStaticOrValidatedDynamicObject: true,
        RequiresCurrentFramePostValidation: true,
        EdgeDepthAgreementMeters: 0.01f);
}
