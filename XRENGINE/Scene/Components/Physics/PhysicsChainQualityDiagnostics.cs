namespace XREngine.Components;

/// <summary>
/// Observable requested and effective quality state for one runtime chain.
/// </summary>
public readonly record struct PhysicsChainQualityDiagnostics(
    PhysicsChainQualityTier RequestedTier,
    PhysicsChainQualityTier EffectiveTier,
    PhysicsChainQualityDecisionReason Reason,
    long ResidenceFrames,
    long LastTransitionFrame,
    bool HasDelayedFeedback,
    long LastFeedbackSourceFrame,
    double SmoothedMillisecondsPerWorkUnit,
    float SmoothedNormalizedError);
