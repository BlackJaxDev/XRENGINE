using System;
using System.Diagnostics;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        internal EDesktopFrameFlow RunDesktopFramePreflight(ref VulkanFrameAttempt attempt)
        {
            attempt.InteractiveResize = DesktopWsiOutput.IsInteractiveResizeInProgress;

            var liveFramebufferSize = DesktopWsiOutput.EffectiveFramebufferSize;
            var liveWindowSize = DesktopWsiOutput.Window.Window.Size;
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
                attempt.LiveSurfaceWidth == OutputRuntime.Desktop.Extent.Width &&
                attempt.LiveSurfaceHeight == OutputRuntime.Desktop.Extent.Height;
            attempt.CanPresentMismatchedSwapchainExtent =
                attempt.LiveSurfaceValid &&
                !attempt.SurfaceMatchesSwapchain &&
                CanPresentMismatchedSwapchainExtent(
                    attempt.LiveSurfaceWidth,
                    attempt.LiveSurfaceHeight,
                    OutputRuntime.Desktop.Extent.Width,
                    OutputRuntime.Desktop.Extent.Height);

            ApplyDesktopSwapchainExtentPolicy(ref attempt);

            if (!attempt.LiveSurfaceValid)
            {
                return StopDesktopFrameForPreflightStatus(
                    ref attempt,
                    EVulkanDesktopPreflightStatus.ZeroSurface,
                    "Live surface size is zero");
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

                    ServiceDesktopSwapchainRecreatePolicy(ref attempt);

            if (_frameBufferInvalidated ||
                (!attempt.SurfaceMatchesSwapchain &&
                 !attempt.CanPresentMismatchedSwapchainExtent))
            {
                string reason =
                    $"Swapchain resize/recreate pending. Pending={_outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth}x{_outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight} " +
                    $"Live={attempt.LiveSurfaceWidth}x{attempt.LiveSurfaceHeight} " +
                    $"Swapchain={OutputRuntime.Desktop.Extent.Width}x{OutputRuntime.Desktop.Extent.Height}";
                return StopDesktopFrameForPreflightStatus(
                    ref attempt,
                    EVulkanDesktopPreflightStatus.ResizePending,
                    reason);
            }

            bool frameGenerationProxyRequired = _outputRuntime._streamlineFrameGenerationProvisioned;
            bool frameGenerationProxyIncludesDlss =
                frameGenerationProxyRequired && _outputRuntime._streamlineDlssProvisioned;
            if (OutputRuntime.Desktop.StreamlineFrameGenerationActive != frameGenerationProxyRequired ||
                (OutputRuntime.Desktop.StreamlineFrameGenerationActive &&
                 OutputRuntime.Desktop.StreamlineFrameGenerationIncludesDlss != frameGenerationProxyIncludesDlss))
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
            ref VulkanFrameAttempt attempt,
            EVulkanDesktopPreflightStatus status,
            string detail)
        {
            VulkanDesktopPreflightOutcome outcome =
                DesktopWsiOutput.ClassifyPreflight(status);
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
            ref VulkanFrameAttempt attempt,
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
