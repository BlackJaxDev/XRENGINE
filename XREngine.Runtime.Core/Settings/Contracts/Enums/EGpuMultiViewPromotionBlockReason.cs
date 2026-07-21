namespace XREngine;

/// <summary>Reason an accelerated multiview lane remains on its conservative path.</summary>
public enum EGpuMultiViewPromotionBlockReason : byte
{
    None,
    NotRequested,
    ValidationNotAuthorized,
    UnsupportedViewCount,
    ExactViewMasksUnavailable,
    StableLogicalViewMappingUnavailable,
    PerFamilyCullingOwnerUnavailable,
    LayeredHiZUnavailable,
}
