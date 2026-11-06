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
    bool SuppressBaselineDiffuse = false,
    uint MsaaSampleCount = 1u,
    bool HasAuthoredBackground = false,
    VulkanAdvancedDirectionalShadowLaneStorage? DirectionalShadowLane = null)
{
    /// <summary>Render time frozen with this authored visibility family.</summary>
    internal float RenderTimeSeconds { get; init; }

    /// <summary>Applies the multi-bounce approximation to occluded diffuse light.</summary>
    internal bool AmbientOcclusionMultiBounce { get; init; }

    /// <summary>Attenuates specular light with plain AO instead of specular occlusion.</summary>
    internal bool AmbientOcclusionSpecularUsesVisibility { get; init; }

    /// <summary>
    /// Records the RVC shadelet telemetry kernel after the last native shading view.
    /// It is a per-frame diagnostic and is not part of the family identity.
    /// </summary>
    internal bool RecordRvcShadeletTelemetry { get; init; }

    /// <summary>True for the directional shadow lane, which records the family's bins into an atlas page.</summary>
    internal bool IsDirectionalShadowStage => Stage == EAdvancedRenderStage.DirectionalShadowRaster;

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
            (EAdvancedRenderStage.DirectionalShadowRaster,
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
           float.IsFinite(RenderTimeSeconds) &&
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
            !string.IsNullOrWhiteSpace(AmbientOcclusionTargetName)) &&
           (!IsDirectionalShadowStage ||
            DirectionalShadowLane is { CascadeCount: > 0 });

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
           SuppressBaselineDiffuse == other.SuppressBaselineDiffuse &&
           AmbientOcclusionMultiBounce == other.AmbientOcclusionMultiBounce &&
           AmbientOcclusionSpecularUsesVisibility == other.AmbientOcclusionSpecularUsesVisibility &&
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
           BitConverter.SingleToUInt32Bits(RenderTimeSeconds) ==
               BitConverter.SingleToUInt32Bits(other.RenderTimeSeconds) &&
           MatchesVisibilityTargets(in other) &&
           string.Equals(AmbientOcclusionTargetName, other.AmbientOcclusionTargetName, StringComparison.Ordinal) &&
           string.Equals(CurrentDepthPyramidTargetName, other.CurrentDepthPyramidTargetName, StringComparison.Ordinal);

    /// <summary>
    /// Names the first family field that differs from <paramref name="other"/>,
    /// for a rejected-family diagnostic. Returns "none" for a matching family.
    /// </summary>
    internal string DescribeFamilyMismatch(in VulkanAdvancedVisibilityStageRequest other)
    {
        if (!Reservation.Equals(other.Reservation))
            return $"Reservation ({Reservation} vs {other.Reservation})";
        if (RequireNativeOutput != other.RequireNativeOutput)
            return nameof(RequireNativeOutput);
        if (EnableBuiltInAmbientOcclusion != other.EnableBuiltInAmbientOcclusion)
            return nameof(EnableBuiltInAmbientOcclusion);
        if (EnableLightProbesAndIbl != other.EnableLightProbesAndIbl)
            return nameof(EnableLightProbesAndIbl);
        if (RequiresMaterialSurfaceExports != other.RequiresMaterialSurfaceExports)
            return nameof(RequiresMaterialSurfaceExports);
        if (SuppressBaselineDiffuse != other.SuppressBaselineDiffuse)
            return nameof(SuppressBaselineDiffuse);
        if (AmbientOcclusionMultiBounce != other.AmbientOcclusionMultiBounce)
            return nameof(AmbientOcclusionMultiBounce);
        if (AmbientOcclusionSpecularUsesVisibility != other.AmbientOcclusionSpecularUsesVisibility)
            return nameof(AmbientOcclusionSpecularUsesVisibility);
        if (MsaaSampleCount != other.MsaaSampleCount)
            return $"{nameof(MsaaSampleCount)} ({MsaaSampleCount} vs {other.MsaaSampleCount})";
        if (HasAuthoredBackground != other.HasAuthoredBackground)
            return nameof(HasAuthoredBackground);
        if (IsMinimalVisibilityOutput != other.IsMinimalVisibilityOutput)
            return nameof(IsMinimalVisibilityOutput);
        if (ShadingDebugView != other.ShadingDebugView)
            return nameof(ShadingDebugView);
        if (!BackendPackage.Equals(other.BackendPackage))
            return nameof(BackendPackage);
        if (!Publication.Equals(other.Publication))
            return $"{nameof(Publication)} ({Publication} vs {other.Publication})";
        if (VisibilityContentGeneration != other.VisibilityContentGeneration)
            return $"{nameof(VisibilityContentGeneration)} ({VisibilityContentGeneration} vs {other.VisibilityContentGeneration})";
        if (!ReferenceEquals(Extractor, other.Extractor))
            return nameof(Extractor);
        if (RenderFrameId != other.RenderFrameId)
            return $"{nameof(RenderFrameId)} ({RenderFrameId} vs {other.RenderFrameId})";
        if (!Views.Equals(other.Views))
            return nameof(Views);
        if (BitConverter.SingleToUInt32Bits(RenderTimeSeconds) != BitConverter.SingleToUInt32Bits(other.RenderTimeSeconds))
            return $"{nameof(RenderTimeSeconds)} ({RenderTimeSeconds} vs {other.RenderTimeSeconds})";
        if (!MatchesVisibilityTargets(in other))
            return $"visibility targets (stage {Stage}/{Phase} vs {other.Stage}/{other.Phase})";
        if (!string.Equals(AmbientOcclusionTargetName, other.AmbientOcclusionTargetName, StringComparison.Ordinal))
            return nameof(AmbientOcclusionTargetName);
        if (!string.Equals(CurrentDepthPyramidTargetName, other.CurrentDepthPyramidTargetName, StringComparison.Ordinal))
            return nameof(CurrentDepthPyramidTargetName);
        return "none";
    }

    private bool MatchesVisibilityTargets(in VulkanAdvancedVisibilityStageRequest other)
    {
        // The directional shadow lane targets a light-owned atlas page; it shares
        // the family's publication and views, never its visibility targets.
        if (IsDirectionalShadowStage || other.IsDirectionalShadowStage)
            return true;
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
