namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable authoring identity for a Vulkan advanced visibility stage. The
/// payload data stays in the warmed extractor until late frame-slot preparation
/// validates the publication generation and copies it into set-1 storage.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityStageRequest(
    EAdvancedRenderStage Stage,
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
    string CurrentDepthPyramidTargetName)
{
    internal bool IsValid
        => Stage is EAdvancedRenderStage.VisibilityPreparation or
            EAdvancedRenderStage.VisibilityRaster or
            EAdvancedRenderStage.DepthPyramidAndLateVisibility &&
           Publication.FrameId != 0u &&
           Publication.PublicationGeneration != 0u &&
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
        => Publication.Equals(other.Publication) &&
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
