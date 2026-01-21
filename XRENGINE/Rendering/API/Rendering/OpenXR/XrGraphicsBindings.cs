using Silk.NET.OpenXR;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.API.Rendering.OpenXR;

internal interface IXrGraphicsBinding
{
    string BackendName { get; }
    bool IsCompatible(AbstractRenderer renderer);
    bool TryCreateSession(OpenXRAPI api, AbstractRenderer renderer);
    void CreateSwapchains(OpenXRAPI api, AbstractRenderer renderer);
    void CleanupSwapchains(OpenXRAPI api);
    void WaitForGpuIdle(OpenXRAPI api, AbstractRenderer renderer);
    void AcquireSwapchainImage(OpenXRAPI api, Swapchain swapchain, out uint imageIndex);
    void WaitSwapchainImage(OpenXRAPI api, Swapchain swapchain, long timeoutNs);
    void ReleaseSwapchainImage(OpenXRAPI api, Swapchain swapchain);
    void RenderViews(OpenXRAPI api, in CompositionLayerProjectionView projectionView, uint viewIndex);
}

internal sealed class VulkanXrGraphicsBinding : IXrGraphicsBinding
{
    public string BackendName => "Vulkan";

    public bool IsCompatible(AbstractRenderer renderer) => renderer is VulkanRenderer;

    public bool TryCreateSession(OpenXRAPI api, AbstractRenderer renderer)
    {
        api.CreateVulkanSession((VulkanRenderer)renderer);
        return true;
    }

    public void CreateSwapchains(OpenXRAPI api, AbstractRenderer renderer)
        => api.InitializeVulkanSwapchains((VulkanRenderer)renderer);

    public void CleanupSwapchains(OpenXRAPI api)
        => api.CleanupSwapchains();

    public void WaitForGpuIdle(OpenXRAPI api, AbstractRenderer renderer)
        => ((VulkanRenderer)renderer).DeviceWaitIdle();

    public void AcquireSwapchainImage(OpenXRAPI api, Swapchain swapchain, out uint imageIndex)
    {
        var acquireInfo = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
        imageIndex = 0;
        api.Api.AcquireSwapchainImage(swapchain, in acquireInfo, ref imageIndex);
    }

    public void WaitSwapchainImage(OpenXRAPI api, Swapchain swapchain, long timeoutNs)
    {
        var waitInfo = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = timeoutNs };
        api.Api.WaitSwapchainImage(swapchain, in waitInfo);
    }

    public void ReleaseSwapchainImage(OpenXRAPI api, Swapchain swapchain)
    {
        var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
        api.Api.ReleaseSwapchainImage(swapchain, in releaseInfo);
    }

    public void RenderViews(OpenXRAPI api, in CompositionLayerProjectionView projectionView, uint viewIndex)
    {
        // Rendering is performed by OpenXRAPI.FrameLifecycle; kept for backend-agnostic API completeness.
    }
}

internal sealed class OpenGLXrGraphicsBinding : IXrGraphicsBinding
{
    public string BackendName => "OpenGL";

    public bool IsCompatible(AbstractRenderer renderer) => renderer is OpenGLRenderer;

    public bool TryCreateSession(OpenXRAPI api, AbstractRenderer renderer)
    {
        api.CreateOpenGLSession((OpenGLRenderer)renderer);
        return true;
    }

    public void CreateSwapchains(OpenXRAPI api, AbstractRenderer renderer)
        => api.InitializeOpenGLSwapchains((OpenGLRenderer)renderer);

    public void CleanupSwapchains(OpenXRAPI api)
        => api.CleanupSwapchains();

    public void WaitForGpuIdle(OpenXRAPI api, AbstractRenderer renderer)
    {
        if (renderer is OpenGLRenderer && api.TryGetGl(out var gl))
            gl.Finish();
    }

    public void AcquireSwapchainImage(OpenXRAPI api, Swapchain swapchain, out uint imageIndex)
    {
        var acquireInfo = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
        imageIndex = 0;
        api.Api.AcquireSwapchainImage(swapchain, in acquireInfo, ref imageIndex);
    }

    public void WaitSwapchainImage(OpenXRAPI api, Swapchain swapchain, long timeoutNs)
    {
        var waitInfo = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = timeoutNs };
        api.Api.WaitSwapchainImage(swapchain, in waitInfo);
    }

    public void ReleaseSwapchainImage(OpenXRAPI api, Swapchain swapchain)
    {
        var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
        api.Api.ReleaseSwapchainImage(swapchain, in releaseInfo);
    }

    public void RenderViews(OpenXRAPI api, in CompositionLayerProjectionView projectionView, uint viewIndex)
    {
        // Rendering is performed by OpenXRAPI.FrameLifecycle; kept for backend-agnostic API completeness.
    }
}
