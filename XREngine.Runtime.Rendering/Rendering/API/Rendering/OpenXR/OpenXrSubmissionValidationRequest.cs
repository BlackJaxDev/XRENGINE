namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Configures a one-shot, smoke-only OpenXR submission validation scenario.</summary>
public readonly record struct OpenXrSubmissionValidationRequest(
    EOpenXrSubmissionValidationScenario Scenario,
    EOpenXrSubmissionShape RequiredShape,
    // Entries at or before this lifecycle frame are observed but excluded from
    // the bounded cohort and one-shot scenario activation.
    ulong ArmAfterOpenXrLifecycleFrameId,
    int LedgerCapacity)
{
    public static OpenXrSubmissionValidationRequest Disabled => new(
        EOpenXrSubmissionValidationScenario.Disabled,
        EOpenXrSubmissionShape.Unknown,
        0,
        0);

    public bool Enabled => Scenario != EOpenXrSubmissionValidationScenario.Disabled;
}
