using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private EDesktopFrameFlow RunDesktopFramePreflight(ref DesktopFrameAttempt attempt)
        {
            attempt.InteractiveResize = XRWindow.IsInteractiveResizeInProgress;

            var liveFramebufferSize = XRWindow.EffectiveFramebufferSize;
            var liveWindowSize = Window.Size;
            attempt.LiveFramebufferWidth = liveFramebufferSize.X;
            attempt.LiveFramebufferHeight = liveFramebufferSize.Y;
            attempt.LiveWindowWidth = liveWindowSize.X;
            attempt.LiveWindowHeight = liveWindowSize.Y;
            attempt.LiveSurfaceWidth = liveFramebufferSize.X > 0
                ? (uint)liveFramebufferSize.X
                : (uint)Math.Max(liveWindowSize.X, 0);
            attempt.LiveSurfaceHeight = liveFramebufferSize.Y > 0
                ? (uint)liveFramebufferSize.Y
                : (uint)Math.Max(liveWindowSize.Y, 0);

            attempt.LiveSurfaceValid =
                attempt.LiveSurfaceWidth > 0 &&
                attempt.LiveSurfaceHeight > 0;
            TrackPendingDesktopSurfaceSize(ref attempt);

            attempt.SurfaceMatchesSwapchain =
                attempt.LiveSurfaceValid &&
                attempt.LiveSurfaceWidth == swapChainExtent.Width &&
                attempt.LiveSurfaceHeight == swapChainExtent.Height;
            attempt.CanPresentMismatchedSwapchainExtent =
                attempt.LiveSurfaceValid &&
                !attempt.SurfaceMatchesSwapchain &&
                CanPresentMismatchedSwapchainExtent(
                    attempt.LiveSurfaceWidth,
                    attempt.LiveSurfaceHeight,
                    swapChainExtent.Width,
                    swapChainExtent.Height);

            ApplyDesktopSwapchainExtentPolicy(ref attempt);
            ServiceDesktopSwapchainRecreatePolicy(ref attempt);

            if (!attempt.LiveSurfaceValid)
            {
                return StopDesktopFrameForPreflightStatus(
                    ref attempt,
                    EVulkanDesktopPreflightStatus.ZeroSurface,
                    "Live surface size is zero");
            }

            if (_frameBufferInvalidated ||
                (!attempt.SurfaceMatchesSwapchain &&
                 !attempt.CanPresentMismatchedSwapchainExtent))
            {
                string reason =
                    $"Swapchain resize/recreate pending. Pending={_pendingSurfaceWidth}x{_pendingSurfaceHeight} " +
                    $"Live={attempt.LiveSurfaceWidth}x{attempt.LiveSurfaceHeight} " +
                    $"Swapchain={swapChainExtent.Width}x{swapChainExtent.Height}";
                return StopDesktopFrameForPreflightStatus(
                    ref attempt,
                    EVulkanDesktopPreflightStatus.ResizePending,
                    reason);
            }

            if (TryGetViewportResourceBlocker(
                    attempt.InteractiveResize,
                    out string resourceMismatchReason))
            {
                return StopDesktopFrameForPreflightStatus(
                    ref attempt,
                    EVulkanDesktopPreflightStatus.ResourceMismatch,
                    resourceMismatchReason);
            }

            bool frameGenerationProxyRequired = _streamlineFrameGenerationProvisioned;
            bool frameGenerationProxyIncludesDlss =
                frameGenerationProxyRequired && _streamlineDlssProvisioned;
            if (_streamlineFrameGenerationSwapchainActive != frameGenerationProxyRequired ||
                (_streamlineFrameGenerationSwapchainActive &&
                 _streamlineFrameGenerationSwapchainIncludesDlss != frameGenerationProxyIncludesDlss))
            {
                TryRecreateSwapchainNow(
                    frameGenerationProxyRequired
                        ? frameGenerationProxyIncludesDlss
                            ? "NVIDIA DLSS/DLSS-G capability provisioned; recreating Streamline swapchain with DLSS + DLSS-G"
                            : "NVIDIA DLSS-G capability provisioned; recreating swapchain through Streamline"
                        : "NVIDIA DLSS-G capability unavailable; recreating swapchain without Streamline");
                attempt.Stop(EDesktopFrameReason.FrameGenerationModeChanged);
                return EDesktopFrameFlow.Stop;
            }

            attempt.AdvanceTo(EDesktopFramePhase.PreflightComplete);
            return EDesktopFrameFlow.Continue;
        }

        private EDesktopFrameFlow StopDesktopFrameForPreflightStatus(
            ref DesktopFrameAttempt attempt,
            EVulkanDesktopPreflightStatus status,
            string detail)
        {
            VulkanDesktopPreflightOutcome outcome =
                VulkanDesktopFramePolicy.ClassifyPreflight(status);
            EDesktopFrameReason reason = outcome.Reason switch
            {
                EVulkanDesktopPolicyReason.ZeroSurface =>
                    EDesktopFrameReason.ZeroSurface,
                EVulkanDesktopPolicyReason.ResizePending =>
                    EDesktopFrameReason.ResizePending,
                EVulkanDesktopPolicyReason.ResourceMismatch =>
                    EDesktopFrameReason.ResourceGenerationBlocked,
                EVulkanDesktopPolicyReason.InteractiveSlotBusy =>
                    EDesktopFrameReason.FrameSlotBusy,
                _ => throw new InvalidOperationException(
                    $"Unsupported desktop preflight stop policy {outcome.Reason}."),
            };

            SkipDesktopFrameBeforeAcquire(
                ref attempt,
                reason,
                detail);
            return EDesktopFrameFlow.Stop;
        }

        private void SkipDesktopFrameBeforeAcquire(
            ref DesktopFrameAttempt attempt,
            EDesktopFrameReason reason,
            string detail)
        {
            _ = TryWaitCurrentFrameSlotAndDrainRetiredResources(
                attempt.FrameSlot,
                attempt.InteractiveResize,
                detail);
            DrainSkippedResizeFrameOps(detail);
            MarkSkippedResizeFrameObserved(attempt.StartTimestamp);
            attempt.Stop(reason);
        }
    }
}
