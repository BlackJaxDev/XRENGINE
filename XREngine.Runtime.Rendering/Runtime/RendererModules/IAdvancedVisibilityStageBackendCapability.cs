namespace XREngine.Rendering;

/// <summary>
/// Optional renderer-owned execution path for the first GPU-only advanced
/// visibility lane. The command layer remains renderer-neutral: it only
/// freezes the prepared publication and logical render-resource identities.
/// </summary>
public interface IAdvancedVisibilityStageBackendCapability
{
    /// <summary>True when the backend can record the requested stage without CPU visibility fallback or readback.</summary>
    bool SupportsAdvancedVisibilityStage(EAdvancedRenderStage stage);

    /// <summary>
    /// Enqueues one sealed advanced visibility stage. A <c>false</c> result is
    /// a visible capability rejection; callers must not substitute CPU work.
    /// </summary>
    bool TryEnqueueAdvancedVisibilityStage(
        in AdvancedVisibilityStageBackendRequest request,
        out string failureReason);
}
