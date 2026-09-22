using System;
using System.Diagnostics;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        private static readonly TimeSpan SwapchainRecreateDebounce =
            TimeSpan.FromMilliseconds(16);
        private const string ResizeReleaseSuccessorDeferredDiagnosticKey =
            "Vulkan.ResizeRelease.SuccessorDeferred";

        private void ScheduleSwapchainRecreate(string reason)
        {
            long now = Stopwatch.GetTimestamp();
            bool wasInvalidated = _frameBufferInvalidated;
            _frameBufferInvalidated = true;

            if (!wasInvalidated || _outputRuntime._desktopSwapchainPolicy.RecreateRequestedAt == 0)
                _outputRuntime._desktopSwapchainPolicy.RecreateRequestedAt = now;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateScheduled",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Scheduled debounced swapchain recreate. Reason={0} RequestedAtTicks={1} WasInvalidated={2}",
                reason,
                _outputRuntime._desktopSwapchainPolicy.RecreateRequestedAt,
                wasInvalidated);
        }

        private bool TryRecreateSwapchainNow(string reason)
        {
            VulkanDesktopSwapchainPolicyState swapchainPolicy =
                _outputRuntime._desktopSwapchainPolicy;
            bool creatingResizeReleaseSuccessor =
                swapchainPolicy.ResizeReleaseHandoffState ==
                    VulkanResizeReleaseHandoffState.AwaitingReadyToRecreate;
            bool rebasingResizeReleaseSuccessor =
                swapchainPolicy.ResizeReleaseHandoffState ==
                    VulkanResizeReleaseHandoffState.AwaitingSuccessorPresent;
            if (creatingResizeReleaseSuccessor || rebasingResizeReleaseSuccessor)
            {
                var handoffLiveFramebufferSize =
                    DesktopWsiOutput.EffectiveFramebufferSize;
                bool handoffStillTargetsLiveSurface =
                    handoffLiveFramebufferSize.X > 0 &&
                    handoffLiveFramebufferSize.Y > 0 &&
                    swapchainPolicy.ResizeReleaseTargetWidth ==
                        (uint)handoffLiveFramebufferSize.X &&
                    swapchainPolicy.ResizeReleaseTargetHeight ==
                        (uint)handoffLiveFramebufferSize.Y;
                if (!handoffStillTargetsLiveSurface)
                {
                    swapchainPolicy.CancelResizeReleaseHandoff();
                    creatingResizeReleaseSuccessor = false;
                    rebasingResizeReleaseSuccessor = false;
                    Debug.VulkanWarning(
                        "[Vulkan][ResizeHandoff] Cancelled before an unrelated or stale swapchain recreation. Live={0}x{1}",
                        handoffLiveFramebufferSize.X,
                        handoffLiveFramebufferSize.Y);
                }
            }
            // A resize invalidates the current viewport generation, so it may be
            // impossible to record the replacement scene until the successor
            // swapchain exists. Completion is enforced before the successor
            // present below; blocking its creation on current-frame completion
            // would leave the handoff permanently AwaitingReadyToRecreate.
            if (creatingResizeReleaseSuccessor &&
                swapchainPolicy.ResizeReleaseSourceSwapchainGeneration !=
                OutputRuntime.Desktop.Generation)
            {
                swapchainPolicy.CancelResizeReleaseHandoff();
                creatingResizeReleaseSuccessor = false;
            }

            long recreateStart = Stopwatch.GetTimestamp();
            uint previousWidth = OutputRuntime.Desktop.Extent.Width;
            uint previousHeight = OutputRuntime.Desktop.Extent.Height;
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateImmediate",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Recreating swapchain immediately. Reason={0}",
                reason);

            if (!TryRecreateDesktopSwapchain())
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
                    OutputRuntime.Desktop.Extent.Width,
                    OutputRuntime.Desktop.Extent.Height);
                ScheduleSwapchainRecreate($"{reason}; surface not presentable yet");
                return false;
            }

            TimeSpan elapsed = Stopwatch.GetElapsedTime(recreateStart);
            _frameBufferInvalidated = false;
            swapchainPolicy.ResetAfterRecreate();
            if (creatingResizeReleaseSuccessor)
            {
                string transitionFailure = string.Empty;
                bool targetMatchesSuccessor =
                    OutputRuntime.Desktop.Extent.Width ==
                        swapchainPolicy.ResizeReleaseTargetWidth &&
                    OutputRuntime.Desktop.Extent.Height ==
                        swapchainPolicy.ResizeReleaseTargetHeight;
                if (!targetMatchesSuccessor ||
                    !swapchainPolicy.TryTransitionAfterSuccessfulRecreate(
                        OutputRuntime.Desktop.Generation,
                        out transitionFailure))
                {
                    Debug.VulkanWarning(
                        "[Vulkan][ResizeHandoff] Cancelling an invalid successor transition. " +
                        "Target={0}x{1} Actual={2}x{3} SourceGeneration={4} " +
                        "SuccessorGeneration={5} Reason={6}",
                        swapchainPolicy.ResizeReleaseTargetWidth,
                        swapchainPolicy.ResizeReleaseTargetHeight,
                        OutputRuntime.Desktop.Extent.Width,
                        OutputRuntime.Desktop.Extent.Height,
                        swapchainPolicy.ResizeReleaseSourceSwapchainGeneration,
                        OutputRuntime.Desktop.Generation,
                        targetMatchesSuccessor
                            ? transitionFailure
                            : "the recreated extent does not match the release target");
                    swapchainPolicy.CancelResizeReleaseHandoff();
                }
                else
                {
                    Debug.Vulkan(
                        "[Vulkan][ResizeHandoff] Successor swapchain is ready; retaining the old compositor image until a complete successor present. " +
                        "Target={0}x{1} SourceGeneration={2} SuccessorGeneration={3}",
                        swapchainPolicy.ResizeReleaseTargetWidth,
                        swapchainPolicy.ResizeReleaseTargetHeight,
                        swapchainPolicy.ResizeReleaseSourceSwapchainGeneration,
                        swapchainPolicy.ResizeReleaseSuccessorSwapchainGeneration);
                }
            }
            else if (rebasingResizeReleaseSuccessor)
            {
                string rebaseFailure = string.Empty;
                if (!swapchainPolicy.TryRebaseSuccessorAfterSuccessfulRecreate(
                        OutputRuntime.Desktop.Generation,
                        OutputRuntime.Desktop.Extent.Width,
                        OutputRuntime.Desktop.Extent.Height,
                        out rebaseFailure))
                {
                    Debug.VulkanWarning(
                        "[Vulkan][ResizeHandoff] Cancelling after a successor swapchain was recreated again. " +
                        "Target={0}x{1} Actual={2}x{3} SourceGeneration={4} " +
                        "SuccessorGeneration={5} Reason={6}",
                        swapchainPolicy.ResizeReleaseTargetWidth,
                        swapchainPolicy.ResizeReleaseTargetHeight,
                        OutputRuntime.Desktop.Extent.Width,
                        OutputRuntime.Desktop.Extent.Height,
                        swapchainPolicy.ResizeReleaseSourceSwapchainGeneration,
                        OutputRuntime.Desktop.Generation,
                        rebaseFailure);
                    swapchainPolicy.CancelResizeReleaseHandoff();
                }
                else
                {
                    Debug.Vulkan(
                        "[Vulkan][ResizeHandoff] Rebased the unpublished successor after another successful swapchain recreation. " +
                        "Target={0}x{1} SourceGeneration={2} SuccessorGeneration={3}",
                        swapchainPolicy.ResizeReleaseTargetWidth,
                        swapchainPolicy.ResizeReleaseTargetHeight,
                        swapchainPolicy.ResizeReleaseSourceSwapchainGeneration,
                        swapchainPolicy.ResizeReleaseSuccessorSwapchainGeneration);
                }
            }
            _outputRuntime.RequestImGuiFrameMarkerReset();

            var liveFramebufferSize = DesktopWsiOutput.EffectiveFramebufferSize;
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateResult",
                TimeSpan.FromMilliseconds(500),
                "[Vulkan] Swapchain recreate completed. Reason={0} ElapsedMs={1:F3} Previous={2}x{3} Current={4}x{5} Live={6}x{7} Divergence={8}x{9}",
                reason,
                elapsed.TotalMilliseconds,
                previousWidth,
                previousHeight,
                OutputRuntime.Desktop.Extent.Width,
                OutputRuntime.Desktop.Extent.Height,
                liveFramebufferSize.X,
                liveFramebufferSize.Y,
                (int)liveFramebufferSize.X - (int)OutputRuntime.Desktop.Extent.Width,
                (int)liveFramebufferSize.Y - (int)OutputRuntime.Desktop.Extent.Height);
            return true;
        }

        private void TrackPendingDesktopSurfaceSize(
            ref VulkanFrameAttempt attempt)
        {
            if (attempt.LiveSurfaceValid)
            {
                if (_outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth != attempt.LiveSurfaceWidth ||
                    _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight != attempt.LiveSurfaceHeight)
                {
                    _outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth = attempt.LiveSurfaceWidth;
                    _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight = attempt.LiveSurfaceHeight;
                    _outputRuntime._desktopSwapchainPolicy.ResizeLastChangedAt =
                        Stopwatch.GetTimestamp();
                }

                return;
            }

            _outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth = 0;
            _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight = 0;
            _outputRuntime._desktopSwapchainPolicy.ResizeLastChangedAt = 0;
        }

        private void ApplyDesktopSwapchainExtentPolicy(
            ref VulkanFrameAttempt attempt)
        {
            if (attempt.LiveSurfaceValid &&
                !attempt.SurfaceMatchesSwapchain)
            {
                VulkanDesktopSwapchainPolicyState swapchainPolicy =
                    _outputRuntime._desktopSwapchainPolicy;
                if (attempt.InteractiveResize)
                {
                    if (swapchainPolicy.ResizeReleaseHandoffState ==
                        VulkanResizeReleaseHandoffState.AwaitingSuccessorPresent)
                    {
                        swapchainPolicy.CancelResizeReleaseHandoff();
                    }
                    else if (swapchainPolicy.ResizeReleaseHandoffState ==
                        VulkanResizeReleaseHandoffState.AwaitingReadyToRecreate)
                    {
                        _ = swapchainPolicy.TryRebaseForInteractiveResize(
                            attempt.LiveSurfaceWidth,
                            attempt.LiveSurfaceHeight,
                            OutputRuntime.Desktop.Generation,
                            Stopwatch.GetTimestamp(),
                            out _);
                    }

                    if (attempt.CanPresentMismatchedSwapchainExtent)
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.Frame.{GetHashCode()}.PresentScaledInteractiveResize",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Presenting through validated WSI scaling during interactive resize. LiveSurface={0}x{1} Swapchain={2}x{3}.",
                            attempt.LiveSurfaceWidth,
                            attempt.LiveSurfaceHeight,
                            OutputRuntime.Desktop.Extent.Width,
                            OutputRuntime.Desktop.Extent.Height);
                    }
                    else
                    {
                        ScheduleSwapchainRecreate(
                            "Interactive resize surface/swapchain size mismatch");
                    }
                }
                else
                {
                    if (swapchainPolicy.ResizeReleaseHandoffState ==
                        VulkanResizeReleaseHandoffState.AwaitingReadyToRecreate &&
                        (swapchainPolicy.ResizeReleaseTargetWidth != attempt.LiveSurfaceWidth ||
                         swapchainPolicy.ResizeReleaseTargetHeight != attempt.LiveSurfaceHeight))
                    {
                        _ = swapchainPolicy.TryRebaseForInteractiveResize(
                            attempt.LiveSurfaceWidth,
                            attempt.LiveSurfaceHeight,
                            OutputRuntime.Desktop.Generation,
                            Stopwatch.GetTimestamp(),
                            out _);
                    }
                    ScheduleSwapchainRecreate(
                        "Surface/swapchain size mismatch");
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
                    OutputRuntime.Desktop.Extent.Width,
                    OutputRuntime.Desktop.Extent.Height,
                    attempt.InteractiveResize,
                    attempt.CanPresentMismatchedSwapchainExtent);
                return;
            }

            if (_outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth == OutputRuntime.Desktop.Extent.Width &&
                _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight == OutputRuntime.Desktop.Extent.Height)
            {
                _outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth = 0;
                _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight = 0;
                _outputRuntime._desktopSwapchainPolicy.ResizeLastChangedAt = 0;
                VulkanDesktopSwapchainPolicyState swapchainPolicy =
                    _outputRuntime._desktopSwapchainPolicy;
                if (swapchainPolicy.ResizeReleaseHandoffState ==
                        VulkanResizeReleaseHandoffState.AwaitingReadyToRecreate &&
                    swapchainPolicy.ResizeReleaseSourceSwapchainGeneration ==
                        OutputRuntime.Desktop.Generation)
                {
                    swapchainPolicy.CancelResizeReleaseHandoff();
                    Debug.Vulkan(
                        "[Vulkan][ResizeHandoff] Cancelled because the live surface returned to the retained swapchain extent.");
                }
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
                _outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth > 0 &&
                _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight > 0;
            bool pendingMatchesLive =
                !hasPendingSurfaceSize ||
                (_outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth == attempt.LiveSurfaceWidth &&
                 _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight == attempt.LiveSurfaceHeight);

            if (attempt.InteractiveResize)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForInteractiveResize",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Freezing the published swapchain/resource generation during interactive resize. Pending={0}x{1} Live={2}x{3} Swapchain={4}x{5} PendingMatchesLive={6}",
                    _outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth,
                    _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight,
                    attempt.LiveSurfaceWidth,
                    attempt.LiveSurfaceHeight,
                    OutputRuntime.Desktop.Extent.Width,
                    OutputRuntime.Desktop.Extent.Height,
                    pendingMatchesLive);
                return;
            }

            // WM_EXITSIZEMOVE already closes the interactive resize transaction.
            // Once the coalesced extent matches, there is no second quiet-period
            // requirement: delaying here creates a visible late release transition.
            if (pendingMatchesLive)
            {
                TryRecreateSwapchainNow(
                    "Debounce elapsed before frame acquire (latest non-interactive extent)");
                UpdateAttemptSwapchainExtentMatch(ref attempt);
                return;
            }

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForResizeSettle",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Deferring swapchain recreate until the coalesced extent matches the live surface. Pending={0}x{1} Live={2}x{3}",
                _outputRuntime._desktopSwapchainPolicy.PendingSurfaceWidth,
                _outputRuntime._desktopSwapchainPolicy.PendingSurfaceHeight,
                attempt.LiveSurfaceWidth,
                attempt.LiveSurfaceHeight);
        }

        private void CaptureResizeReleaseHandoffFromSuccessfulHeldPresent(
            ref VulkanFrameAttempt attempt)
        {
            if (!attempt.InteractiveResize ||
                attempt.SurfaceMatchesSwapchain ||
                !attempt.ScenePrimaryRecordedThisFrame ||
                attempt.SceneSwapchainWriteCount <= 0 ||
                !attempt.HasAuthoredSwapchainWrite ||
                !attempt.Submitted ||
                attempt.GraphicsSignalValue == 0)
            {
                return;
            }

            VulkanDesktopSwapchainPolicyState swapchainPolicy =
                _outputRuntime._desktopSwapchainPolicy;
            VulkanPresentationSourceTuple heldPresentationSource =
                attempt.PresentationSource;
            VulkanResidentTemplateDependencyLease? heldPresentationSourceLease = null;
            bool reusingHeldPresentationSourceLease =
                swapchainPolicy.TryGetHeldPresentationSource(
                    out _,
                    out VulkanResidentTemplateDependencyLease? existingHeldPresentationSourceLease) &&
                ResourceRuntime.TryValidateRetainedPresentationSourceForReplay(
                    heldPresentationSource,
                    existingHeldPresentationSourceLease,
                    out _);
            if (reusingHeldPresentationSourceLease)
                heldPresentationSourceLease = existingHeldPresentationSourceLease;

            swapchainPolicy.BeginSuccessfulHeldPresentCapture();
            bool captureFailed = false;
            string captureFailure = string.Empty;
            if (!reusingHeldPresentationSourceLease &&
                !ResourceRuntime.TryValidatePresentationSourceForReplay(
                    heldPresentationSource,
                    out captureFailure))
            {
                captureFailed = true;
            }
            else if (!reusingHeldPresentationSourceLease)
            {
                Span<VulkanResidentTemplateDependencyRequest> dependencies =
                    stackalloc VulkanResidentTemplateDependencyRequest[3];
                dependencies[0] = new(
                    EVulkanResidentTemplateDependencyKind.Image,
                    heldPresentationSource.Image.Handle,
                    heldPresentationSource.ImageAllocationGeneration);
                dependencies[1] = new(
                    EVulkanResidentTemplateDependencyKind.ImageView,
                    heldPresentationSource.ImageView.Handle,
                    heldPresentationSource.ImageViewGeneration);
                dependencies[2] = new(
                    EVulkanResidentTemplateDependencyKind.Sampler,
                    heldPresentationSource.Sampler.Handle,
                    heldPresentationSource.SamplerGeneration);
                if (!ResourceRuntime.TryAcquireResidentTemplateDependencies(
                        dependencies,
                        out heldPresentationSourceLease,
                        out string? leaseFailure))
                {
                    captureFailed = true;
                    captureFailure = leaseFailure ??
                        "The held presentation source image could not be pinned.";
                }
            }

            var viewports = DesktopWsiOutput.Window.Viewports;
            for (int index = 0; !captureFailed && index < viewports.Count; index++)
            {
                XRViewport viewport = viewports[index];
                if (viewport.CompletedSceneCommandChainThisFrame &&
                    !swapchainPolicy.TryAddRequiredSceneViewport(
                        viewport,
                        out captureFailure))
                {
                    captureFailed = true;
                    break;
                }

                if (viewport.TryGetActiveScreenSpaceUserInterface(
                        out IRuntimeScreenSpaceUserInterface? userInterface) &&
                    userInterface!.CompletedRenderCommandChainThisFrame &&
                    !swapchainPolicy.TryAddRequiredScreenSpaceUserInterface(
                        viewport,
                        userInterface,
                        out captureFailure))
                {
                    captureFailed = true;
                    break;
                }
            }

            bool requiresScreenSpaceUi =
                swapchainPolicy.RequiredScreenSpaceUserInterfaceCount > 0;
            string armFailure = string.Empty;
            if (captureFailed ||
                !swapchainPolicy.TryArmFromSuccessfulHeldPresent(
                    attempt.LiveSurfaceWidth,
                    attempt.LiveSurfaceHeight,
                    OutputRuntime.Desktop.Generation,
                    in heldPresentationSource,
                    heldPresentationSourceLease!,
                    requiresSceneContributor: true,
                    requiresScreenSpaceUserInterfaceContributor:
                        requiresScreenSpaceUi,
                    requiresImGuiContributor:
                        attempt.HasImGuiOverlayCommandBuffer,
                    armedAt: Stopwatch.GetTimestamp(),
                    reason: out armFailure))
            {
                heldPresentationSourceLease?.Dispose();
                swapchainPolicy.CancelResizeReleaseHandoff();
                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.ResizeHandoffArmFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan][ResizeHandoff] Could not retain the successful held frame for resize release. Reason={0}",
                    captureFailed ? captureFailure : armFailure);
                return;
            }

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.ResizeHandoffArmed",
                TimeSpan.FromSeconds(1),
                "[Vulkan][ResizeHandoff] Armed from a successful held presentation. Target={0}x{1} SourceGeneration={2} SourceImage=0x{3:X} SourceExtent={4}x{5} SceneViewports={6} ScreenUi={7} ImGui={8}",
                swapchainPolicy.ResizeReleaseTargetWidth,
                swapchainPolicy.ResizeReleaseTargetHeight,
                swapchainPolicy.ResizeReleaseSourceSwapchainGeneration,
                heldPresentationSource.Image.Handle,
                heldPresentationSource.Width,
                heldPresentationSource.Height,
                swapchainPolicy.RequiredSceneViewportCount,
                swapchainPolicy.RequiredScreenSpaceUserInterfaceCount,
                swapchainPolicy.RequiresImGuiContributor);
        }

        private void ClassifyIncompleteResizeReleaseSuccessorBeforeAcquire(
            ref VulkanFrameAttempt attempt)
        {
            VulkanDesktopSwapchainPolicyState swapchainPolicy =
                _outputRuntime._desktopSwapchainPolicy;
            if (swapchainPolicy.ResizeReleaseHandoffState !=
                VulkanResizeReleaseHandoffState.AwaitingSuccessorPresent)
            {
                return;
            }

            VulkanResizeReleaseBlocker blocker = VulkanResizeReleaseBlocker.None;
            if (OutputRuntime.Desktop.Generation !=
                swapchainPolicy.ResizeReleaseSuccessorSwapchainGeneration)
            {
                blocker = VulkanResizeReleaseBlocker.SuccessorGenerationMismatch;
            }
            else if (OutputRuntime.Desktop.Extent.Width !=
                         swapchainPolicy.ResizeReleaseTargetWidth ||
                     OutputRuntime.Desktop.Extent.Height !=
                         swapchainPolicy.ResizeReleaseTargetHeight)
            {
                blocker = VulkanResizeReleaseBlocker.SuccessorExtentMismatch;
            }
            if (blocker == VulkanResizeReleaseBlocker.None ||
                !swapchainPolicy.HasActiveResizeReleaseHandoff)
            {
                return;
            }

            // Generation and extent are immutable authorities for this dispatch.
            // Contributor receipts are verified after recording. A fresh-empty
            // plan must be allowed to submit its useful nonterminal work; the
            // following authored frame completes the continuity handoff.

            attempt.ResizeReleaseContinuity = true;
            attempt.ResizeReleaseBlocker = blocker;
            Debug.VulkanEvery(
                ResizeReleaseSuccessorDeferredDiagnosticKey,
                TimeSpan.FromMilliseconds(250),
                "[Vulkan][ResizeHandoff] Continuing resize-release handoff with a recovery presentation. Reason={0}",
                blocker);
        }

        /// <summary>
        /// Verifies the artifacts actually recorded for the acquired successor.
        /// Unlike producer Completed*ThisFrame flags or pending ImGui snapshots,
        /// these facts belong to this exact attempt and cannot change underneath
        /// admission.
        /// </summary>
        private bool TryGetRecordedResizeReleaseBlocker(
            ref VulkanFrameAttempt attempt,
            out VulkanResizeReleaseBlocker blocker)
        {
            blocker = VulkanResizeReleaseBlocker.None;
            VulkanDesktopSwapchainPolicyState swapchainPolicy =
                _outputRuntime._desktopSwapchainPolicy;
            if (swapchainPolicy.ResizeReleaseHandoffState !=
                VulkanResizeReleaseHandoffState.AwaitingSuccessorPresent)
            {
                return false;
            }

            if (OutputRuntime.Desktop.Generation !=
                swapchainPolicy.ResizeReleaseSuccessorSwapchainGeneration)
            {
                blocker = VulkanResizeReleaseBlocker.SuccessorGenerationMismatch;
            }
            else if (OutputRuntime.Desktop.Extent.Width !=
                         swapchainPolicy.ResizeReleaseTargetWidth ||
                     OutputRuntime.Desktop.Extent.Height !=
                         swapchainPolicy.ResizeReleaseTargetHeight)
            {
                blocker = VulkanResizeReleaseBlocker.SuccessorExtentMismatch;
            }
            else if (!attempt.ScenePrimaryRecordedThisFrame ||
                     attempt.SceneSwapchainWriteCount <= 0)
            {
                blocker = VulkanResizeReleaseBlocker.AuthoredTerminalProducerMissing;
            }
            else if (swapchainPolicy.RequiresImGuiContributor &&
                     !attempt.HasImGuiOverlayCommandBuffer)
            {
                blocker = VulkanResizeReleaseBlocker.ImGuiSnapshotIncomplete;
            }

            return blocker != VulkanResizeReleaseBlocker.None;
        }

        private void TryCompleteResizeReleaseHandoffAfterSuccessorPresent(
            ref VulkanFrameAttempt attempt)
        {
            VulkanDesktopSwapchainPolicyState swapchainPolicy =
                _outputRuntime._desktopSwapchainPolicy;
            if (swapchainPolicy.ResizeReleaseHandoffState !=
                    VulkanResizeReleaseHandoffState.AwaitingSuccessorPresent ||
                OutputRuntime.Desktop.Generation !=
                    swapchainPolicy.ResizeReleaseSuccessorSwapchainGeneration ||
                OutputRuntime.Desktop.Extent.Width !=
                    swapchainPolicy.ResizeReleaseTargetWidth ||
                OutputRuntime.Desktop.Extent.Height !=
                    swapchainPolicy.ResizeReleaseTargetHeight ||
                !attempt.Presented ||
                !attempt.ScenePrimaryRecordedThisFrame ||
                !attempt.HasAuthoredSwapchainWrite ||
                (swapchainPolicy.RequiresSceneContributor &&
                 attempt.SceneSwapchainWriteCount <= 0) ||
                (swapchainPolicy.RequiresImGuiContributor &&
                 !attempt.HasImGuiOverlayCommandBuffer))
            {
                return;
            }

            // A sealed plan can request a synthetic terminal even when scene
            // commands later write the acquired image. Use the recorder's
            // receipt from before finalization: total writes also count clears,
            // continuity blits, and overlays and cannot prove scene readiness.

            // Recovery replay intentionally preserves the held image while the
            // successor catches up. It must never be treated as the authored
            // successor presentation that releases the retained source.
            if (attempt.ResizeReleaseContinuity ||
                attempt.ResizeReleaseBlocker != VulkanResizeReleaseBlocker.None)
            {
                return;
            }

            if (!swapchainPolicy.HasActiveResizeReleaseHandoff)
                return;

            ulong successorGeneration =
                swapchainPolicy.ResizeReleaseSuccessorSwapchainGeneration;
            uint targetWidth = swapchainPolicy.ResizeReleaseTargetWidth;
            uint targetHeight = swapchainPolicy.ResizeReleaseTargetHeight;
            if (!swapchainPolicy.TryCompleteAfterSuccessorPresent(
                    OutputRuntime.Desktop.Generation,
                    out string completionFailure))
            {
                Debug.VulkanWarning(
                    "[Vulkan][ResizeHandoff] Successor presentation did not complete the handoff. Reason={0}",
                    completionFailure);
                return;
            }

            Debug.Vulkan(
                "[Vulkan][ResizeHandoff] Completed with an authored successor presentation. Target={0}x{1} SuccessorGeneration={2} Frame={3}",
                targetWidth,
                targetHeight,
                successorGeneration,
                attempt.FrameNumber);
        }

        private void UpdateAttemptSwapchainExtentMatch(
            ref VulkanFrameAttempt attempt)
        {
            attempt.SurfaceMatchesSwapchain =
                attempt.LiveSurfaceValid &&
                attempt.LiveSurfaceWidth == OutputRuntime.Desktop.Extent.Width &&
                attempt.LiveSurfaceHeight == OutputRuntime.Desktop.Extent.Height;
        }

        private bool ShouldRunSwapchainRecreate(bool interactiveResize)
        {
            if (!_frameBufferInvalidated)
                return false;

            if (interactiveResize)
                return false;

            if (_outputRuntime._desktopSwapchainPolicy.RecreateRequestedAt == 0)
                return true;

            return Stopwatch.GetElapsedTime(_outputRuntime._desktopSwapchainPolicy.RecreateRequestedAt) >= SwapchainRecreateDebounce;
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

            return OutputRuntime.Desktop.IsPresentScalingExtentSupported(
                swapchainWidth,
                swapchainHeight);
        }

        internal bool ShouldKeepDesktopPresentScalingSwapchainCore(Result result, bool interactiveResize)
            => result == Result.SuboptimalKhr &&
                interactiveResize &&
                OutputRuntime.Desktop.PresentScalingActive;

    }
}
