namespace XREngine;

/// <summary>
/// Resolves the effective VR view render mode and its associated implementation path and temporal history policy based on the requested mode and runtime conditions.
/// </summary>
public static class VrViewRenderModeResolver
{
    /// <summary>
    /// Resolves the effective VR view render mode resolution based on the requested mode and runtime conditions.
    /// </summary>
    /// <param name="backend">The rendering backend being used (e.g., Vulkan, OpenGL).</param>
    /// <param name="requestedMode">The requested VR view render mode.</param>
    /// <param name="enableOpenXrVulkanParallelRendering">Indicates whether OpenXR Vulkan parallel rendering is enabled.</param>
    /// <param name="trueSinglePassStereoAvailable">Indicates whether true single-pass stereo rendering is available.</param>
    /// <param name="rendersExternalSwapchainTargets">Indicates whether the application renders to external swapchain targets.</param>
    /// <returns>A <see cref="VrViewRenderModeResolution"/> representing the resolved VR view render mode, implementation path, and temporal history policy.</returns>
    public static VrViewRenderModeResolution Resolve(
        ERenderLibrary backend,
        EVrViewRenderMode requestedMode,
        bool enableOpenXrVulkanParallelRendering = true,
        bool trueSinglePassStereoAvailable = false,
        bool rendersExternalSwapchainTargets = true)
    {
        // Handle the special case for ParallelCommandBufferRecording mode, which has specific requirements and limitations.
        if (requestedMode == EVrViewRenderMode.ParallelCommandBufferRecording)
        {
            // Ensure that the requested mode is only used with Vulkan backend and when OpenXR Vulkan parallel rendering is enabled.
            if (backend != ERenderLibrary.Vulkan)
            {
                // Return an unsupported resolution if the backend is not Vulkan.
                return new(
                    requestedMode,
                    requestedMode,
                    EVrViewRenderImplementationPath.Unsupported,
                    EVrTemporalHistoryPolicy.Disabled,
                    false,
                    "VR.ViewRenderMode=ParallelCommandBufferRecording is Vulkan-only.");
            }

            // Return an unsupported resolution if OpenXR Vulkan parallel rendering is not enabled.
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

        // Resolve the implementation path based on the requested mode and runtime conditions.
        EVrViewRenderImplementationPath implementationPath = ResolveImplementationPath(
            requestedMode,
            trueSinglePassStereoAvailable);

        // Return the resolved VR view render mode resolution.
        return new(
            requestedMode,
            requestedMode,
            implementationPath,
            ResolveTemporalHistoryPolicy(implementationPath, rendersExternalSwapchainTargets),
            true,
            null);
    }

    /// <summary>
    /// Resolves the VR view render implementation path based on the requested view render mode and runtime conditions.
    /// </summary>
    /// <param name="requestedMode">The requested VR view render mode.</param>
    /// <param name="trueSinglePassStereoAvailable">Indicates whether true single-pass stereo rendering is available.</param>
    /// <returns>The resolved VR view render implementation path.</returns>
    private static EVrViewRenderImplementationPath ResolveImplementationPath(
        EVrViewRenderMode requestedMode,
        bool trueSinglePassStereoAvailable)
        => requestedMode switch
        {
            EVrViewRenderMode.SequentialViews => EVrViewRenderImplementationPath.SequentialViews,
            EVrViewRenderMode.ParallelCommandBufferRecording => EVrViewRenderImplementationPath.ParallelCommandBufferRecording,
            EVrViewRenderMode.SinglePassStereo => trueSinglePassStereoAvailable
                ? EVrViewRenderImplementationPath.TrueSinglePassStereo
                : EVrViewRenderImplementationPath.OpenXrSinglePassCompatibility, // Fallback to OpenXR single-pass compatibility if true single-pass stereo is not available.
            _ => EVrViewRenderImplementationPath.Unsupported,
        };

    /// <summary>
    /// Resolves the temporal history policy based on the VR view render implementation path and whether external swapchain targets are used.
    /// </summary>
    /// <param name="implementationPath">The resolved VR view render implementation path.</param>
    /// <param name="rendersExternalSwapchainTargets">Indicates whether external swapchain targets are used.</param>
    /// <returns>The resolved temporal history policy.</returns>
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
