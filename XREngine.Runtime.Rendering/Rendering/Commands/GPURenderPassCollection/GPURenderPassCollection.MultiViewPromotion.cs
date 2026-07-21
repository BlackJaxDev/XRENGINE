namespace XREngine.Rendering.Commands;

public sealed partial class GPURenderPassCollection
{
    private const string PromoteExternalOpenXrFamilyCullingEnvironmentVariable =
        "XRE_VK_PROMOTE_OPENXR_GPU_FAMILY_CULLING";
    private const string ValidateExternalOpenXrFamilyCullingEnvironmentVariable =
        "XRE_VK_VALIDATE_OPENXR_GPU_FAMILY_CULLING";
    private const string PromoteMeshletMultiviewOcclusionEnvironmentVariable =
        "XRE_VK_PROMOTE_MESHLET_MULTIVIEW_OCCLUSION";
    private const string ValidateMeshletMultiviewOcclusionEnvironmentVariable =
        "XRE_VK_VALIDATE_MESHLET_MULTIVIEW_OCCLUSION";

    private static readonly bool s_externalOpenXrFamilyCullingRequested =
        IsPromotionEnabled(PromoteExternalOpenXrFamilyCullingEnvironmentVariable);
    private static readonly bool s_externalOpenXrFamilyCullingValidated =
        IsPromotionEnabled(ValidateExternalOpenXrFamilyCullingEnvironmentVariable);
    private static readonly bool s_meshletMultiviewOcclusionRequested =
        IsPromotionEnabled(PromoteMeshletMultiviewOcclusionEnvironmentVariable);
    private static readonly bool s_meshletMultiviewOcclusionValidated =
        IsPromotionEnabled(ValidateMeshletMultiviewOcclusionEnvironmentVariable);

    private GpuMultiViewPromotionDecision _externalOpenXrFamilyCullingDecision;
    private GpuMultiViewPromotionDecision _meshletMultiviewOcclusionDecision;
    private bool _hasExternalOpenXrFamilyCullingDecision;
    private bool _hasMeshletMultiviewOcclusionDecision;

    /// <summary>Most recent externally visible OpenXR family-culling promotion decision.</summary>
    public GpuMultiViewPromotionDecision ExternalOpenXrFamilyCullingDecision
        => _externalOpenXrFamilyCullingDecision;

    /// <summary>Most recent meshlet stereo/quad occlusion promotion decision.</summary>
    public GpuMultiViewPromotionDecision MeshletMultiviewOcclusionDecision
        => _meshletMultiviewOcclusionDecision;

    private bool RetainExternalOpenXrSharedVisibilityException()
    {
        GpuMultiViewPromotionDecision decision = ResolvePromotion(
            EGpuMultiViewPromotionLane.ExternalOpenXrPerFamilyCulling,
            s_externalOpenXrFamilyCullingRequested,
            s_externalOpenXrFamilyCullingValidated,
            perFamilyCullingOwner: false,
            layeredHiZ: false);
        PublishPromotionDecision(decision, ref _externalOpenXrFamilyCullingDecision,
            ref _hasExternalOpenXrFamilyCullingDecision);
        return decision.UsesConservativePath;
    }

    private bool IsMeshletMultiviewHiZPromoted()
    {
        GpuMultiViewPromotionDecision decision = ResolvePromotion(
            EGpuMultiViewPromotionLane.MeshletStereoQuadOcclusion,
            s_meshletMultiviewOcclusionRequested,
            s_meshletMultiviewOcclusionValidated,
            perFamilyCullingOwner: false,
            // The current pyramid and meshlet sampling contract is sampler2D, not layered.
            layeredHiZ: false);
        PublishPromotionDecision(decision, ref _meshletMultiviewOcclusionDecision,
            ref _hasMeshletMultiviewOcclusionDecision);
        return decision.Promoted;
    }

    private GpuMultiViewPromotionDecision ResolvePromotion(
        EGpuMultiViewPromotionLane lane,
        bool requested,
        bool validationAuthorized,
        bool perFamilyCullingOwner,
        bool layeredHiZ)
    {
        uint viewCount = _activeViewCount == 0u ? 1u : _activeViewCount;
        bool exactMasks = HasCurrentFrameExactCommandViewMasks;
        bool stableLogicalViews = _cachedViewDescriptorCount == viewCount;
        GpuMultiViewPromotionRequest request = new(
            lane,
            viewCount,
            requested,
            validationAuthorized);
        GpuMultiViewPromotionCapabilities capabilities = new(
            exactMasks,
            stableLogicalViews,
            perFamilyCullingOwner,
            layeredHiZ,
            SupportsStereo: _viewSetCapacity >= 2u,
            SupportsQuad: _viewSetCapacity >= 4u);
        return GpuMultiViewPromotionResolver.Resolve(request, capabilities);
    }

    private void PublishPromotionDecision(
        in GpuMultiViewPromotionDecision decision,
        ref GpuMultiViewPromotionDecision previous,
        ref bool hasPrevious)
    {
        if (hasPrevious && previous == decision)
            return;

        previous = decision;
        hasPrevious = true;
        XREngine.Debug.Rendering(
            "GPU multiview promotion lane={0} views={1} promoted={2} blockReason={3}",
            decision.Lane,
            decision.ViewCount,
            decision.Promoted,
            decision.BlockReason);
    }

    private static bool IsPromotionEnabled(string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}
