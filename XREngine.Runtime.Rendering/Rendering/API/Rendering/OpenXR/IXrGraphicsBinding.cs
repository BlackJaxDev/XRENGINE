namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Renderer-specific graphics resources for one OpenXR runtime host.</summary>
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
    ulong PreviewLeftEyeFrameId => 0;
    ulong PreviewRightEyeFrameId => 0;
    XRTexture2D? DesktopMirrorTexture => null;
    OpenXrSmokeCaptureLedgerEntry[] GetStrictSpsBoundaryCaptureLedger() => [];
    bool RequiresRuntimeStateRenderThread(RuntimeOpenXrState runtimeState, bool runtimeLossPending) => false;
    bool ShouldDeferSessionStart(AbstractRenderer renderer, out string reason)
    {
        reason = string.Empty;
        return false;
    }

    void ExecuteRuntimeGraphicsTransition(AbstractRenderer renderer, string operation, Action action)
        => action();

    bool TryGetRendererOwnedInstance(AbstractRenderer renderer, out IOpenXrVulkanBootstrapLease? lease)
    {
        lease = null;
        return false;
    }

    bool InvalidateRendererOwnedInstance(AbstractRenderer renderer, string reason) => false;
    OpenXrDeviceLossBindingAbandonment AbandonAfterDeviceLoss(
        IOpenXrGraphicsHost host, AbstractRenderer renderer, string reason) => default;
    int TryDestroyRendererOwnedInstanceAfterDeviceLoss(AbstractRenderer renderer, string reason)
        => OpenXrResultCodes.ErrorFunctionUnsupported;
    bool UsesOpenXrVulkanEnable2Creation(AbstractRenderer renderer) => false;
    void ResetRenderingResourcesForRuntimeRecreate(AbstractRenderer renderer, string reason) { }
    bool SupportsVulkanFragmentShadingRate(AbstractRenderer renderer) => false;
    bool SupportsVulkanFragmentDensityMap(AbstractRenderer renderer) => false;
    bool CanUseTrueSinglePassStereo => false;
    bool TryResolveViewRenderMode(IOpenXrGraphicsHost host, out VrViewRenderModeResolution resolution)
    {
        resolution = default;
        return false;
    }

    bool TryRenderViewsBatch(IOpenXrGraphicsHost host, out bool handled)
    {
        handled = false;
        return false;
    }

    bool TryRenderEye(IOpenXrGraphicsHost host, uint viewIndex, uint imageIndex, OpenXrRenderToEyeCallback? renderCallback)
        => false;
    bool ShouldPrewarmEyeResources(IOpenXrGraphicsHost host, uint viewIndex) => false;
    void PrewarmEyeResources(IOpenXrGraphicsHost host, uint viewIndex) { }
    void Flush(IOpenXrGraphicsHost host) { }
    void CaptureRenderCallbackState(IOpenXrGraphicsHost host) { }
    void RestoreRenderCallbackState(IOpenXrGraphicsHost host) { }
    bool TryRenderDesktopMirrorComposition(IOpenXrGraphicsHost host, uint targetWidth, uint targetHeight)
        => false;
    void EnsureStereoViewport(IOpenXrGraphicsHost host, uint width, uint height) { }
    void ResetBackendDiagnostics(IOpenXrGraphicsHost host) { }
    void DestroyBackendResources(IOpenXrGraphicsHost host) { }

    bool TryCreateSession(IOpenXrGraphicsHost host, AbstractRenderer renderer);
    void CreateSwapchains(IOpenXrGraphicsHost host, AbstractRenderer renderer);
    void CleanupSwapchains(IOpenXrGraphicsHost host);
    OpenXrSwapchainRetirementOutcome RetireSwapchainsForDeferredDestruction(
        IOpenXrGraphicsHost host, AbstractRenderer renderer)
        => OpenXrSwapchainRetirementOutcome.Unsupported;
    OpenXrSwapchainRetirementSnapshot CaptureSwapchainRetirementSnapshot() => new();
    void PollDeferredSwapchainRetirement(IOpenXrGraphicsHost host, AbstractRenderer renderer) { }
    bool RequiresDeferredSwapchainRetirement => false;
    bool HasPendingDeferredSwapchainRetirement => false;
    bool HasPendingOpenXrSubmissionOwnership => false;
    int PendingOpenXrSubmissionCount => 0;
    string? PendingOpenXrSubmissionReceiptSource => null;
    bool WaitForGpuIdle(IOpenXrGraphicsHost host, AbstractRenderer renderer);
    int BeginFrame(IOpenXrGraphicsHost host) => host.GraphicsCalls.BeginFrame();
    int AcquireSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain, out uint imageIndex)
        => host.GraphicsCalls.AcquireSwapchainImage(swapchain, out imageIndex);
    int WaitSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain, long timeoutNs)
        => host.GraphicsCalls.WaitSwapchainImage(swapchain, timeoutNs);
    int ReleaseSwapchainImage(IOpenXrGraphicsHost host, ulong swapchain)
        => host.GraphicsCalls.ReleaseSwapchainImage(swapchain);
    int EndFrame(IOpenXrGraphicsHost host, bool submitLayer)
        => host.GraphicsCalls.EndFrame(submitLayer);
    void RenderViews(IOpenXrGraphicsHost host, uint viewIndex) { }
}
