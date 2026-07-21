namespace XREngine;

/// <summary>Applies capability and explicit-validation gates to unsafe multiview lanes.</summary>
public static class GpuMultiViewPromotionResolver
{
    public static GpuMultiViewPromotionDecision Resolve(
        in GpuMultiViewPromotionRequest request,
        in GpuMultiViewPromotionCapabilities capabilities)
    {
        if (!request.Requested)
            return Blocked(request, EGpuMultiViewPromotionBlockReason.NotRequested);
        if (!request.ValidationAuthorized)
            return Blocked(request, EGpuMultiViewPromotionBlockReason.ValidationNotAuthorized);
        if (!SupportsViewCount(request.ViewCount, capabilities))
            return Blocked(request, EGpuMultiViewPromotionBlockReason.UnsupportedViewCount);
        if (!capabilities.ExactViewMasks)
            return Blocked(request, EGpuMultiViewPromotionBlockReason.ExactViewMasksUnavailable);
        if (!capabilities.StableLogicalViewMapping)
            return Blocked(request, EGpuMultiViewPromotionBlockReason.StableLogicalViewMappingUnavailable);
        if (request.Lane == EGpuMultiViewPromotionLane.ExternalOpenXrPerFamilyCulling &&
            !capabilities.PerFamilyCullingOwner)
            return Blocked(request, EGpuMultiViewPromotionBlockReason.PerFamilyCullingOwnerUnavailable);
        if (request.Lane == EGpuMultiViewPromotionLane.MeshletStereoQuadOcclusion &&
            !capabilities.LayeredHiZ)
            return Blocked(request, EGpuMultiViewPromotionBlockReason.LayeredHiZUnavailable);

        return new(request.Lane, request.ViewCount, true, EGpuMultiViewPromotionBlockReason.None);
    }

    private static bool SupportsViewCount(
        uint viewCount,
        in GpuMultiViewPromotionCapabilities capabilities)
        => viewCount switch
        {
            2u => capabilities.SupportsStereo,
            4u => capabilities.SupportsQuad,
            _ => false,
        };

    private static GpuMultiViewPromotionDecision Blocked(
        in GpuMultiViewPromotionRequest request,
        EGpuMultiViewPromotionBlockReason reason)
        => new(request.Lane, request.ViewCount, false, reason);
}
