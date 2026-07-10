namespace XREngine;

public readonly record struct RvcReuseValidationPlan(
    RvcReusePolicy Policy,
    RvcEdgeRejectionPolicy EdgeRejection,
    bool RequireAbHarnessBeforeStereoDefault,
    bool KeepSharpSpecularPerView)
{
    public static RvcReuseValidationPlan Defaults => new(
        RvcReusePolicy.DefaultsWithStereoOff,
        RvcEdgeRejectionPolicy.Conservative,
        RequireAbHarnessBeforeStereoDefault: true,
        KeepSharpSpecularPerView: true);
}
