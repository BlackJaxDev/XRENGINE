using Silk.NET.OpenXR;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Implements the graphics-API-specific portion of an OpenXR session.
/// Concrete renderer assemblies register an implementation explicitly at composition time.
/// </summary>
public interface IXrGraphicsBinding
{
    RendererBackendId BackendId { get; }
    string BackendName { get; }

    bool IsCompatible(AbstractRenderer renderer);

    bool RequiresDeferredSessionCreation => false;

    bool DestroysRuntimeInstanceOnRendererTeardown => false;

    bool RequiresRenderThreadForTeardown => false;

    XRTexture2D? PreviewLeftEyeTexture => null;

    XRTexture2D? PreviewRightEyeTexture => null;

    XRTexture2D? DesktopMirrorTexture => null;

    OpenXrSmokeCaptureLedgerEntry[] GetStrictSpsBoundaryCaptureLedger()
        => [];

    bool RequiresRuntimeStateRenderThread(
        OpenXRAPI.OpenXrRuntimeState runtimeState,
        bool runtimeLossPending)
        => false;

    bool ShouldDeferSessionStart(AbstractRenderer renderer, out string reason)
    {
        reason = string.Empty;
        return false;
    }

    void ExecuteRuntimeGraphicsTransition(
        AbstractRenderer renderer,
        string operation,
        System.Action action)
        => action();

    bool TryGetRendererOwnedInstance(
        AbstractRenderer renderer,
        out XR? rendererOwnedApi,
        out Instance rendererOwnedInstance,
        out string[] rendererOwnedExtensions)
    {
        rendererOwnedApi = null;
        rendererOwnedInstance = default;
        rendererOwnedExtensions = [];
        return false;
    }

    bool InvalidateRendererOwnedInstance(AbstractRenderer renderer, string reason)
        => false;

    bool UsesOpenXrVulkanEnable2Creation(AbstractRenderer renderer)
        => false;

    void ResetRenderingResourcesForRuntimeRecreate(AbstractRenderer renderer, string reason)
    {
    }

    bool SupportsVulkanFragmentShadingRate(AbstractRenderer renderer)
        => false;

    bool SupportsVulkanFragmentDensityMap(AbstractRenderer renderer)
        => false;

    bool CanUseTrueSinglePassStereo => false;

    bool TryResolveViewRenderMode(
        OpenXRAPI api,
        out VrViewRenderModeResolution resolution)
    {
        resolution = default;
        return false;
    }

    bool TryRenderViewsBatch(
        OpenXRAPI api,
        nint projectionViews,
        out bool handled)
    {
        handled = false;
        return false;
    }

    bool TryRenderEye(
        OpenXRAPI api,
        uint viewIndex,
        uint imageIndex,
        OpenXRAPI.DelRenderToFBO? renderCallback)
        => false;

    bool ShouldPrewarmEyeResources(OpenXRAPI api, uint viewIndex)
        => false;

    void PrewarmEyeResources(OpenXRAPI api, uint viewIndex)
    {
    }

    void Flush(OpenXRAPI api)
    {
    }

    void CaptureRenderCallbackState(OpenXRAPI api)
    {
    }

    void RestoreRenderCallbackState(OpenXRAPI api)
    {
    }

    bool TryRenderDesktopMirrorComposition(
        OpenXRAPI api,
        uint targetWidth,
        uint targetHeight)
        => false;

    void EnsureStereoViewport(OpenXRAPI api, uint width, uint height)
    {
    }

    void ResetBackendDiagnostics(OpenXRAPI api)
    {
    }

    void DestroyBackendResources(OpenXRAPI api)
    {
    }

    bool TryCreateSession(OpenXRAPI api, AbstractRenderer renderer);
    void CreateSwapchains(OpenXRAPI api, AbstractRenderer renderer);
    void CleanupSwapchains(OpenXRAPI api);
    void WaitForGpuIdle(OpenXRAPI api, AbstractRenderer renderer);
    void AcquireSwapchainImage(OpenXRAPI api, Swapchain swapchain, out uint imageIndex);
    void WaitSwapchainImage(OpenXRAPI api, Swapchain swapchain, long timeoutNs);
    void ReleaseSwapchainImage(OpenXRAPI api, Swapchain swapchain);
    void RenderViews(OpenXRAPI api, in CompositionLayerProjectionView projectionView, uint viewIndex);
}
