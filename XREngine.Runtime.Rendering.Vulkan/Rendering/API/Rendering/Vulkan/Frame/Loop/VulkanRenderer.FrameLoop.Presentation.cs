using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        internal EDesktopFrameFlow PresentSubmittedDesktopFrame(
            ref VulkanFrameAttempt attempt)
        {
            // Detached ImGui viewports sample renderer-owned textures. Submit them only
            // after the primary scene submission so graphics-queue ordering makes those
            // resources visible without introducing a second engine-wide frame graph.
            _imguiBackend?.RenderPendingViewports();

            VulkanDesktopPresentDispatchOutcome dispatch = QueueDesktopPresentCore(
                ref attempt,
                "Vulkan.FrameLifecycle.QueuePresent",
                disableFrameGenerationReason: null);
            Result result = dispatch.Result;

            if (!dispatch.Dispatched &&
                result != Result.ErrorDeviceLost)
            {
                RecordDesktopPresentBookkeeping(
                    ref attempt,
                    result,
                    presentAccepted: false,
                    hasValidFrameContent: false);
                ResolveDesktopAcquireBySwapchainRecreation(
                    ref attempt,
                    "Desktop presentation dispatch was rejected before vkQueuePresent");
                CompleteDesktopFrameSlot(ref attempt);
                attempt.AdvanceTo(EDesktopFramePhase.Recovered);
                attempt.Flow = EDesktopFrameFlow.Completed;
                throw dispatch.AuxiliaryFailure ??
                    new InvalidOperationException(
                        "Desktop presentation dispatch was rejected before vkQueuePresent.");
            }

            VulkanDesktopPresentOutcome presentOutcome =
                DesktopWsiOutput.ClassifyPresent(result);
            bool presentAccepted =
                presentOutcome.PresentationAccepted;
            RecordDesktopPresentBookkeeping(
                ref attempt,
                result,
                presentAccepted,
                hasValidFrameContent: true);
            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.Present",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Frame={0} PresentedImage={1} Result={2}",
                    attempt.FrameNumber,
                    attempt.ImageIndex,
                    result);
            }

            if (result == Result.ErrorDeviceLost)
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership
                        .IndeterminateAfterDeviceLoss);
                attempt.Stop(
                    EDesktopFrameReason.PresentDeviceLost,
                    EDesktopFrameRecoveryAction.DeviceLost);
                throw CreateDeviceLostException("QueuePresent", result);
            }

            attempt.TransitionAcquireOwnership(
                EVulkanDesktopAcquireOwnership.ResolvedByPresentation);
            Exception? policyFailure = ApplyDesktopPresentPolicy(
                ref attempt,
                result,
                "QueuePresent");
            if (presentOutcome.AdvanceFrameSlot)
                CompleteDesktopFrameSlot(ref attempt);
            attempt.AdvanceTo(EDesktopFramePhase.Presented);
            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.PostPresentAuxiliary);

            Exception? deferredFailure = attempt.DeferredFailure;
            if (deferredFailure is not null)
                ExceptionDispatchInfo.Capture(deferredFailure).Throw();
            if (dispatch.AuxiliaryFailure is not null)
            {
                ExceptionDispatchInfo
                    .Capture(dispatch.AuxiliaryFailure)
                    .Throw();
            }
            if (policyFailure is not null)
                ExceptionDispatchInfo.Capture(policyFailure).Throw();

            if (!attempt.InteractiveResize &&
                ShouldRunSwapchainRecreate(interactiveResize: false))
            {
                TryRecreateSwapchainNow(
                    "Debounce elapsed after present");
            }

            attempt.Reason = EDesktopFrameReason.Success;
            attempt.Flow = EDesktopFrameFlow.Completed;
            return EDesktopFrameFlow.Completed;
        }

        internal VulkanDesktopPresentDispatchOutcome QueueDesktopPresentCore(
            ref VulkanFrameAttempt attempt,
            string profileScope,
            string? disableFrameGenerationReason)
        {
            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.Presentation);
            Exception? auxiliaryFailure = null;
            bool dispatched = false;
            Semaphore queuedPresentSemaphore = attempt.PresentSemaphore;
            uint queuedImageIndex = attempt.ImageIndex;
            var swapChains = stackalloc[] { OutputRuntime.Desktop.Swapchain };
            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &queuedPresentSemaphore,
                SwapchainCount = 1,
                PSwapchains = swapChains,
                PImageIndices = &queuedImageIndex,
            };

            long stageStartTimestamp = Stopwatch.GetTimestamp();
            Result result;
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                       profileScope))
            {
                if (disableFrameGenerationReason is not null)
                {
                    DisableStreamlineFrameGenerationBeforeSwapchainMutation(
                        disableFrameGenerationReason);
                }

                DrainStreamlineFrameGenerationDisableBeforePresent();
                try
                {
                    MarkDlssFrameGenerationPclMarker(
                        NvidiaDlssManager.Native.StreamlinePclMarker.PresentStart);
                }
                catch (Exception ex)
                {
                    auxiliaryFailure = ex;
                }

                dispatched = TryPresentToQueueTracked(
                        _deviceContext.PresentQueue,
                        ref presentInfo,
                        out result,
                        out string failureReason,
                        caller: "RenderFrameCallback");
                if (!dispatched)
                {
                    if (result == Result.ErrorDeviceLost)
                    {
                        attempt.Timing.PresentQueue +=
                            Stopwatch.GetElapsedTime(stageStartTimestamp);
                        return new VulkanDesktopPresentDispatchOutcome(
                            result,
                            dispatched: false,
                            auxiliaryFailure);
                    }

                    auxiliaryFailure ??= new InvalidOperationException(
                        $"NVIDIA DLSS frame generation failed to present through Streamline: {failureReason}");
                }
            }

            attempt.Timing.PresentQueue +=
                Stopwatch.GetElapsedTime(stageStartTimestamp);

            // QueuePresent has returned. Commit presentation bookkeeping before
            // allowing an auxiliary PCL marker failure to propagate.
            try
            {
                MarkDlssFrameGenerationPclMarker(
                    NvidiaDlssManager.Native.StreamlinePclMarker.PresentEnd);
            }
            catch (Exception ex)
            {
                auxiliaryFailure ??= ex;
            }

            return new VulkanDesktopPresentDispatchOutcome(
                result,
                dispatched,
                auxiliaryFailure);
        }

        private void RecordDesktopPresentBookkeeping(
            ref VulkanFrameAttempt attempt,
            Result result,
            bool presentAccepted,
            bool hasValidFrameContent)
        {
            RecordFinalPresentationLedger(
                ref attempt,
                result,
                presentAccepted,
                hasValidFrameContent);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPresentResult(
                (int)result,
                presentAccepted);
            if (!presentAccepted)
                return;

            OutputRuntime.Desktop.LastPresentedImageIndex = attempt.ImageIndex;
            if (OutputRuntime.Desktop.ImageEverPresented is not null &&
                attempt.ImageIndex < OutputRuntime.Desktop.ImageEverPresented.Length)
            {
                OutputRuntime.Desktop.ImageEverPresented[attempt.ImageIndex] = true;
            }

            if (!hasValidFrameContent ||
                OutputRuntime.Desktop.ImageHasValidPresentedContent is null ||
                attempt.ImageIndex >=
                OutputRuntime.Desktop.ImageHasValidPresentedContent.Length)
            {
                return;
            }

            bool submittedFrameWroteSwapchain =
                attempt.SceneSwapchainWriteCount > 0 ||
                attempt.RecoverySwapchainWriteCount > 0 ||
                attempt.HasImGuiOverlayCommandBuffer ||
                attempt.HasDynamicTextOverlayCommandBuffer;
            if (attempt.SceneSwapchainWriteCount > 0)
                Volatile.Write(ref _outputRuntime._hasPresentedCompleteSceneFrame, 1);

            if (submittedFrameWroteSwapchain)
            {
                OutputRuntime.Desktop.ImageHasValidPresentedContent[
                    attempt.ImageIndex] = true;
                return;
            }

            if (!OutputRuntime.Desktop.ImageHasValidPresentedContent[attempt.ImageIndex])
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.PresentedWithoutValidFinalWrite",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan][FrameFailure] Presented swapchain image {0} without a recorded final write or valid prior contents. plannerRev={1} plan=0x{2:X16} allocation=0x{3:X16} sceneWrites={4} imgui={5} dynamicUi={6}",
                    attempt.ImageIndex,
                    ResourcePlannerRevision,
                    ActiveResourcePlannerSignature,
                    ActiveResourceAllocationSignature,
                    attempt.SceneSwapchainWriteCount,
                    attempt.HasImGuiOverlayCommandBuffer,
                    attempt.HasDynamicTextOverlayCommandBuffer);
            }
        }

        private Exception? ApplyDesktopPresentPolicy(
            ref VulkanFrameAttempt attempt,
            Result result,
            string operation)
        {
            switch (result)
            {
                case Result.Success:
                    return null;
                case Result.SuboptimalKhr:
                    if (!ShouldKeepDesktopPresentScalingSwapchainCore(
                            result,
                            attempt.InteractiveResize))
                    {
                        ScheduleSwapchainRecreate(
                            $"{operation} returned SuboptimalKhr");
                    }
                    attempt.Reason = EDesktopFrameReason.PresentSuboptimal;
                    return null;
                case Result.ErrorOutOfDateKhr:
                    ScheduleSwapchainRecreate(
                        $"{operation} returned ErrorOutOfDateKhr");
                    attempt.Reason = EDesktopFrameReason.PresentOutOfDate;
                    return null;
                case Result.ErrorSurfaceLostKhr:
                    attempt.Reason = EDesktopFrameReason.PresentSurfaceLost;
                    attempt.RecoveryAction =
                        EDesktopFrameRecoveryAction.RecreateSurface;
                    return new InvalidOperationException(
                        $"{operation} reported ErrorSurfaceLostKhr. " +
                        "A platform surface restart is required; swapchain-only recreation is unsafe.");
                default:
                    attempt.Reason =
                        EDesktopFrameReason.PresentUnexpectedFailure;
                    return new InvalidOperationException(
                        $"Failed to present swapchain image ({result}).");
            }
        }

        private void CompleteDesktopFrameSlot(ref VulkanFrameAttempt attempt)
        {
            if (attempt.SlotCompleted)
                return;

            AdvanceDesktopFrameSlot(attempt.FrameSlot);
            attempt.SlotCompleted = true;
            RecordDesktopFrameTickObserved(Stopwatch.GetTimestamp());
        }
    }
}
