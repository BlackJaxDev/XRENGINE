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
    bool IsExternalOutput)
{
    internal bool IsValid => PassIdentity != 0u;
}
