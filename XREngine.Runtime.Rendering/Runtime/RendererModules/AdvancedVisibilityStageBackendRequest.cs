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
    string CurrentDepthPyramidTargetName)
{
    public bool IsValid
        => IsStagePhaseValid() &&
           Publication.FrameId != 0u &&
           Reservation.IsValid &&
           Publication.PublicationGeneration != 0u &&
           Publication.ScenePublication.IsValid &&
           Publication.DrawCount != 0u &&
           Extractor is not null &&
           RenderFrameId != 0u &&
           Views.ViewCount > 0 &&
           Target is not null &&
           Target.Width != 0u &&
           Target.Height != 0u &&
           !string.IsNullOrWhiteSpace(IdentityTargetName) &&
           !string.IsNullOrWhiteSpace(MetadataTargetName) &&
           !string.IsNullOrWhiteSpace(SelectionTargetName) &&
           !string.IsNullOrWhiteSpace(DepthTargetName) &&
           !string.IsNullOrWhiteSpace(CurrentDepthPyramidTargetName);

    private bool IsStagePhaseValid()
        => Stage switch
        {
            EAdvancedRenderStage.VisibilityPreparation or
            EAdvancedRenderStage.VisibilityRaster =>
                Phase == EAdvancedVisibilityStageBackendPhase.Complete,
            EAdvancedRenderStage.DepthPyramidAndLateVisibility =>
                Phase is EAdvancedVisibilityStageBackendPhase.LateCompute or
                    EAdvancedVisibilityStageBackendPhase.LateRaster,
            _ => false,
        };
}
