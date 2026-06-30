namespace XREngine;

public enum EVrViewRenderMode
{
    /// <summary>
    /// Render the two eye views one after the other.
    /// </summary>
    SequentialViews,

    /// <summary>
    /// Request a stereo render path. OpenXR Vulkan uses true stereo when the
    /// layered staging path is available, and otherwise reports a compatibility
    /// path over per-eye swapchains.
    /// </summary>
    SinglePassStereo,

    /// <summary>
    /// Render the same per-eye output as sequential views while preparing safe
    /// command-buffer work concurrently where the backend supports it.
    /// </summary>
    ParallelCommandBufferRecording,
}

public enum EVrViewRenderImplementationPath
{
    Unsupported,
    SequentialViews,
    ParallelCommandBufferRecording,
    OpenXrSinglePassCompatibility,
    TrueSinglePassStereo,
}

public enum EVrTemporalHistoryPolicy
{
    Disabled,
    DisabledPerEyeSwapchain,
    DisabledExternalPerEyeSwapchain,
    PerEye,
    StereoArrayLayer,
    HeadsetShared,
}

public enum EVrAutoExposurePolicy
{
    HeadsetShared,
    PerEye,
    LeftEyeOnly,
}

public enum EVrFoveationMode
{
    Off,
    Fixed,
    EyeTracked,
    RuntimePreferred,
}

public enum EVrFoveationQualityPreset
{
    Conservative,
    Balanced,
    Aggressive,
}

public enum EOpenXrEyeResolutionPreset
{
    /// <summary>
    /// Use the active OpenXR runtime's recommended image rect size.
    /// </summary>
    RuntimeRecommended,

    /// <summary>
    /// Valve Index native panel resolution, 1440 x 1600 per eye.
    /// </summary>
    ValveIndex,

    /// <summary>
    /// Meta Quest Pro native panel resolution, 1800 x 1920 per eye.
    /// </summary>
    QuestPro,

    /// <summary>
    /// Bigscreen Beyond 2 native panel resolution, 2560 x 2560 per eye.
    /// </summary>
    BigscreenBeyond2,

    /// <summary>
    /// Use the configured custom width and height.
    /// </summary>
    Custom,
}

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

public readonly record struct VrViewRenderModeResolution(
    EVrViewRenderMode RequestedMode,
    EVrViewRenderMode EffectiveMode,
    EVrViewRenderImplementationPath EffectiveImplementationPath,
    EVrTemporalHistoryPolicy TemporalHistoryPolicy,
    bool IsSupported,
    string? Diagnostic);

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

public readonly record struct VrFoveationResolution(
    EVrFoveationMode RequestedMode,
    EVrFoveationMode EffectiveMode,
    EVrFoveationQualityPreset QualityPreset,
    EVrFoveationCapabilityPath CapabilityPath,
    bool IsSupported,
    string? Diagnostic);

public static class VrViewRenderModeResolver
{
    public static VrViewRenderModeResolution Resolve(
        ERenderLibrary backend,
        EVrViewRenderMode requestedMode,
        bool enableOpenXrVulkanParallelRendering = true,
        bool trueSinglePassStereoAvailable = false,
        bool rendersExternalSwapchainTargets = true)
    {
        if (requestedMode == EVrViewRenderMode.ParallelCommandBufferRecording)
        {
            if (backend != ERenderLibrary.Vulkan)
            {
                return new(
                    requestedMode,
                    requestedMode,
                    EVrViewRenderImplementationPath.Unsupported,
                    EVrTemporalHistoryPolicy.Disabled,
                    false,
                    "VR.ViewRenderMode=ParallelCommandBufferRecording is Vulkan-only.");
            }

            if (!enableOpenXrVulkanParallelRendering)
            {
                return new(
                    requestedMode,
                    requestedMode,
                    EVrViewRenderImplementationPath.Unsupported,
                    EVrTemporalHistoryPolicy.Disabled,
                    false,
                    "VR.ViewRenderMode=ParallelCommandBufferRecording was requested, but OpenXR Vulkan parallel rendering is disabled by startup settings.");
            }
        }

        EVrViewRenderImplementationPath implementationPath = ResolveImplementationPath(
            requestedMode,
            trueSinglePassStereoAvailable);

        return new(
            requestedMode,
            requestedMode,
            implementationPath,
            ResolveTemporalHistoryPolicy(implementationPath, rendersExternalSwapchainTargets),
            true,
            null);
    }

    private static EVrViewRenderImplementationPath ResolveImplementationPath(
        EVrViewRenderMode requestedMode,
        bool trueSinglePassStereoAvailable)
        => requestedMode switch
        {
            EVrViewRenderMode.SequentialViews => EVrViewRenderImplementationPath.SequentialViews,
            EVrViewRenderMode.ParallelCommandBufferRecording => EVrViewRenderImplementationPath.ParallelCommandBufferRecording,
            EVrViewRenderMode.SinglePassStereo => trueSinglePassStereoAvailable
                ? EVrViewRenderImplementationPath.TrueSinglePassStereo
                : EVrViewRenderImplementationPath.OpenXrSinglePassCompatibility,
            _ => EVrViewRenderImplementationPath.Unsupported,
        };

    private static EVrTemporalHistoryPolicy ResolveTemporalHistoryPolicy(
        EVrViewRenderImplementationPath implementationPath,
        bool rendersExternalSwapchainTargets)
        => implementationPath switch
        {
            EVrViewRenderImplementationPath.TrueSinglePassStereo => EVrTemporalHistoryPolicy.StereoArrayLayer,
            EVrViewRenderImplementationPath.OpenXrSinglePassCompatibility => EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain,
            EVrViewRenderImplementationPath.SequentialViews or EVrViewRenderImplementationPath.ParallelCommandBufferRecording =>
                rendersExternalSwapchainTargets
                    ? EVrTemporalHistoryPolicy.DisabledExternalPerEyeSwapchain
                    : EVrTemporalHistoryPolicy.DisabledPerEyeSwapchain,
            _ => EVrTemporalHistoryPolicy.Disabled,
        };
}

public static class VrFoveationResolver
{
    public static VrFoveationResolution Resolve(
        ERenderLibrary backend,
        EVrFoveationMode requestedMode,
        EVrFoveationQualityPreset qualityPreset,
        bool requireRequested,
        VrFoveationBackendCapabilities capabilities)
    {
        if (requestedMode == EVrFoveationMode.Off)
            return new(requestedMode, EVrFoveationMode.Off, qualityPreset, EVrFoveationCapabilityPath.None, true, null);

        EVrFoveationCapabilityPath path = backend switch
        {
            ERenderLibrary.Vulkan => ResolveVulkanPath(requestedMode, capabilities),
            ERenderLibrary.OpenGL => ResolveOpenGlPath(requestedMode, capabilities),
            _ => EVrFoveationCapabilityPath.None,
        };

        if (path != EVrFoveationCapabilityPath.None)
        {
            EVrFoveationMode effective = requestedMode == EVrFoveationMode.RuntimePreferred
                ? ResolveRuntimePreferredEffectiveMode(path)
                : requestedMode;
            return new(requestedMode, effective, qualityPreset, path, true, null);
        }

        string diagnostic = backend == ERenderLibrary.OpenGL
            ? $"VR.Foveation.Mode={requestedMode} is not supported on OpenGL without an explicit fixed-foveation extension or multi-resolution path."
            : $"VR.Foveation.Mode={requestedMode} is not supported by the detected Vulkan/OpenXR foveation capabilities.";

        return new(
            requestedMode,
            requireRequested ? requestedMode : EVrFoveationMode.Off,
            qualityPreset,
            EVrFoveationCapabilityPath.None,
            false,
            diagnostic);
    }

    private static EVrFoveationCapabilityPath ResolveVulkanPath(
        EVrFoveationMode requestedMode,
        VrFoveationBackendCapabilities capabilities)
    {
        if (requestedMode == EVrFoveationMode.EyeTracked)
        {
            if (capabilities.OpenXrQuadViews)
                return EVrFoveationCapabilityPath.OpenXrQuadViews;
            if (capabilities.OpenXrRuntimeFoveation)
                return EVrFoveationCapabilityPath.OpenXrRuntimeFoveation;
            return EVrFoveationCapabilityPath.None;
        }

        if (capabilities.VulkanFragmentShadingRate)
            return EVrFoveationCapabilityPath.VulkanFragmentShadingRate;
        if (capabilities.VulkanFragmentDensityMap)
            return EVrFoveationCapabilityPath.VulkanFragmentDensityMap;
        if (capabilities.OpenXrRuntimeFoveation)
            return EVrFoveationCapabilityPath.OpenXrRuntimeFoveation;
        if (capabilities.OpenXrQuadViews)
            return EVrFoveationCapabilityPath.OpenXrQuadViews;

        return EVrFoveationCapabilityPath.None;
    }

    private static EVrFoveationCapabilityPath ResolveOpenGlPath(
        EVrFoveationMode requestedMode,
        VrFoveationBackendCapabilities capabilities)
    {
        if (requestedMode == EVrFoveationMode.EyeTracked)
            return EVrFoveationCapabilityPath.None;

        if (capabilities.OpenGlFixedFoveationExtension)
            return EVrFoveationCapabilityPath.OpenGlFixedFoveationExtension;
        if (capabilities.OpenGlMultiResolution)
            return EVrFoveationCapabilityPath.OpenGlMultiResolution;

        return EVrFoveationCapabilityPath.None;
    }

    private static EVrFoveationMode ResolveRuntimePreferredEffectiveMode(EVrFoveationCapabilityPath path)
        => (path & (EVrFoveationCapabilityPath.OpenXrQuadViews | EVrFoveationCapabilityPath.OpenXrRuntimeFoveation)) != 0
            ? EVrFoveationMode.EyeTracked
            : EVrFoveationMode.Fixed;
}
