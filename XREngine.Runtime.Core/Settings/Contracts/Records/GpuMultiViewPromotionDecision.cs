namespace XREngine;

/// <summary>Visible result of resolving an accelerated multiview promotion request.</summary>
public readonly record struct GpuMultiViewPromotionDecision(
    EGpuMultiViewPromotionLane Lane,
    uint ViewCount,
    bool Promoted,
    EGpuMultiViewPromotionBlockReason BlockReason)
{
    public bool UsesConservativePath => !Promoted;
}
