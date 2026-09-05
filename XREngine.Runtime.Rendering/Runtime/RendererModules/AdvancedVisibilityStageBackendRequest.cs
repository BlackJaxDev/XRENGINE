namespace XREngine.Rendering;

/// <summary>
/// Immutable renderer-neutral request for one advanced visibility stage.
/// Logical resource names are resolved by the backend only after the frame
/// plan has frozen its resource-generation identity.
/// </summary>
public readonly record struct AdvancedVisibilityStageBackendRequest(
    EAdvancedRenderStage Stage,
    EAdvancedVisibilityStageBackendPhase Phase,
    AdvancedVisibilityFamilyReservation Reservation,
    AdvancedPreparationPublication Publication,
    AdvancedPreparationExtractor Extractor,
    ulong RenderFrameId,
    RenderFrameViewSet Views,
    XRFrameBuffer Target,
    string IdentityTargetName,
    string MetadataTargetName,
    string SelectionTargetName,
    string DepthTargetName,
    string AmbientOcclusionTargetName,
    string CurrentDepthPyramidTargetName,
    EAdvancedShadingDebugView ShadingDebugView = EAdvancedShadingDebugView.Disabled,
    bool RequireNativeOutput = false,
    bool EnableBuiltInAmbientOcclusion = false)
{
    public bool IsValid => GetInvalidReason() is null;

    /// <summary>Reports the failed prerequisite without allocating on accepted frames.</summary>
    public string? GetInvalidReason()
    {
        if (!IsStagePhaseValid())
            return "The advanced visibility stage/phase combination is unsupported.";
        if (Publication.FrameId == 0u || Publication.PublicationGeneration == 0u)
            return "The advanced preparation publication has no frame or generation.";
        if (!Reservation.IsValid)
            return "The advanced visibility output has no valid family reservation.";
        if (!Publication.ScenePublication.IsValid)
            return "The advanced preparation publication has no resident scene publication.";
        if (Publication.DrawCount == 0u)
            return Extractor is { LastDeferralReason.Length: > 0 }
                ? Extractor.LastDeferralReason
                : "The advanced preparation publication contains no draws.";
        if (Extractor is null || RenderFrameId == 0u || Views.ViewCount == 0)
            return "The advanced visibility request has no extractor, render frame, or views.";
        if (Target is null || Target.Width == 0u || Target.Height == 0u)
            return "The advanced visibility target has no renderable extent.";
        if (string.IsNullOrWhiteSpace(IdentityTargetName) ||
            string.IsNullOrWhiteSpace(MetadataTargetName) ||
            string.IsNullOrWhiteSpace(SelectionTargetName) ||
            string.IsNullOrWhiteSpace(DepthTargetName) ||
            string.IsNullOrWhiteSpace(AmbientOcclusionTargetName) ||
            string.IsNullOrWhiteSpace(CurrentDepthPyramidTargetName))
            return "The advanced visibility request is missing required resource names.";
        return null;
    }

    private bool IsStagePhaseValid()
        => Stage switch
        {
            EAdvancedRenderStage.VisibilityPreparation or
            EAdvancedRenderStage.VisibilityRaster =>
                Phase == EAdvancedVisibilityStageBackendPhase.Complete,
            EAdvancedRenderStage.DepthPyramidAndLateVisibility =>
                Phase is EAdvancedVisibilityStageBackendPhase.LateCompute or
                    EAdvancedVisibilityStageBackendPhase.LateRaster,
            // These stages are compute-only.  Their Vulkan implementation
            // seals a per-stage descriptor closure from the frozen graph
            // generation before recording; they do not participate in the
            // visibility raster's split late-compute/late-raster phase.
            EAdvancedRenderStage.WorkClassification or
            EAdvancedRenderStage.AmbientOcclusion or
            EAdvancedRenderStage.NativeOpaqueShading =>
                Phase == EAdvancedVisibilityStageBackendPhase.Complete,
            _ => false,
        };
}
