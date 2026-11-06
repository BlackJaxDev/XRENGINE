namespace XREngine;

public readonly record struct VrFoveationBackendCapabilities(
    bool VulkanFragmentShadingRate,
    bool VulkanFragmentDensityMap,
    bool OpenXrRuntimeFoveation,
    bool OpenXrQuadViews,
    bool OpenGlFixedFoveationExtension,
    bool OpenGlMultiResolution)
{
    /// <summary>
    /// The XR runtime reports a gaze direction (for example XR_EXT_eye_gaze_interaction). With a gaze
    /// source, an engine shading-rate path can follow the gaze instead of a fixed center.
    /// </summary>
    public bool OpenXrEyeGaze { get; init; }

    public static VrFoveationBackendCapabilities None => default;
}
