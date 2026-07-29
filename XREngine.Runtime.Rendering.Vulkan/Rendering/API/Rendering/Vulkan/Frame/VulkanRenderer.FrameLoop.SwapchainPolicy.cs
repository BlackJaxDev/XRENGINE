using System;
using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private static readonly TimeSpan SwapchainRecreateDebounce =
            TimeSpan.FromMilliseconds(16);
        private static readonly TimeSpan SwapchainResizeSettleDelay =
            TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan InteractiveSwapchainRecreateMinInterval =
            TimeSpan.FromMilliseconds(16);

        private long _swapchainRecreateRequestedAt;
        private long _swapchainResizeLastChangedAt;
        private uint _pendingSurfaceWidth;
        private uint _pendingSurfaceHeight;
        private long _lastInteractiveSwapchainRecreateTimestamp;

        private void ScheduleSwapchainRecreate(string reason)
        {
            long now = Stopwatch.GetTimestamp();
            bool wasInvalidated = _frameBufferInvalidated;
            _frameBufferInvalidated = true;

            if (!wasInvalidated || _swapchainRecreateRequestedAt == 0)
                _swapchainRecreateRequestedAt = now;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateScheduled",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Scheduled debounced swapchain recreate. Reason={0} RequestedAtTicks={1} WasInvalidated={2}",
                reason,
                _swapchainRecreateRequestedAt,
                wasInvalidated);
        }

        private bool TryRecreateSwapchainNow(string reason)
        {
            long recreateStart = Stopwatch.GetTimestamp();
            uint previousWidth = swapChainExtent.Width;
            uint previousHeight = swapChainExtent.Height;
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateImmediate",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Recreating swapchain immediately. Reason={0}",
                reason);

            if (!RecreateSwapChain())
            {
                TimeSpan failedElapsed = Stopwatch.GetElapsedTime(recreateStart);
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecreateResult",
                    TimeSpan.FromMilliseconds(500),
                    "[Vulkan] Swapchain recreate deferred/failed. Reason={0} ElapsedMs={1:F3} Previous={2}x{3} Current={4}x{5}",
                    reason,
                    failedElapsed.TotalMilliseconds,
                    previousWidth,
                    previousHeight,
                    swapChainExtent.Width,
                    swapChainExtent.Height);
                ScheduleSwapchainRecreate($"{reason}; surface not presentable yet");
                return false;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(recreateStart);
            _frameBufferInvalidated = false;
            _swapchainRecreateRequestedAt = 0;
            _swapchainResizeLastChangedAt = 0;
            _pendingSurfaceWidth = 0;
            _pendingSurfaceHeight = 0;
            ResetImGuiFrameMarker();

            var liveFramebufferSize = XRWindow.EffectiveFramebufferSize;
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateResult",
                TimeSpan.FromMilliseconds(500),
                "[Vulkan] Swapchain recreate completed. Reason={0} ElapsedMs={1:F3} Previous={2}x{3} Current={4}x{5} Live={6}x{7} Divergence={8}x{9}",
                reason,
                elapsed.TotalMilliseconds,
                previousWidth,
                previousHeight,
                swapChainExtent.Width,
                swapChainExtent.Height,
                liveFramebufferSize.X,
                liveFramebufferSize.Y,
                (int)liveFramebufferSize.X - (int)swapChainExtent.Width,
                (int)liveFramebufferSize.Y - (int)swapChainExtent.Height);
            return true;
        }

        private void TrackPendingDesktopSurfaceSize(
            ref VulkanFrameAttempt attempt)
        {
            if (attempt.LiveSurfaceValid)
            {
                if (_pendingSurfaceWidth != attempt.LiveSurfaceWidth ||
                    _pendingSurfaceHeight != attempt.LiveSurfaceHeight)
                {
                    _pendingSurfaceWidth = attempt.LiveSurfaceWidth;
                    _pendingSurfaceHeight = attempt.LiveSurfaceHeight;
                    _swapchainResizeLastChangedAt =
                        Stopwatch.GetTimestamp();
                }

                return;
            }

            _pendingSurfaceWidth = 0;
            _pendingSurfaceHeight = 0;
            _swapchainResizeLastChangedAt = 0;
        }

        private void ApplyDesktopSwapchainExtentPolicy(
            ref VulkanFrameAttempt attempt)
        {
            if (attempt.LiveSurfaceValid &&
                !attempt.SurfaceMatchesSwapchain)
            {
                if (attempt.InteractiveResize &&
                    attempt.CanPresentMismatchedSwapchainExtent)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.PresentScaledInteractiveResize",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Presenting through validated WSI scaling during interactive resize. LiveSurface={0}x{1} Swapchain={2}x{3}.",
                        attempt.LiveSurfaceWidth,
                        attempt.LiveSurfaceHeight,
                        swapChainExtent.Width,
                        swapChainExtent.Height);
                }
                else
                {
                    ScheduleSwapchainRecreate(
                        attempt.InteractiveResize
                            ? "Interactive resize surface/swapchain size mismatch"
                            : "Surface/swapchain size mismatch");
                }

                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.SizeMismatch",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Detected surface/swapchain size mismatch: WindowFB={0}x{1} Window={2}x{3} LiveSurface={4}x{5} Swapchain={6}x{7}. Interactive={8} PresentScaling={9}.",
                    attempt.LiveFramebufferWidth,
                    attempt.LiveFramebufferHeight,
                    attempt.LiveWindowWidth,
                    attempt.LiveWindowHeight,
                    attempt.LiveSurfaceWidth,
                    attempt.LiveSurfaceHeight,
                    swapChainExtent.Width,
                    swapChainExtent.Height,
                    attempt.InteractiveResize,
                    attempt.CanPresentMismatchedSwapchainExtent);
                return;
            }

            if (_pendingSurfaceWidth == swapChainExtent.Width &&
                _pendingSurfaceHeight == swapChainExtent.Height)
            {
                _pendingSurfaceWidth = 0;
                _pendingSurfaceHeight = 0;
                _swapchainResizeLastChangedAt = 0;
            }
        }

        private void ServiceDesktopSwapchainRecreatePolicy(
            ref VulkanFrameAttempt attempt)
        {
            if (!ShouldRunSwapchainRecreate(
                    attempt.InteractiveResize))
            {
                return;
            }

            bool hasPendingSurfaceSize =
                _pendingSurfaceWidth > 0 &&
                _pendingSurfaceHeight > 0;
            bool pendingMatchesLive =
                !hasPendingSurfaceSize ||
                (_pendingSurfaceWidth == attempt.LiveSurfaceWidth &&
                 _pendingSurfaceHeight == attempt.LiveSurfaceHeight);
            bool resizeSettled =
                !hasPendingSurfaceSize ||
                (_swapchainResizeLastChangedAt != 0 &&
                 Stopwatch.GetElapsedTime(
                     _swapchainResizeLastChangedAt) >=
                 SwapchainResizeSettleDelay);

            if (attempt.InteractiveResize)
            {
                if (pendingMatchesLive &&
                    ShouldRunInteractiveSwapchainRecreate())
                {
                    TryRecreateSwapchainNow(
                        "Interactive resize presentation extent");
                    _lastInteractiveSwapchainRecreateTimestamp =
                        Stopwatch.GetTimestamp();
                    UpdateAttemptSwapchainExtentMatch(ref attempt);
                    return;
                }

                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForInteractiveResize",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Deferring interactive swapchain recreate. Pending={0}x{1} Live={2}x{3} Swapchain={4}x{5} PendingMatchesLive={6}",
                    _pendingSurfaceWidth,
                    _pendingSurfaceHeight,
                    attempt.LiveSurfaceWidth,
                    attempt.LiveSurfaceHeight,
                    swapChainExtent.Width,
                    swapChainExtent.Height,
                    pendingMatchesLive);
                return;
            }

            if (pendingMatchesLive && resizeSettled)
            {
                _lastInteractiveSwapchainRecreateTimestamp = 0;
                TryRecreateSwapchainNow(
                    "Debounce elapsed before frame acquire (resize settled)");
                UpdateAttemptSwapchainExtentMatch(ref attempt);
                return;
            }

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForResizeSettle",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Debounce elapsed but resize is still active. Deferring swapchain recreate. Pending={0}x{1} Live={2}x{3} Settled={4}",
                _pendingSurfaceWidth,
                _pendingSurfaceHeight,
                attempt.LiveSurfaceWidth,
                attempt.LiveSurfaceHeight,
                resizeSettled);
        }

        private void UpdateAttemptSwapchainExtentMatch(
            ref VulkanFrameAttempt attempt)
        {
            attempt.SurfaceMatchesSwapchain =
                attempt.LiveSurfaceValid &&
                attempt.LiveSurfaceWidth == swapChainExtent.Width &&
                attempt.LiveSurfaceHeight == swapChainExtent.Height;
        }

        private bool ShouldRunSwapchainRecreate(bool interactiveResize)
        {
            if (!_frameBufferInvalidated)
                return false;

            if (interactiveResize)
                return true;

            if (_swapchainRecreateRequestedAt == 0)
                return true;

            return Stopwatch.GetElapsedTime(_swapchainRecreateRequestedAt) >= SwapchainRecreateDebounce;
        }

        private bool ShouldRunInteractiveSwapchainRecreate()
        {
            long last = _lastInteractiveSwapchainRecreateTimestamp;
            return last == 0 ||
                Stopwatch.GetElapsedTime(last) >= InteractiveSwapchainRecreateMinInterval;
        }

        private bool CanPresentMismatchedSwapchainExtent(
            uint liveSurfaceWidth,
            uint liveSurfaceHeight,
            uint swapchainWidth,
            uint swapchainHeight)
        {
            if (liveSurfaceWidth == 0 ||
                liveSurfaceHeight == 0 ||
                swapchainWidth == 0 ||
                swapchainHeight == 0)
            {
                return false;
            }

            return IsSwapchainPresentScalingExtentSupported(
                swapchainWidth,
                swapchainHeight);
        }

        private bool ShouldKeepPresentScalingSwapchain(Result result, bool interactiveResize)
            => result == Result.SuboptimalKhr &&
                interactiveResize &&
                _swapchainPresentScalingActive;

    }
}
