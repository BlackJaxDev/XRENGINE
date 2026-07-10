namespace XREngine;

/// <summary>
/// Resolves the VR foveation settings based on the requested mode, quality preset, and backend capabilities.
/// </summary>
public static class VrFoveationResolver
{
    /// <summary>
    /// Resolves the VR foveation settings based on the requested mode, quality preset, and backend capabilities.
    /// </summary>
    /// <param name="backend">The rendering backend being used (e.g., Vulkan, OpenGL).</param>
    /// <param name="requestedMode">The requested VR foveation mode.</param>
    /// <param name="qualityPreset">The requested VR foveation quality preset.</param>
    /// <param name="requireRequested">Indicates whether the requested mode is required to be used.</param>
    /// <param name="capabilities">The backend capabilities related to VR foveation.</param>
    /// <returns>The resolved VR foveation settings.</returns>
    public static VrFoveationResolution Resolve(
        ERenderLibrary backend,
        EVrFoveationMode requestedMode,
        EVrFoveationQualityPreset qualityPreset,
        bool requireRequested,
        VrFoveationBackendCapabilities capabilities)
    {
        // Handle the special case where the requested foveation mode is Off, which bypasses all capability checks.
        if (requestedMode == EVrFoveationMode.Off)
            return new(requestedMode, EVrFoveationMode.Off, qualityPreset, EVrFoveationCapabilityPath.None, true, null);

        // Determine the capability path based on the backend and requested mode.
        EVrFoveationCapabilityPath path = backend switch
        {
            ERenderLibrary.Vulkan => ResolveVulkanPath(requestedMode, capabilities),
            ERenderLibrary.OpenGL => ResolveOpenGLPath(requestedMode, capabilities),
            _ => EVrFoveationCapabilityPath.None,
        };

        // If a valid capability path is found, determine the effective foveation mode and return the resolved settings.
        if (path != EVrFoveationCapabilityPath.None)
        {
            // Determine the effective foveation mode based on the requested mode and the resolved capability path.
            EVrFoveationMode effective = requestedMode == EVrFoveationMode.RuntimePreferred
                ? ResolveRuntimePreferredEffectiveMode(path)
                : requestedMode;
            
            // At this point, the effective mode has been determined and can be used to construct the resolved settings.
            return new(requestedMode, effective, qualityPreset, path, true, null);
        }

        // If no valid capability path is found, construct a diagnostic message and return the unresolved settings.
        string diagnostic = backend == ERenderLibrary.OpenGL
            ? $"VR.Foveation.Mode={requestedMode} is not supported on OpenGL without an explicit fixed-foveation extension or multi-resolution path."
            : $"VR.Foveation.Mode={requestedMode} is not supported by the detected Vulkan/OpenXR foveation capabilities.";

        // Return the unresolved settings with the constructed diagnostic message.
        return new(
            requestedMode,
            requireRequested ? requestedMode : EVrFoveationMode.Off,
            qualityPreset,
            EVrFoveationCapabilityPath.None,
            false,
            diagnostic);
    }

    /// <summary>
    /// Resolves the effective VR foveation mode when the requested mode is set to RuntimePreferred.
    /// </summary>
    /// <param name="requestedMode">The requested VR foveation mode, expected to be RuntimePreferred.</param>
    /// <param name="capabilities">The backend capabilities for VR foveation.</param>
    /// <returns>The resolved effective VR foveation mode based on the backend capabilities.</returns>
    private static EVrFoveationCapabilityPath ResolveVulkanPath(
        EVrFoveationMode requestedMode,
        VrFoveationBackendCapabilities capabilities)
    {
        // Handle the special case for eye-tracked foveation mode, which has specific backend requirements.
        if (requestedMode == EVrFoveationMode.EyeTracked)
        {
            // Check for the available Vulkan/OpenXR paths for eye-tracked foveation.
            if (capabilities.OpenXrQuadViews)
                return EVrFoveationCapabilityPath.OpenXrQuadViews;

            // Check for the OpenXR multi-resolution path for eye-tracked foveation.
            if (capabilities.OpenXrRuntimeFoveation)
                return EVrFoveationCapabilityPath.OpenXrRuntimeFoveation;

            // If no specific Vulkan/OpenXR paths are available for eye-tracked foveation, return None.
            return EVrFoveationCapabilityPath.None;
        }

        // Check for the available Vulkan/OpenXR paths for the requested foveation mode in order of preference.
        if (capabilities.VulkanFragmentShadingRate)
            return EVrFoveationCapabilityPath.VulkanFragmentShadingRate;

        // Check for the Vulkan fragment density map path next.
        if (capabilities.VulkanFragmentDensityMap)
            return EVrFoveationCapabilityPath.VulkanFragmentDensityMap;

        // Check for the OpenXR runtime foveation path next.
        if (capabilities.OpenXrRuntimeFoveation)
            return EVrFoveationCapabilityPath.OpenXrRuntimeFoveation;

        // Check for the OpenXR quad views path next.
        if (capabilities.OpenXrQuadViews)
            return EVrFoveationCapabilityPath.OpenXrQuadViews;

        // If no supported Vulkan/OpenXR foveation paths are available, return None.
        return EVrFoveationCapabilityPath.None;
    }

    /// <summary>
    /// Resolves the VR foveation capability path for the OpenGL backend based on the requested foveation mode and backend capabilities.
    /// </summary>
    /// <param name="requestedMode">The requested VR foveation mode.</param>
    /// <param name="capabilities">The backend capabilities for VR foveation.</param>
    /// <returns>The resolved VR foveation capability path for the OpenGL backend.</returns>
    private static EVrFoveationCapabilityPath ResolveOpenGLPath(
        EVrFoveationMode requestedMode,
        VrFoveationBackendCapabilities capabilities)
    {
        // Handle the special case for eye-tracked foveation mode, which is not supported on OpenGL.
        if (requestedMode == EVrFoveationMode.EyeTracked)
            return EVrFoveationCapabilityPath.None;

        // Resolve the foveation capability path for the OpenGL backend based on the available extensions and features.
        if (capabilities.OpenGlFixedFoveationExtension)
            return EVrFoveationCapabilityPath.OpenGlFixedFoveationExtension;
        
        // Check for the OpenGL multi-resolution extension next.
        if (capabilities.OpenGlMultiResolution)
            return EVrFoveationCapabilityPath.OpenGlMultiResolution;

        // If no supported OpenGL foveation extensions are available, return None.
        return EVrFoveationCapabilityPath.None;
    }

    /// <summary>
    /// Resolves the runtime-preferred effective VR foveation mode based on the capability path.
    /// </summary>
    /// <param name="path">The resolved VR foveation capability path.</param>
    /// <returns>The runtime-preferred effective VR foveation mode.</returns>
    private static EVrFoveationMode ResolveRuntimePreferredEffectiveMode(EVrFoveationCapabilityPath path)
        => (path & (EVrFoveationCapabilityPath.OpenXrQuadViews | EVrFoveationCapabilityPath.OpenXrRuntimeFoveation)) != 0
            ? EVrFoveationMode.EyeTracked
            : EVrFoveationMode.Fixed;
}
