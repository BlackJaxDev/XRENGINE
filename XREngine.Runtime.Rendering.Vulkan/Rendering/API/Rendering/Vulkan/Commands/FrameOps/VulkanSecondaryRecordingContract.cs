namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free decision shared by non-graphics secondary policy, telemetry,
/// and the primary-command fallback path.
/// </summary>
internal readonly record struct VulkanSecondaryRecordingContract(
    EVulkanSecondaryCommandFamily Family,
    EVulkanSecondaryRecordingEligibility Eligibility,
    VulkanQuerySecondaryInheritanceContract QueryInheritance = default)
{
    internal bool IsEligible
        => Eligibility == EVulkanSecondaryRecordingEligibility.Eligible;
}
