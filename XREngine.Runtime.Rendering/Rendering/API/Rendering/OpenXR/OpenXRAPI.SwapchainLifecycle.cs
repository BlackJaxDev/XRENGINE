using Silk.NET.OpenXR;
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
    private OpenXrSwapchainReplacementOutcome TryReplaceSwapchainsInSession(string reason)
    {
        if (_session.Handle == 0 || Window?.Renderer is not AbstractRenderer renderer ||
            _graphicsBinding is null || !HasCreatedOpenXrSwapchains())
            return OpenXrSwapchainReplacementOutcome.DeferredBeforeDetachment;

        // Resolution changes are scheduled on the render thread. Refuse to
        // mutate images while a begun frame may still consume their acquire
        // state; the normal runtime retry path will attempt again later.
        if (!CanReplaceOpenXrSwapchainsInSession())
            return OpenXrSwapchainReplacementOutcome.DeferredBeforeDetachment;

        StopOpenXrPacingThread();
        if (_openXrPacingThread?.IsAlive == true)
            return OpenXrSwapchainReplacementOutcome.DeferredBeforeDetachment;

        bool sessionWasBegun = _sessionBegun;
        bool cleanupCompleted;
        try
        {
            cleanupCompleted = CleanupSwapchains();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OpenXR] Swapchain cleanup failed during in-session replacement: {ex.Message}");
            return OpenXrSwapchainReplacementOutcome.FailedAfterDetachment;
        }

        if (!cleanupCompleted || HasCreatedOpenXrSwapchains())
        {
            // A cleanup failure may have detached some child state before it
            // reported failure. Only checks before CleanupSwapchains are safe
            // deferrals; every result after that mutation boundary recovers.
            return OpenXrSwapchainReplacementOutcome.FailedAfterDetachment;
        }

        try
        {
            _graphicsBinding.ResetRenderingResourcesForRuntimeRecreate(renderer, reason);
            _graphicsBinding.CreateSwapchains(this, renderer);
            if (!HasCompleteOpenXrSwapchainSet())
                return OpenXrSwapchainReplacementOutcome.FailedAfterDetachment;

            _sessionBegun = sessionWasBegun;
            return OpenXrSwapchainReplacementOutcome.Replaced;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OpenXR] In-session swapchain replacement failed: {ex.Message}");
            return OpenXrSwapchainReplacementOutcome.FailedAfterDetachment;
        }
    }

    /// <summary>
    /// Clears any partial replacement children and moves the runtime into the
    /// existing session-teardown path. The parent remains alive until deferred
    /// children retire, after which normal probing recreates with the still
    /// requested eye-resolution settings.
    /// </summary>
    private void RecoverFromDetachedSwapchainReplacementFailure(string reason)
    {
        try
        {
            if (!CleanupSwapchains() || HasCreatedOpenXrSwapchains())
            {
                Debug.LogWarning(
                    $"[OpenXR] Swapchain replacement detached the active generation but child cleanup remains pending. " +
                    $"Retaining the session parent for teardown recovery. Reason={reason}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[OpenXR] Partial swapchain cleanup failed after replacement detachment; retaining parents for retry. " +
                $"Reason={reason}; Error={ex.Message}");
        }

        _sessionBegun = false;
        _sessionState = SessionState.Unknown;
        SetRuntimeState(OpenXrRuntimeState.SessionStopping);
    }

    /// <summary>
    /// Checks the OpenXR view-to-swapchain contract used by sequential and
    /// true single-pass stereo. SPS still presents one image-backed OpenXR
    /// swapchain per located view; it does not publish an array swapchain.
    /// </summary>
    private bool HasCompleteOpenXrSwapchainSet()
    {
        if (_viewCount == 0)
            return false;

        for (int i = 0; i < _viewCount; i++)
        {
            if (_swapchains[i].Handle == 0 || _swapchainImageCounts[i] == 0)
                return false;
        }

        return true;
    }

    private void RequireCompleteOpenXrSwapchainSet()
    {
        if (!HasCompleteOpenXrSwapchainSet())
        {
            throw new InvalidOperationException(
                "OpenXR swapchain creation completed without a complete image-backed view set.");
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
