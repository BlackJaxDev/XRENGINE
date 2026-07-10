namespace XREngine;

public enum ERenderOutputPolicyReason
{
    None,
    Cadence,
    CpuBudget,
    GpuBudget,
    MirrorDisabled,
    SurfaceUnavailable,
    VrGated,
    OutputDisabled,
    HeldLastImage,
    DependencyUnavailable,
    DeadlineRisk,
}
