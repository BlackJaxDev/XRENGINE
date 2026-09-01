using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        internal EDesktopFrameFlow PresentSubmittedDesktopFrame(
            ref VulkanFrameAttempt attempt)
        {
            long allocationBefore =
                GC.GetAllocatedBytesForCurrentThread();
            try
            {
                return PresentSubmittedDesktopFrameCore(ref attempt);
            }
            finally
            {
                VulkanFrameHotPathTelemetry.RecordPresent(
                    allocationBefore);
            }
        }

        private EDesktopFrameFlow PresentSubmittedDesktopFrameCore(
            ref VulkanFrameAttempt attempt)
        {
            // Detached ImGui viewports sample renderer-owned textures. Submit them only
            // after the primary scene submission so graphics-queue ordering makes those
            // resources visible without introducing a second engine-wide frame graph.
            if (!attempt.InteractiveResizeOverlayOnly)
                _imguiBackend?.RenderPendingViewports();

            VulkanDesktopPresentDispatchOutcome dispatch = QueueDesktopPresentCore(
                ref attempt,
                "Vulkan.FrameLifecycle.QueuePresent",
                disableFrameGenerationReason: null);
            Result result = dispatch.Result;
            attempt.PresentResult = result;
            attempt.PresentDispatched = dispatch.Dispatched;
            bool presentationReleaseEnqueued =
                dispatch.Dispatched &&
                VulkanWsiPresentResult.EnqueuesPresentationRelease(result);
            if (result == Result.ErrorDeviceLost)
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss);
            }
            else if (presentationReleaseEnqueued)
            {
                attempt.TransitionAcquireOwnership(
                    EVulkanDesktopAcquireOwnership.ResolvedByPresentation);
            }

            if (!presentationReleaseEnqueued &&
                result != Result.ErrorDeviceLost)
            {
                if (VulkanWsiPresentResult.RequiresOutputQuarantine(
                        dispatch.Dispatched,
                        result))
                {
                    QuarantineDesktopFrameAdmission(
                        ref attempt,
                        $"QueuePresent returned an indeterminate WSI result: {result}.");
                }
                RecordDesktopPresentBookkeeping(
                    ref attempt,
                    result,
                    presentAccepted: false,
                    hasValidFrameContent: false);
                ResolveDesktopAcquireBySwapchainRecreation(
                    ref attempt,
                    "Desktop presentation did not enqueue WSI release work");
                CompleteDesktopFrameSlot(ref attempt);
                attempt.AdvanceTo(EDesktopFramePhase.Recovered);
                attempt.Flow = EDesktopFrameFlow.Completed;
                Exception? nonEnqueuePolicyFailure = ApplyDesktopPresentPolicy(
                    ref attempt,
                    result,
                    "QueuePresent");
                throw dispatch.AuxiliaryFailure ?? nonEnqueuePolicyFailure ??
                    new InvalidOperationException(
                        $"Desktop presentation did not enqueue WSI release work ({result}).");
            }

            VulkanDesktopPresentOutcome presentOutcome =
                DesktopWsiOutput.ClassifyPresent(result);
            bool presentAccepted =
                presentOutcome.PresentationAccepted;
            attempt.Timing.PresentDispatched = dispatch.Dispatched;
            attempt.Timing.PresentationAccepted = presentAccepted;
            attempt.Timing.FramesAhead = attempt.Submitted ? 1 : 0;
            if (presentAccepted)
            {
                long presentCompletedTimestamp =
                    attempt.PresentCompletedTimestamp != 0L
                        ? attempt.PresentCompletedTimestamp
                        : Stopwatch.GetTimestamp();
                long previousPresentCompletedTimestamp = Interlocked.Exchange(
                    ref _lastAcceptedPresentCompletedTimestamp,
                    presentCompletedTimestamp);
                if (previousPresentCompletedTimestamp != 0L &&
                    presentCompletedTimestamp > previousPresentCompletedTimestamp)
                {
                    attempt.Timing.ActualPresentInterval =
                        Stopwatch.GetElapsedTime(
                            previousPresentCompletedTimestamp,
                            presentCompletedTimestamp);
                }

                bool presentedNew =
                    attempt.ScenePrimaryRecordedThisFrame &&
                    attempt.Submitted &&
                    attempt.GraphicsSignalValue != 0UL;
                bool presentedInteractiveOverlay =
                    attempt.InteractiveResizeOverlayOnly &&
                    attempt.HasImGuiOverlayCommandBuffer &&
                    attempt.Submitted &&
                    attempt.GraphicsSignalValue != 0UL;
                if (presentedNew)
                {
                    attempt.Presented = true;
                    attempt.PresentedSourceFrameId = attempt.FrameNumber;
                }
                else if (presentedInteractiveOverlay)
                {
                    attempt.Presented = true;
                    attempt.PresentedSourceFrameId =
                        OutputRuntime.Desktop.LastPresentedFrameNumber;
                }
                else if (attempt.WorkClass == ERenderOutputWorkClass.PresentNow)
                {
                    TimeSpan elapsed =
                        Stopwatch.GetElapsedTime(attempt.StartTimestamp);
                    VulkanPresentNowReadinessException failure = new(
                        attempt.FrameNumber,
                        EVulkanPresentNowReadinessStage.Presentation,
                        "presented-source-frame",
                        "DesktopScene -> new primary -> new submission -> present wait",
                        elapsed,
                        TimeSpan.Zero,
                        $"Presentation was accepted without truthful new-frame identity " +
                        $"(recorded={attempt.ScenePrimaryRecordedThisFrame}, " +
                        $"submitted={attempt.Submitted}, serial={attempt.GraphicsSignalValue}).");
                    _presentNowTerminalFailure ??= failure;
                    attempt.DeferredFailure ??= failure;
                    Debug.VulkanError(
                        $"[Vulkan][PresentNow][RendererPaused] {failure.Message}");
                }
            }
            RecordDesktopPresentBookkeeping(
                ref attempt,
                result,
                presentAccepted,
                hasValidFrameContent: true);
            if (presentAccepted)
            {
                CaptureResizeReleaseHandoffFromSuccessfulHeldPresent(
                    ref attempt);
                TryCompleteResizeReleaseHandoffAfterSuccessorPresent(
                    ref attempt);
            }
            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.Present",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan][PresentNow] frame={0} sceneEpoch={1} outputGeneration={2} " +
                    "image={3} commandBuffer=0x{4:X} submitSerial={5} " +
                    "presentedSourceFrame={6} result={7} policy={8} workClass={9}",
                    attempt.FrameNumber,
                    attempt.AcceptedSceneEpoch,
                    attempt.OutputGeneration,
                    attempt.ImageIndex,
                    attempt.SceneCommandBuffer.Handle,
                    attempt.GraphicsSignalValue,
                    attempt.PresentedSourceFrameId,
                    result,
                    attempt.ReadinessPolicy,
                    attempt.WorkClass);
            }

            if (result == Result.ErrorDeviceLost)
            {
                attempt.Stop(
                    EDesktopFrameReason.PresentDeviceLost,
                    EDesktopFrameRecoveryAction.DeviceLost);
                throw CreateDeviceLostException("QueuePresent", result);
            }

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

        internal unsafe VulkanDesktopPresentDispatchOutcome QueueDesktopPresentCore(
            ref VulkanFrameAttempt attempt,
            string profileScope,
            string? disableFrameGenerationReason)
        {
            ThrowIfDesktopFrameFaultInjected(
                EVulkanDesktopFrameFaultPoint.Presentation);
            Exception? auxiliaryFailure = null;
            bool dispatched = false;
            Semaphore queuedPresentSemaphore = attempt.PresentSemaphore;
            attempt.PresentWaitSemaphoreProvenanceValid =
                queuedPresentSemaphore.Handle != 0 &&
                attempt.ExpectedPresentWaitSemaphore.Handle != 0 &&
                queuedPresentSemaphore.Handle == attempt.ExpectedPresentWaitSemaphore.Handle &&
                attempt.FrameTargetLease.SubmissionSignalSemaphore.Handle == queuedPresentSemaphore.Handle;
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
            attempt.PresentStartedTimestamp = stageStartTimestamp;
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
                        in attempt.PresentReservation,
                        out result,
                        out string failureReason,
                        out Exception? postDispatchFailure,
                        out TimeSpan queueAdmissionWait,
                        out TimeSpan nativePresentElapsed,
                        caller: "RenderFrameCallback");
                attempt.Timing.QueuePresentAdmission += queueAdmissionWait;
                attempt.Timing.NativeQueuePresent += nativePresentElapsed;
                attempt.Timing.RecordCausalWait(new VulkanFrameCausalWait(
                    EVulkanFrameWaitReason.QueuePresentAdmission,
                    queueAdmissionWait,
                    attempt.FrameNumber,
                    attempt.FrameSlot,
                    unchecked((int)attempt.ImageIndex),
                    SemaphoreTargetValue: attempt.GraphicsSignalValue,
                    SemaphoreCompletedValue: 0UL,
                    QueueFamily: _deviceContext.QueueFamilies.PresentFamilyIndex ?? 0U,
                    PendingCommandCount: attempt.Submitted ? 1 : 0,
                    ConcurrentWorkerActivity: Volatile.Read(
                        ref _commandRuntime.Workers.ActiveWorkerCount),
                    Stage: EVulkanFrameStage.OutputComplete));
                attempt.Timing.RecordCausalWait(new VulkanFrameCausalWait(
                    EVulkanFrameWaitReason.NativeQueuePresent,
                    nativePresentElapsed,
                    attempt.FrameNumber,
                    attempt.FrameSlot,
                    unchecked((int)attempt.ImageIndex),
                    SemaphoreTargetValue: attempt.GraphicsSignalValue,
                    SemaphoreCompletedValue: 0UL,
                    QueueFamily: _deviceContext.QueueFamilies.PresentFamilyIndex ?? 0U,
                    PendingCommandCount: attempt.Submitted ? 1 : 0,
                    ConcurrentWorkerActivity: Volatile.Read(
                        ref _commandRuntime.Workers.ActiveWorkerCount),
                    Stage: EVulkanFrameStage.OutputComplete));
                auxiliaryFailure ??= postDispatchFailure;
                if (!dispatched)
                {
                    if (result == Result.ErrorDeviceLost)
                    {
                        attempt.Timing.PresentQueue +=
                            Stopwatch.GetElapsedTime(stageStartTimestamp);
                        attempt.PresentCompletedTimestamp = Stopwatch.GetTimestamp();
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
            attempt.PresentCompletedTimestamp = Stopwatch.GetTimestamp();

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
                OutputRuntime.Desktop.LastPresentedFrameNumber = attempt.FrameNumber;
                return;
            }

            if (OutputRuntime.Desktop.ImageHasValidPresentedContent[attempt.ImageIndex])
                OutputRuntime.Desktop.LastPresentedFrameNumber = attempt.FrameNumber;

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
