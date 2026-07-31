namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free worker eligibility result used by scheduling, telemetry,
/// diagnostics, and serial fallback policy.
/// </summary>
internal readonly record struct VulkanCommandChainWorkerEligibilityResult(
    EVulkanCommandChainWorkerEligibility Reason,
    int WorkerIndex = -1)
{
    internal bool IsEligible
        => Reason == EVulkanCommandChainWorkerEligibility.Eligible &&
            WorkerIndex >= 0;

    internal bool IsPermanentRejection
        => Reason is
            EVulkanCommandChainWorkerEligibility.UnsupportedOperation or
            EVulkanCommandChainWorkerEligibility.UnsupportedInheritance or
            EVulkanCommandChainWorkerEligibility.PrimaryOwnedIndirectStream;
}
