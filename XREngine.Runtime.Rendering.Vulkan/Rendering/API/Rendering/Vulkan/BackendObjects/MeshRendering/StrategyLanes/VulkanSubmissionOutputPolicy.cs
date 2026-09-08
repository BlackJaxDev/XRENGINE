namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Sealed pass/output constraints preserved by every submission lane.
/// </summary>
internal readonly record struct VulkanSubmissionOutputPolicy(
    ulong PassIdentity,
    bool AllowsGpuDrivenSubmission,
    bool IsShadowPass,
    bool IsExplicitOutput,
    bool IsOpenXrOutput,
    bool IsMirrorOutput,
    bool IsCaptureOutput,
    bool IsExternalOutput,
    AdvancedVisibilityFamilyReservation VisibilityReservation = default)
{
    internal bool IsValid => PassIdentity != 0u;

    /// <summary>
    /// Capture and XR outputs require their own frozen visibility reservation.
    /// Ordinary policy snapshots cannot borrow another output's family.
    /// </summary>
    internal bool AllowsCanonicalVisibilityFamily
        => IsValid && !IsShadowPass &&
           (VisibilityReservation.IsValid ||
            !IsExplicitOutput && !IsOpenXrOutput && !IsMirrorOutput && !IsCaptureOutput && !IsExternalOutput);

    internal string DescribeCanonicalVisibilityRejection()
    {
        if (!IsValid)
            return "the output policy has no stable pass identity";
        if (IsShadowPass)
            return "shadow outputs require their own canonical visibility family";
        if (IsOpenXrOutput)
            return "OpenXR outputs remain on the runtime-owned eye submission path";
        if (IsMirrorOutput)
            return "mirror outputs require an independent composition family";
        if (IsCaptureOutput)
            return "capture outputs require an independent capture family";
        if (IsExternalOutput)
            return "external-image outputs require an independently sealed native target";
        if (IsExplicitOutput)
            return "explicit outputs require an independently sealed target family";
        return "the output policy is not admitted by the canonical visibility family";
    }
}
