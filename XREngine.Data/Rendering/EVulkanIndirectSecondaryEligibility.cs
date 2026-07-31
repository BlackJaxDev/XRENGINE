namespace XREngine;

/// <summary>
/// Exact outcome of Vulkan indirect-draw secondary eligibility evaluation.
/// </summary>
public enum EVulkanIndirectSecondaryEligibility : byte
{
    NotEvaluated = 0,
    EligibleProducerComplete,
    MutableCurrentFrame,
    ProducerIncomplete,
    BufferIdentityChanged,
    InvalidRange,
    CommandChainsDisabled,
    UnsupportedInheritance,
    ResourcePreparationFailed,
    Count,
}
