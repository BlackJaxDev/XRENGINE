namespace XREngine;

public enum ERvcFoveationRateBackend
{
    None,
    VulkanFragmentShadingRate,
    VulkanFragmentDensityMap,
    OpenXrRuntimeFoveation,
    OpenXrQuadViews,
}
