using System.Threading;
using XREngine.Rendering;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    /// <summary>
    /// Replaces only session-owned swapchain resources. This path deliberately
    /// preserves the OpenXR session and instance so deferred Vulkan swapchain
    /// generations never outlive their parent runtime handles.
    /// </summary>
    private bool TryReplaceSwapchainsInSession(string reason)
    {
        if (_session.Handle == 0 || Window?.Renderer is not AbstractRenderer renderer ||
            _graphicsBinding is null || !HasCreatedOpenXrSwapchains())
            return false;

        // Resolution changes are scheduled on the render thread. Refuse to
        // mutate images while a begun frame may still consume their acquire
        // state; the normal runtime retry path will attempt again later.
        if (!CanReplaceOpenXrSwapchainsInSession())
            return false;

        StopOpenXrPacingThread();
        if (_openXrPacingThread?.IsAlive == true)
            return false;

        bool sessionWasBegun = _sessionBegun;
        if (!CleanupSwapchains() || HasCreatedOpenXrSwapchains())
            return false;

        try
        {
            _graphicsBinding.ResetRenderingResourcesForRuntimeRecreate(renderer, reason);
            _graphicsBinding.CreateSwapchains(this, renderer);
            if (!HasCreatedOpenXrSwapchains())
                return false;

            _sessionBegun = sessionWasBegun;
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OpenXR] In-session swapchain replacement failed: {ex.Message}");
            return false;
        }
    }

    private bool CanReplaceOpenXrSwapchainsInSession()
    {
        return Volatile.Read(ref _pendingXrFrame) == 0 &&
            Volatile.Read(ref _pendingXrFrameCollected) == 0 &&
            Volatile.Read(ref _framePrepared) == 0 &&
            Volatile.Read(ref _openXrCollectVisiblePrepActive) == 0 &&
            Volatile.Read(ref _openXrFramePrepActive) == 0;
    }
}
