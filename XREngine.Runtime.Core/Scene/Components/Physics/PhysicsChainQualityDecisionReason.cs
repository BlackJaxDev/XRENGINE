namespace XREngine.Components;

/// <summary>
/// Explains the most recent world quality-controller decision for a chain.
/// </summary>
public enum PhysicsChainQualityDecisionReason
{
    AuthoredFixedTier,
    RelevancePolicy,
    WithinBudget,
    CpuBudgetPressure,
    GpuBudgetPressure,
    PromotionHeadroom,
    MinimumResidency,
    TransitionLimit,
}
