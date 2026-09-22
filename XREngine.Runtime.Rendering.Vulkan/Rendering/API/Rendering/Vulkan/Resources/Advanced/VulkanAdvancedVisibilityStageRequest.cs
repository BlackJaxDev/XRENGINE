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
    string AmbientOcclusionTargetName,
    string CurrentDepthPyramidTargetName,
    EAdvancedShadingDebugView ShadingDebugView = EAdvancedShadingDebugView.Disabled,
    bool RequireNativeOutput = false,
    bool EnableBuiltInAmbientOcclusion = false,
    bool EnableLightProbesAndIbl = false,
    bool IsMinimalVisibilityOutput = false,
    uint NativeViewIndex = 0u,
    bool RequiresMaterialSurfaceExports = false,
    uint MsaaSampleCount = 1u,
    bool HasAuthoredBackground = false)
{
    /// <summary>
    /// Native-compute closure capture is required only by stages that consume
    /// reconstruction, classification, or shading resources. Visibility-only
    /// exports deliberately omit those graph resources.
    /// </summary>
    internal bool RequiresNativeComputeClosure
        => Stage is EAdvancedRenderStage.WorkClassification or
            EAdvancedRenderStage.AmbientOcclusion or
            EAdvancedRenderStage.NativeOpaqueShading;

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
            (EAdvancedRenderStage.DepthPyramidAndLateVisibility,
                EAdvancedVisibilityStageBackendPhase.MultisampleResolve) or
            (EAdvancedRenderStage.WorkClassification,
                EAdvancedVisibilityStageBackendPhase.Complete) or
            (EAdvancedRenderStage.AmbientOcclusion,
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
           Extractor is not null && RenderFrameId != 0u &&
           Views.ViewCount > 0 &&
           NativeViewIndex < (uint)Views.ViewCount &&
           Target is not null &&
           Target.Width != 0u &&
           Target.Height != 0u &&
           !string.IsNullOrWhiteSpace(IdentityTargetName) &&
           !string.IsNullOrWhiteSpace(MetadataTargetName) &&
           !string.IsNullOrWhiteSpace(SelectionTargetName) &&
           !string.IsNullOrWhiteSpace(DepthTargetName) &&
           !string.IsNullOrWhiteSpace(CurrentDepthPyramidTargetName) &&
           (!RequiresNativeComputeClosure ||
            !string.IsNullOrWhiteSpace(AmbientOcclusionTargetName));

    /// <summary>
    /// Verifies that two authored stages are members of one visibility family.
    /// The stage and native-view ordinal are intentionally excluded. MSAA's
    /// resolve changes from raw raster attachments to canonical attachments
    /// within the same logical family, while the publication and view identity
    /// must remain exact.
    /// </summary>
    internal bool MatchesFamily(in VulkanAdvancedVisibilityStageRequest other)
        => Reservation.Equals(other.Reservation) &&
           RequireNativeOutput == other.RequireNativeOutput &&
           EnableBuiltInAmbientOcclusion == other.EnableBuiltInAmbientOcclusion &&
           EnableLightProbesAndIbl == other.EnableLightProbesAndIbl &&
           RequiresMaterialSurfaceExports == other.RequiresMaterialSurfaceExports &&
           MsaaSampleCount == other.MsaaSampleCount &&
           HasAuthoredBackground == other.HasAuthoredBackground &&
           IsMinimalVisibilityOutput == other.IsMinimalVisibilityOutput &&
           ShadingDebugView == other.ShadingDebugView &&
           BackendPackage.Equals(other.BackendPackage) &&
           Publication.Equals(other.Publication) &&
           VisibilityContentGeneration == other.VisibilityContentGeneration &&
           ReferenceEquals(Extractor, other.Extractor) &&
           RenderFrameId == other.RenderFrameId &&
           Views.Equals(other.Views) &&
           MatchesVisibilityTargets(in other) &&
           string.Equals(AmbientOcclusionTargetName, other.AmbientOcclusionTargetName, StringComparison.Ordinal) &&
           string.Equals(CurrentDepthPyramidTargetName, other.CurrentDepthPyramidTargetName, StringComparison.Ordinal);

    private bool MatchesVisibilityTargets(in VulkanAdvancedVisibilityStageRequest other)
    {
        bool raw = UsesRawMultisampleTargets;
        bool otherRaw = other.UsesRawMultisampleTargets;
        if (!HasExpectedVisibilityTargetNames(raw) || !other.HasExpectedVisibilityTargetNames(otherRaw))
            return false;

        if (raw == otherRaw)
            return ReferenceEquals(Target, other.Target);

        // The raw and resolved FBOs are distinct objects in one generation.
        // The reservation identifies that generation; both must cover the
        // same view-sized visibility surface.
        return Target.Width == other.Target.Width && Target.Height == other.Target.Height;
    }

    private bool UsesRawMultisampleTargets
        => MsaaSampleCount > 1u &&
           (Stage is EAdvancedRenderStage.VisibilityPreparation or
               EAdvancedRenderStage.VisibilityRaster ||
            Stage == EAdvancedRenderStage.DepthPyramidAndLateVisibility &&
            Phase != EAdvancedVisibilityStageBackendPhase.MultisampleResolve);

    private bool HasExpectedVisibilityTargetNames(bool raw)
        => string.Equals(IdentityTargetName, raw ? AdvancedVisibilityResourceNames.IdentityMultisample : AdvancedVisibilityResourceNames.Identity, StringComparison.Ordinal) &&
           string.Equals(MetadataTargetName, raw ? AdvancedVisibilityResourceNames.MetadataMultisample : AdvancedVisibilityResourceNames.Metadata, StringComparison.Ordinal) &&
           string.Equals(SelectionTargetName, raw ? AdvancedVisibilityResourceNames.SelectionMultisample : AdvancedVisibilityResourceNames.Selection, StringComparison.Ordinal) &&
           string.Equals(DepthTargetName, raw ? AdvancedVisibilityResourceNames.DepthStencilMultisample : AdvancedVisibilityResourceNames.DepthStencil, StringComparison.Ordinal);
}
