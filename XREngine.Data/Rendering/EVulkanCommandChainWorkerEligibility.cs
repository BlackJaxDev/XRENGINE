namespace XREngine;

/// <summary>
/// Exact outcome of command-chain worker eligibility evaluation.
/// </summary>
public enum EVulkanCommandChainWorkerEligibility : byte
{
    NotEvaluated = 0,
    Eligible,
    TooLittleIndependentWork,
    MutableRendererConflict,
    UnsupportedOperation,
    UnsupportedInheritance,
    PrimaryOwnedIndirectStream,
    WorkerQuarantined,
    ResourcePreparationFailed,
    Count,
}
