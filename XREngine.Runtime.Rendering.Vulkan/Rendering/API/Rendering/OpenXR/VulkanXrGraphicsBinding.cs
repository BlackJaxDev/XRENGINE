using Silk.NET.OpenXR;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan implementation of the OpenXR graphics binding contract.
/// </summary>
internal sealed partial class VulkanXrGraphicsBinding : IXrGraphicsBinding
{
    private OpenXRAPI? _host;

    private OpenXRAPI Host
        => _host ?? throw new InvalidOperationException("The Vulkan OpenXR binding is not attached to an API host.");

    private void Attach(OpenXRAPI api)
        => _host = api;

    public RendererBackendId BackendId => RendererBackendId.Vulkan;
    public string BackendName => "Vulkan";
    public XRTexture2D? PreviewLeftEyeTexture => _previewLeftEyeTexture;
    public XRTexture2D? PreviewRightEyeTexture => _previewRightEyeTexture;
    public XRTexture2D? DesktopMirrorTexture => _viewportMirrorColor;

    public bool IsCompatible(AbstractRenderer renderer) => renderer is VulkanRenderer;

    public bool DestroysRuntimeInstanceOnRendererTeardown => true;
    public bool RequiresRenderThreadForTeardown => true;

    public bool RequiresRuntimeStateRenderThread(
        OpenXRAPI.OpenXrRuntimeState runtimeState,
        bool runtimeLossPending)
        => runtimeState != OpenXRAPI.OpenXrRuntimeState.SessionRunning || runtimeLossPending;

    public bool ShouldDeferSessionStart(AbstractRenderer renderer, out string reason)
        => ((VulkanRenderer)renderer).OpenXrFrameLoop.ShouldDeferOpenXrRuntimeSessionStart(out reason);

    public void ExecuteRuntimeGraphicsTransition(
        AbstractRenderer renderer,
        string operation,
        System.Action action)
        => ((VulkanRenderer)renderer).OpenXrFrameLoop.ExecuteOpenXrRuntimeGraphicsTransition(operation, action);

    public bool TryGetRendererOwnedInstance(
        AbstractRenderer renderer,
        out XR? rendererOwnedApi,
        out Instance rendererOwnedInstance,
        out string[] rendererOwnedExtensions)
    {
        bool success = ((VulkanRenderer)renderer).DeviceContext.TryGetOpenXrBootstrapInstance(
            out XR api,
            out rendererOwnedInstance,
            out rendererOwnedExtensions);
        rendererOwnedApi = success ? api : null;
        return success;
    }

    public bool InvalidateRendererOwnedInstance(AbstractRenderer renderer, string reason)
        => ((VulkanRenderer)renderer).DeviceContext.InvalidateOpenXrBootstrapInstance(reason);

    public bool UsesOpenXrVulkanEnable2Creation(AbstractRenderer renderer)
        => ((VulkanRenderer)renderer).DeviceContext.InstanceCreatedThroughOpenXr &&
           ((VulkanRenderer)renderer).DeviceContext.CreatedThroughOpenXr;

    public void ResetRenderingResourcesForRuntimeRecreate(AbstractRenderer renderer, string reason)
        => ((VulkanRenderer)renderer).OpenXrFrameLoop.ResetOpenXrRenderingResourcesForRuntimeRecreate(reason);

    public bool SupportsVulkanFragmentShadingRate(AbstractRenderer renderer)
        => ((VulkanRenderer)renderer).SupportsVulkanFragmentShadingRate;

    public bool SupportsVulkanFragmentDensityMap(AbstractRenderer renderer)
        => ((VulkanRenderer)renderer).SupportsVulkanFragmentDensityMap;

    bool IXrGraphicsBinding.CanUseTrueSinglePassStereo
        => CanUseTrueSinglePassStereo;

    public bool TryResolveViewRenderMode(
        OpenXRAPI api,
        out VrViewRenderModeResolution resolution)
    {
        Attach(api);
        return TryResolveOpenXrViewRenderModeForCurrentBackend(out resolution);
    }

    public bool TryCreateSession(OpenXRAPI api, AbstractRenderer renderer)
    {
        Attach(api);
        CreateVulkanSession();
        return true;
    }

    public void CreateSwapchains(OpenXRAPI api, AbstractRenderer renderer)
    {
        Attach(api);
        InitializeVulkanSwapchains((VulkanRenderer)renderer);
    }

    public unsafe void CleanupSwapchains(OpenXRAPI api)
    {
        Attach(api);
        for (int i = 0; i < _swapchainImagesVK.Length; i++)
        {
            if (_swapchainImagesVK[i] is null)
                continue;

            System.Runtime.InteropServices.Marshal.FreeHGlobal(
                (nint)_swapchainImagesVK[i]);
            _swapchainImagesVK[i] = null;
        }
    }

    public void WaitForGpuIdle(OpenXRAPI api, AbstractRenderer renderer)
    {
        VulkanRenderer vulkanRenderer = (VulkanRenderer)renderer;
        if (!vulkanRenderer.IsDeviceLost)
            vulkanRenderer.DeviceWaitIdle();
    }

    public void AcquireSwapchainImage(OpenXRAPI api, Swapchain swapchain, out uint imageIndex)
    {
        SwapchainImageAcquireInfo acquireInfo = new()
        {
            Type = StructureType.SwapchainImageAcquireInfo
        };
        imageIndex = 0;
        api.Api.AcquireSwapchainImage(swapchain, in acquireInfo, ref imageIndex);
    }

    public void WaitSwapchainImage(OpenXRAPI api, Swapchain swapchain, long timeoutNs)
    {
        SwapchainImageWaitInfo waitInfo = new()
        {
            Type = StructureType.SwapchainImageWaitInfo,
            Timeout = timeoutNs
        };
        api.Api.WaitSwapchainImage(swapchain, in waitInfo);
    }

    public void ReleaseSwapchainImage(OpenXRAPI api, Swapchain swapchain)
    {
        SwapchainImageReleaseInfo releaseInfo = new()
        {
            Type = StructureType.SwapchainImageReleaseInfo
        };
        api.Api.ReleaseSwapchainImage(swapchain, in releaseInfo);
    }

    public void RenderViews(
        OpenXRAPI api,
        in CompositionLayerProjectionView projectionView,
        uint viewIndex)
    {
        // Rendering remains coordinated by the backend-neutral frame lifecycle.
    }

    public unsafe bool TryRenderViewsBatch(
        OpenXRAPI api,
        nint projectionViews,
        out bool handled)
    {
        Attach(api);
        return TryRenderVulkanEyesBatch(
            (CompositionLayerProjectionView*)projectionViews,
            out handled);
    }

    public bool TryRenderEye(
        OpenXRAPI api,
        uint viewIndex,
        uint imageIndex,
        OpenXRAPI.DelRenderToFBO? renderCallback)
    {
        Attach(api);
        return TryRenderVulkanEye(viewIndex, imageIndex);
    }

    public bool ShouldPrewarmEyeResources(OpenXRAPI api, uint viewIndex)
    {
        Attach(api);
        return ShouldPrewarmVulkanEyeResources(viewIndex);
    }

    public void PrewarmEyeResources(OpenXRAPI api, uint viewIndex)
    {
        Attach(api);
        PrewarmVulkanEyeResources(viewIndex);
    }

    public bool TryRenderDesktopMirrorComposition(
        OpenXRAPI api,
        uint targetWidth,
        uint targetHeight)
    {
        Attach(api);
        return TryRenderVulkanDesktopMirrorComposition(
            (VulkanRenderer)api.Window!.Renderer!,
            targetWidth,
            targetHeight);
    }

    public void EnsureStereoViewport(OpenXRAPI api, uint width, uint height)
    {
        Attach(api);
        EnsureOpenXrStereoViewport(width, height);
    }

    public void ResetBackendDiagnostics(OpenXRAPI api)
    {
        Attach(api);
        ResetStrictSpsBoundaryCaptureDiagnostics();
    }

    public void DestroyBackendResources(OpenXRAPI api)
    {
        Attach(api);
        DestroyVulkanEyeMirrorTargets();
        DestroyVulkanStereoRenderTarget();
        DestroyOpenXrPreviewTargets();
        DestroyViewportMirrorTargets();
    }
}
