namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    /// <summary>
    /// Renders a view into a graphics-backend-owned swapchain image.
    /// </summary>
    /// <param name="textureHandle">Backend image handle for the active view.</param>
    /// <param name="viewIndex">Index of the OpenXR view being rendered.</param>
    public delegate void DelRenderToFBO(uint textureHandle, uint viewIndex);

    public bool CanUseTrueSinglePassStereo
        => Window?.Renderer is AbstractRenderer renderer &&
           TryGetOrCreateGraphicsBinding(renderer, out IXrGraphicsBinding? binding) &&
           binding.CanUseTrueSinglePassStereo;

    internal bool TryRenderDesktopMirrorComposition(uint targetWidth, uint targetHeight)
        => Window?.Renderer is AbstractRenderer renderer &&
           TryGetOrCreateGraphicsBinding(renderer, out IXrGraphicsBinding? binding) &&
           binding.TryRenderDesktopMirrorComposition(this, targetWidth, targetHeight);

    public OpenXrSmokeCaptureLedgerEntry[] GetStrictSpsBoundaryCaptureLedger()
        => _graphicsBinding?.GetStrictSpsBoundaryCaptureLedger() ?? [];

    private bool TryResolveOpenXrViewRenderModeForCurrentBackend(
        out VrViewRenderModeResolution resolution)
    {
        ERenderLibrary backend = Window?.Renderer.BackendId == RendererBackendId.Vulkan
            ? ERenderLibrary.Vulkan
            : ERenderLibrary.OpenGL;

        string? trueSinglePassStereoUnavailableReason = null;
        bool trueSinglePassStereoAvailable =
            RuntimeRenderingHostServices.Presentation.VrViewRenderMode == EVrViewRenderMode.SinglePassStereo &&
            backend == ERenderLibrary.Vulkan &&
            CanUseTrueSinglePassStereo;

        if (RuntimeRenderingHostServices.Presentation.VrViewRenderMode == EVrViewRenderMode.SinglePassStereo &&
            backend != ERenderLibrary.Vulkan)
        {
            trueSinglePassStereoUnavailableReason =
                $"OpenXR backend {backend} does not implement an engine-owned layered multiview target";
        }

        resolution = VrViewRenderModeResolver.Resolve(
            backend,
            RuntimeRenderingHostServices.Presentation.VrViewRenderMode,
            RuntimeRenderingHostServices.Presentation.EnableOpenXrVulkanParallelRendering,
            trueSinglePassStereoAvailable,
            rendersExternalSwapchainTargets: !trueSinglePassStereoAvailable,
            trueSinglePassStereoUnavailableReason: trueSinglePassStereoUnavailableReason);
        RecordSmokeViewRenderModeResolution(resolution);

        if (resolution.IsSupported)
            return true;

        Debug.RenderingWarningEvery(
            $"OpenXR.ViewRenderMode.Unsupported.{backend}.{resolution.RequestedMode}",
            TimeSpan.FromSeconds(5),
            "[OpenXR] Unsupported VR.ViewRenderMode={0} for backend {1}. {2}",
            resolution.RequestedMode,
            backend,
            resolution.Diagnostic ?? "No fallback was applied.");
        RecordSmokeFailureOnce(
            $"Unsupported VR.ViewRenderMode={resolution.RequestedMode} for backend {backend}. " +
            $"{resolution.Diagnostic ?? "No fallback was applied."}");
        return false;
    }

    private void ResetGraphicsBackendDiagnostics()
    {
        if (Window?.Renderer is AbstractRenderer renderer &&
            TryGetOrCreateGraphicsBinding(renderer, out IXrGraphicsBinding? binding))
        {
            binding.ResetBackendDiagnostics(this);
        }
    }

    private void DestroyGraphicsBackendResources()
    {
        if (Window?.Renderer is AbstractRenderer renderer &&
            TryGetOrCreateGraphicsBinding(renderer, out IXrGraphicsBinding? binding))
        {
            binding.DestroyBackendResources(this);
        }
    }
}
