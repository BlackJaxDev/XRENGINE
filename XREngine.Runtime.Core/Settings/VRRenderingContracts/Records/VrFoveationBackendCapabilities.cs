namespace XREngine;

public readonly record struct VrFoveationBackendCapabilities(
    bool VulkanFragmentShadingRate,
    bool VulkanFragmentDensityMap,
    bool OpenXrRuntimeFoveation,
    bool OpenXrQuadViews,
    bool OpenGlFixedFoveationExtension,
    bool OpenGlMultiResolution)
{
    public static VrFoveationBackendCapabilities None => default;
}
