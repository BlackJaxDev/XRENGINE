namespace XREngine;

public readonly record struct RvcFoveatedShadingRatePlan(
    ERvcFoveationRateBackend Backend,
    ERvcShadeletDensity MaxFragmentShadingRateDensity,
    bool RequiresComputeFor8x8,
    bool UsesCombinerOpsForNearField)
{
    public static RvcFoveatedShadingRatePlan FromBackend(ERvcFoveationRateBackend backend)
        => new(
            backend,
            MaxFragmentShadingRateDensity: ERvcShadeletDensity.Rate4x4,
            RequiresComputeFor8x8: true,
            UsesCombinerOpsForNearField: backend == ERvcFoveationRateBackend.VulkanFragmentShadingRate);
}
