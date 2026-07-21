namespace XREngine.Components;

/// <summary>
/// Aggregate result of the latest world physics-chain budget evaluation.
/// Work is expressed in deterministic normalized units rather than timing.
/// </summary>
public readonly record struct PhysicsChainQualityBudgetDiagnostics(
    long Frame,
    long CpuBudgetWorkUnits,
    long CpuEffectiveWorkUnits,
    long CpuEffectiveBudgetWorkUnits,
    long GpuBudgetWorkUnits,
    long GpuEffectiveWorkUnits,
    long GpuEffectiveBudgetWorkUnits,
    int AutomaticChainCount,
    int Transitions,
    int DeferredByResidency,
    int DeferredByTransitionLimit,
    long AcceptedFeedbackSamples,
    long RejectedFeedbackSamples,
    long LastCpuFeedbackSourceFrame,
    long LastGpuFeedbackSourceFrame,
    double SmoothedCpuMillisecondsPerWorkUnit,
    double SmoothedGpuMillisecondsPerWorkUnit);
