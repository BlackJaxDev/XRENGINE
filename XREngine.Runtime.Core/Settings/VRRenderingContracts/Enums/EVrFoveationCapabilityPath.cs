namespace XREngine;

[Flags]
public enum EVrFoveationCapabilityPath
{
    None = 0,
    VulkanFragmentShadingRate = 1 << 0,
    VulkanFragmentDensityMap = 1 << 1,
    OpenXrRuntimeFoveation = 1 << 2,
    OpenXrQuadViews = 1 << 3,
    OpenGlFixedFoveationExtension = 1 << 4,
    OpenGlMultiResolution = 1 << 5,
}
