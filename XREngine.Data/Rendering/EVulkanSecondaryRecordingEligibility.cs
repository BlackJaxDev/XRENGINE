namespace XREngine;

/// <summary>
/// Exact outcome of non-graphics Vulkan secondary-command eligibility evaluation.
/// </summary>
public enum EVulkanSecondaryRecordingEligibility : byte
{
    NotEvaluated = 0,
    Eligible,
    FamilyDisabled,
    SecondaryCommandBuffersDisabled,
    EmptyRange,
    QueueFamilyUnsupported,
    ActiveRenderScope,
    QueryInheritanceUnsupported,
    BarrierPlanUnavailable,
    QueryResetPrimaryOwned,
    QueryPairPrimaryOwned,
    QueryTimestampPrimaryOwned,
    QueryPropertiesPrimaryOwned,
    QueryResultOrderingUnavailable,
    InvalidOperationState,
    Count,
}
