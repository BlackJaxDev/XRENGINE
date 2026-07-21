namespace XREngine;

/// <summary>Two-factor request to promote a validated accelerated multiview lane.</summary>
public readonly record struct GpuMultiViewPromotionRequest(
    EGpuMultiViewPromotionLane Lane,
    uint ViewCount,
    bool Requested,
    bool ValidationAuthorized);
