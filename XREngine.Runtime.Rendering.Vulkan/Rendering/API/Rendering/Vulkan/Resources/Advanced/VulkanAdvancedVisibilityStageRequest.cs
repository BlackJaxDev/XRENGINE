namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable authoring identity for a Vulkan advanced visibility stage. The
/// payload data stays in the warmed extractor until late frame-slot preparation
/// validates the publication generation and copies it into set-1 storage.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityStageRequest(
    EAdvancedRenderStage Stage,
    EAdvancedVisibilityStageBackendPhase Phase,
    AdvancedVisibilityFamilyReservation Reservation,
    VulkanAdvancedVisibilityBackendPackageSnapshot BackendPackage,
    AdvancedPreparationPublication Publication,
    ulong VisibilityContentGeneration,
    AdvancedPreparationExtractor Extractor,
    ulong RenderFrameId,
    RenderFrameViewSet Views,
    XRFrameBuffer Target,
    string IdentityTargetName,
    string MetadataTargetName,
    string SelectionTargetName,
    string DepthTargetName,
    string CurrentDepthPyramidTargetName,
    EAdvancedShadingDebugView ShadingDebugView = EAdvancedShadingDebugView.Disabled,
    bool RequireNativeOutput = false)
{
    internal bool IsValid
        => ((Stage, Phase) is
            (EAdvancedRenderStage.VisibilityPreparation,
                EAdvancedVisibilityStageBackendPhase.Complete) or
            (EAdvancedRenderStage.VisibilityRaster,
                EAdvancedVisibilityStageBackendPhase.Complete) or
            (EAdvancedRenderStage.DepthPyramidAndLateVisibility,
                EAdvancedVisibilityStageBackendPhase.LateCompute) or
            (EAdvancedRenderStage.DepthPyramidAndLateVisibility,
                EAdvancedVisibilityStageBackendPhase.LateRaster) or
            (EAdvancedRenderStage.WorkClassification,
                EAdvancedVisibilityStageBackendPhase.Complete) or
            (EAdvancedRenderStage.AttributeReconstruction,
                EAdvancedVisibilityStageBackendPhase.Complete) or
            (EAdvancedRenderStage.NativeOpaqueShading,
                EAdvancedVisibilityStageBackendPhase.Complete)) &&
           Publication.FrameId != 0u &&
           Reservation.IsValid &&
           BackendPackage.IsValid &&
           Publication.PublicationGeneration != 0u &&
           Publication.ScenePublication.IsValid &&
           VisibilityContentGeneration != 0u &&
           Publication.VisibilityContentGeneration == VisibilityContentGeneration &&
           Publication.DrawCount != 0u &&
           Extractor is not null && RenderFrameId != 0u &&
           Views.ViewCount > 0 &&
           Target is not null &&
           Target.Width != 0u &&
           Target.Height != 0u &&
           !string.IsNullOrWhiteSpace(IdentityTargetName) &&
           !string.IsNullOrWhiteSpace(MetadataTargetName) &&
           !string.IsNullOrWhiteSpace(SelectionTargetName) &&
           !string.IsNullOrWhiteSpace(DepthTargetName) &&
           !string.IsNullOrWhiteSpace(CurrentDepthPyramidTargetName);

    /// <summary>
    /// Verifies that two authored stages are members of one visibility family.
    /// The stage discriminator is intentionally excluded; every other logical
    /// publication, view, target, and extractor identity must be exact.
    /// </summary>
    internal bool MatchesFamily(in VulkanAdvancedVisibilityStageRequest other)
        => Reservation.Equals(other.Reservation) &&
           RequireNativeOutput == other.RequireNativeOutput &&
           ShadingDebugView == other.ShadingDebugView &&
           BackendPackage.Equals(other.BackendPackage) &&
           Publication.Equals(other.Publication) &&
           VisibilityContentGeneration == other.VisibilityContentGeneration &&
           ReferenceEquals(Extractor, other.Extractor) &&
           RenderFrameId == other.RenderFrameId &&
           Views.Equals(other.Views) &&
           ReferenceEquals(Target, other.Target) &&
           string.Equals(IdentityTargetName, other.IdentityTargetName, StringComparison.Ordinal) &&
           string.Equals(MetadataTargetName, other.MetadataTargetName, StringComparison.Ordinal) &&
           string.Equals(SelectionTargetName, other.SelectionTargetName, StringComparison.Ordinal) &&
           string.Equals(DepthTargetName, other.DepthTargetName, StringComparison.Ordinal) &&
           string.Equals(CurrentDepthPyramidTargetName, other.CurrentDepthPyramidTargetName, StringComparison.Ordinal);
}
