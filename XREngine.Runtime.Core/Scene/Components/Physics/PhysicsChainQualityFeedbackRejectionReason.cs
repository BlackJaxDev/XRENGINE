namespace XREngine.Components;

/// <summary>Why delayed quality feedback was not accepted by the world.</summary>
public enum PhysicsChainQualityFeedbackRejectionReason : byte
{
    None,
    InvalidSample,
    InvalidHandle,
    StaleGeneration,
    NotAutomatic,
    BackendMismatch,
    CurrentOrFutureFrame,
    Expired,
    OutOfOrder,
}
