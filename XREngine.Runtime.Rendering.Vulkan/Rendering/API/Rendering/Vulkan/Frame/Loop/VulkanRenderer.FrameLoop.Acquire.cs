using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        private const ulong BlockingAcquireTimeoutNanoseconds = ulong.MaxValue;
        private const ulong InteractiveResizeAcquireTimeoutNanoseconds = 0UL;

        internal EDesktopFrameFlow AcquireDesktopSwapchainImageCore(
            ref VulkanFrameAttempt attempt)
        {
            attempt.ImageIndex = 0;
            attempt.AcquireSemaphore =
                _commandRuntime.Synchronization.acquireBridgeSemaphores![attempt.FrameSlot];
            bool xrOwnsFrameDeadline = RuntimeRenderingHostServices.Presentation.IsOpenXRActive;
            ulong acquireTimeoutNanoseconds = attempt.InteractiveResize || xrOwnsFrameDeadline
                ? InteractiveResizeAcquireTimeoutNanoseconds
                : BlockingAcquireTimeoutNanoseconds;

            long stageStartTimestamp = Stopwatch.GetTimestamp();
            attempt.AcquireStartedTimestamp = stageStartTimestamp;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       "Vulkan.FrameLifecycle.AcquireNextImage"))
            {
                ThrowIfDesktopFrameFaultInjected(
                    EVulkanDesktopFrameFaultPoint.Acquire);
                if (OutputRuntime.Desktop.StreamlineFrameGenerationActive)
                {
                    if (!NvidiaDlssManager.Native.TryAcquireProxyNextImage(
                            _outputRuntime.CaptureStreamlineDeviceBinding(
                                _deviceContext),
                            OutputRuntime.Desktop.Swapchain,
                            acquireTimeoutNanoseconds,
                            attempt.AcquireSemaphore,
                            default,
                            ref attempt.ImageIndex,
                            out attempt.AcquireResult,
                            out string failureReason))
                    {
                        if (attempt.AcquireResult is Result.Success or Result.SuboptimalKhr)
                        {
                            VulkanDesktopAcquireOutcome acceptedOutcome =
                                DesktopWsiOutput.ClassifyAcquire(attempt.AcquireResult);
                            attempt.TransitionAcquireOwnership(acceptedOutcome.Ownership);
                        }
                        if (attempt.AcquireResult == Result.ErrorDeviceLost)
                        {
                            attempt.TransitionAcquireOwnership(
                                EVulkanDesktopAcquireOwnership
                                    .IndeterminateAfterDeviceLoss);
                            attempt.Stop(
                                EDesktopFrameReason.AcquireDeviceLost,
                                EDesktopFrameRecoveryAction.DeviceLost);
                            throw CreateDeviceLostException(
                                "Streamline AcquireNextImage",
                                attempt.AcquireResult);
                        }

                        string message =
                            $"NVIDIA DLSS frame generation failed to acquire the swapchain image through Streamline: {failureReason}";
                        Debug.RenderingError(message);
                        throw new InvalidOperationException(message);
                    }
                }
                else
                {
                    attempt.AcquireResult = OutputRuntime.Desktop.SwapchainExtension!.AcquireNextImage(
                        _deviceContext.Device,
                        OutputRuntime.Desktop.Swapchain,
                        acquireTimeoutNanoseconds,
                        attempt.AcquireSemaphore,
                        default,
                        ref attempt.ImageIndex);
                }
            }

            VulkanDesktopAcquireOutcome acquireOutcome =
                DesktopWsiOutput.ClassifyAcquire(attempt.AcquireResult);
            if (attempt.AcquireResult is Result.Success or Result.SuboptimalKhr)
                attempt.TransitionAcquireOwnership(acquireOutcome.Ownership);

            attempt.Timing.AcquireImage +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);
            attempt.AcquireCompletedTimestamp = Stopwatch.GetTimestamp();

            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.Acquire",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Frame={0} InFlightSlot={1} AcquiredImage={2} LastPresented={3}",
                    attempt.FrameNumber,
                    attempt.FrameSlot,
                    attempt.ImageIndex,
                    OutputRuntime.Desktop.LastPresentedImageIndex);
            }

            switch (attempt.AcquireResult)
            {
                case Result.Success:
                    break;
                case Result.SuboptimalKhr:
                    if (!ShouldKeepDesktopPresentScalingSwapchainCore(
                            attempt.AcquireResult,
                            attempt.InteractiveResize))
                    {
                        ScheduleSwapchainRecreate(
                            "AcquireNextImage returned SuboptimalKhr");
                    }
                    break;
                case Result.ErrorDeviceLost:
                    attempt.TransitionAcquireOwnership(
                        acquireOutcome.Ownership);
                    attempt.Stop(
                        EDesktopFrameReason.AcquireDeviceLost,
                        EDesktopFrameRecoveryAction.DeviceLost);
                    throw CreateDeviceLostException(
                        "AcquireNextImage",
                        attempt.AcquireResult);
                case Result.ErrorOutOfDateKhr:
                    ScheduleSwapchainRecreate(
                        "AcquireNextImage returned ErrorOutOfDateKhr");
                    attempt.Stop(
                        EDesktopFrameReason.AcquireOutOfDate,
                        EDesktopFrameRecoveryAction.RecreateSwapchain);
                    return EDesktopFrameFlow.Stop;
                case Result.ErrorSurfaceLostKhr:
                    attempt.Stop(
                        EDesktopFrameReason.AcquireSurfaceLost,
                        EDesktopFrameRecoveryAction.RecreateSurface);
                    throw new InvalidOperationException(
                        "AcquireNextImage reported ErrorSurfaceLostKhr. " +
                        "A platform surface restart is required; swapchain-only recreation is unsafe.");
                case Result.NotReady:
                case Result.Timeout:
                    return HandleDesktopAcquireUnavailable(
                        ref attempt,
                        in acquireOutcome,
                        xrOwnsFrameDeadline);
                default:
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Frame.{GetHashCode()}.AcquireFailure.{(int)attempt.AcquireResult}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] AcquireNextImage failed with {0}.",
                        attempt.AcquireResult);
                    attempt.Stop(
                        EDesktopFrameReason.AcquireUnexpectedFailure);
                    throw new InvalidOperationException(
                        $"Failed to acquire swapchain image ({attempt.AcquireResult}).");
            }

            _outputRuntime._desktopAcquireAvailability.Reset();
            attempt.AcquireTimelineValue = _commandRuntime.Synchronization._graphicsTimelineValue;
            _commandRuntime.Synchronization._acquireTimelineValue = attempt.AcquireTimelineValue;
            attempt.PresentSemaphore =
                OutputRuntime.Desktop.PresentBridgeSemaphores![attempt.ImageIndex];
            attempt.FrameTargetLease = DesktopWsiOutput.CreateFrameTargetLease(
                OutputRuntime,
                attempt.ImageIndex,
                checked((uint)attempt.FrameSlot),
                attempt.AcquireResult,
                attempt.AcquireSemaphore,
                attempt.PresentSemaphore);
            attempt.ExpectedPresentWaitSemaphore =
                attempt.FrameTargetLease.SubmissionWaitSemaphore;
            attempt.AdvanceTo(EDesktopFramePhase.ImageAcquired);
            return EDesktopFrameFlow.Continue;
        }

        private EDesktopFrameFlow HandleDesktopAcquireUnavailable(
            ref VulkanFrameAttempt attempt,
            in VulkanDesktopAcquireOutcome outcome,
            bool xrOwnsFrameDeadline)
        {
            EDesktopFrameReason reason = outcome.Reason switch
            {
                EVulkanDesktopPolicyReason.AcquireNotReady =>
                    EDesktopFrameReason.AcquireNotReady,
                EVulkanDesktopPolicyReason.AcquireTimeout =>
                    EDesktopFrameReason.AcquireTimeout,
                _ => throw new InvalidOperationException(
                    $"Unexpected acquire-unavailable policy {outcome.Reason}."),
            };
            bool shouldRecreate =
                _outputRuntime._desktopAcquireAvailability.ObserveUnavailable(
                    attempt.InteractiveResize,
                    out int observedCount);

            if (attempt.InteractiveResize || xrOwnsFrameDeadline)
            {
                Debug.VulkanEvery(
                    "Vulkan.Frame.NonBlockingAcquireNotReady",
                    TimeSpan.FromMilliseconds(500),
                    "[Vulkan] AcquireNextImage returned {0} during {1}; deferring desktop output without blocking the XR/resize critical path.",
                    attempt.AcquireResult,
                    xrOwnsFrameDeadline ? "an XR-owned frame" : "interactive resize");
                DrainSkippedResizeFrameOps(
                    xrOwnsFrameDeadline
                        ? "Nonblocking desktop acquisition was unavailable during an XR-owned frame"
                        : "Nonblocking desktop acquisition was unavailable during interactive resize");
                if (attempt.InteractiveResize)
                    MarkSkippedResizeFrameObserved(attempt.StartTimestamp);
                RuntimeRenderingHostServices.Presentation.RecordRenderFrameOutputWork(
                    new FrameOutputWorkTelemetry(CpuBudgetDeferrals: 1));
                attempt.Stop(reason);
                return EDesktopFrameFlow.Stop;
            }

            if (shouldRecreate)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.AcquireNotReady.Recreate",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] AcquireNextImage returned {0} {1} consecutive times. Recreating swapchain to recover.",
                    attempt.AcquireResult,
                    observedCount);
                TryRecreateSwapchainNow(
                    "Persistent acquire unavailability after failed frame");
                attempt.Stop(
                    reason,
                    EDesktopFrameRecoveryAction.RecreateSwapchain);
                return EDesktopFrameFlow.Stop;
            }

            Debug.VulkanWarningEvery(
                $"Vulkan.Frame.{GetHashCode()}.AcquireNotReady",
                TimeSpan.FromSeconds(1),
                "[Vulkan] AcquireNextImage returned {0} ({1}/{2}). Skipping this frame.",
                attempt.AcquireResult,
                observedCount,
                VulkanDesktopAcquireAvailabilityTracker
                    .DefaultRecreateThreshold);
            attempt.Stop(reason);
            return EDesktopFrameFlow.Stop;
        }
    }
}
