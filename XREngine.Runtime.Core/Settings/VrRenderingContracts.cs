namespace XREngine;

public enum EVrViewRenderMode
{
    SequentialViews,
    SinglePassStereo,
    ParallelCommandBufferRecording,
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
        bool enableOpenXrVulkanParallelRendering = true)
    {
        if (requestedMode == EVrViewRenderMode.ParallelCommandBufferRecording)
        {
            if (backend != ERenderLibrary.Vulkan)
            {
                return new(
                    requestedMode,
                    requestedMode,
                    false,
                    "VR.ViewRenderMode=ParallelCommandBufferRecording is Vulkan-only.");
            }

            if (!enableOpenXrVulkanParallelRendering)
            {
                return new(
                    requestedMode,
                    requestedMode,
                    false,
                    "VR.ViewRenderMode=ParallelCommandBufferRecording was requested, but OpenXR Vulkan parallel rendering is disabled by startup settings.");
            }
        }

        return new(requestedMode, requestedMode, true, null);
    }
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
