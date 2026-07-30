using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
internal sealed class VulkanDesktopFrameLoopPolicyTests
{
    [Test]
    public void PhaseTransitions_AllowOnlyDeclaredLifecycleEdges()
    {
        foreach (EDesktopFramePhase current in
                 Enum.GetValues<EDesktopFramePhase>())
        {
            foreach (EDesktopFramePhase next in
                     Enum.GetValues<EDesktopFramePhase>())
            {
                bool expected =
                    next == EDesktopFramePhase.Finalized
                        ? current !=
                          EDesktopFramePhase.Finalized
                        : (current, next) switch
                        {
                            (EDesktopFramePhase.Entered,
                                EDesktopFramePhase
                                    .PreflightComplete) => true,
                            (EDesktopFramePhase
                                    .PreflightComplete,
                                EDesktopFramePhase
                                    .SlotReady) => true,
                            (EDesktopFramePhase.SlotReady,
                                EDesktopFramePhase
                                    .ImageAcquired) => true,
                            (EDesktopFramePhase.ImageAcquired,
                                EDesktopFramePhase.ImageReady
                                or EDesktopFramePhase
                                    .Recovered) => true,
                            (EDesktopFramePhase.ImageReady,
                                EDesktopFramePhase.Recorded
                                or EDesktopFramePhase
                                    .Recovered) => true,
                            (EDesktopFramePhase.Recorded,
                                EDesktopFramePhase.Validated
                                or EDesktopFramePhase
                                    .Recovered) => true,
                            (EDesktopFramePhase.Validated,
                                EDesktopFramePhase.Submitted
                                or EDesktopFramePhase
                                    .Recovered) => true,
                            (EDesktopFramePhase.Submitted,
                                EDesktopFramePhase.Presented
                                or EDesktopFramePhase
                                    .Recovered) => true,
                            _ => false,
                        };

                VulkanDesktopFramePolicy.IsLegalPhaseTransition(
                        current,
                        next)
                    .ShouldBe(expected);
            }
        }
    }

    [TestCase(
        EVulkanDesktopPreflightStatus.Ready,
        EVulkanDesktopPolicyFlow.Continue,
        EVulkanDesktopPolicyReason.Ready,
        EVulkanDesktopRecoveryDirective.None,
        true)]
    [TestCase(
        EVulkanDesktopPreflightStatus.Reentrant,
        EVulkanDesktopPolicyFlow.Stop,
        EVulkanDesktopPolicyReason.Reentrant,
        EVulkanDesktopRecoveryDirective.None,
        false)]
    [TestCase(
        EVulkanDesktopPreflightStatus.ZeroSurface,
        EVulkanDesktopPolicyFlow.Stop,
        EVulkanDesktopPolicyReason.ZeroSurface,
        EVulkanDesktopRecoveryDirective.None,
        false)]
    [TestCase(
        EVulkanDesktopPreflightStatus.ResizePending,
        EVulkanDesktopPolicyFlow.Stop,
        EVulkanDesktopPolicyReason.ResizePending,
        EVulkanDesktopRecoveryDirective.RecreateSwapchain,
        false)]
    [TestCase(
        EVulkanDesktopPreflightStatus.ResourceMismatch,
        EVulkanDesktopPolicyFlow.Stop,
        EVulkanDesktopPolicyReason.ResourceMismatch,
        EVulkanDesktopRecoveryDirective.RecreateSwapchain,
        false)]
    [TestCase(
        EVulkanDesktopPreflightStatus.InteractiveSlotBusy,
        EVulkanDesktopPolicyFlow.Stop,
        EVulkanDesktopPolicyReason.InteractiveSlotBusy,
        EVulkanDesktopRecoveryDirective.None,
        false)]
    [TestCase(
        EVulkanDesktopPreflightStatus.SurfaceUnavailable,
        EVulkanDesktopPolicyFlow.Faulted,
        EVulkanDesktopPolicyReason.SurfaceUnavailable,
        EVulkanDesktopRecoveryDirective.RestartRenderer,
        false)]
    public void PreflightClassification_IsDeterministic(
        EVulkanDesktopPreflightStatus status,
        EVulkanDesktopPolicyFlow expectedFlow,
        EVulkanDesktopPolicyReason expectedReason,
        EVulkanDesktopRecoveryDirective expectedRecovery,
        bool expectedCanAcquire)
    {
        VulkanDesktopPreflightOutcome outcome = VulkanDesktopFramePolicy.ClassifyPreflight(status);

        outcome.Flow.ShouldBe(expectedFlow);
        outcome.Reason.ShouldBe(expectedReason);
        outcome.RecoveryDirective.ShouldBe(expectedRecovery);
        outcome.CanAcquire.ShouldBe(expectedCanAcquire);
    }

    [TestCase(
        Result.Success,
        EVulkanDesktopPolicyFlow.Continue,
        EVulkanDesktopPolicyReason.AcquireSuccess,
        EVulkanDesktopRecoveryDirective.None,
        EVulkanDesktopAcquireOwnership.AcquiredUnresolved,
        true,
        false)]
    [TestCase(
        Result.SuboptimalKhr,
        EVulkanDesktopPolicyFlow.Continue,
        EVulkanDesktopPolicyReason.AcquireSuboptimal,
        EVulkanDesktopRecoveryDirective.ResolveAcquiredWorkThenRecreateSwapchain,
        EVulkanDesktopAcquireOwnership.AcquiredUnresolved,
        true,
        false)]
    [TestCase(
        Result.NotReady,
        EVulkanDesktopPolicyFlow.Stop,
        EVulkanDesktopPolicyReason.AcquireNotReady,
        EVulkanDesktopRecoveryDirective.None,
        EVulkanDesktopAcquireOwnership.None,
        false,
        true)]
    [TestCase(
        Result.Timeout,
        EVulkanDesktopPolicyFlow.Stop,
        EVulkanDesktopPolicyReason.AcquireTimeout,
        EVulkanDesktopRecoveryDirective.None,
        EVulkanDesktopAcquireOwnership.None,
        false,
        true)]
    [TestCase(
        Result.ErrorOutOfDateKhr,
        EVulkanDesktopPolicyFlow.Stop,
        EVulkanDesktopPolicyReason.AcquireOutOfDate,
        EVulkanDesktopRecoveryDirective.RecreateSwapchain,
        EVulkanDesktopAcquireOwnership.None,
        false,
        false)]
    [TestCase(
        Result.ErrorSurfaceLostKhr,
        EVulkanDesktopPolicyFlow.Faulted,
        EVulkanDesktopPolicyReason.AcquireSurfaceLost,
        EVulkanDesktopRecoveryDirective.RestartRenderer,
        EVulkanDesktopAcquireOwnership.None,
        false,
        false)]
    [TestCase(
        Result.ErrorDeviceLost,
        EVulkanDesktopPolicyFlow.TerminalDeviceLoss,
        EVulkanDesktopPolicyReason.AcquireDeviceLost,
        EVulkanDesktopRecoveryDirective.TerminalDeviceLoss,
        EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss,
        false,
        false)]
    [TestCase(
        Result.ErrorOutOfHostMemory,
        EVulkanDesktopPolicyFlow.Faulted,
        EVulkanDesktopPolicyReason.AcquireUnexpected,
        EVulkanDesktopRecoveryDirective.None,
        EVulkanDesktopAcquireOwnership.None,
        false,
        false)]
    public void AcquireClassification_PreservesImageOwnership(
        Result result,
        EVulkanDesktopPolicyFlow expectedFlow,
        EVulkanDesktopPolicyReason expectedReason,
        EVulkanDesktopRecoveryDirective expectedRecovery,
        EVulkanDesktopAcquireOwnership expectedOwnership,
        bool expectedImageAcquired,
        bool expectedTransientSkip)
    {
        VulkanDesktopAcquireOutcome outcome = VulkanDesktopFramePolicy.ClassifyAcquire(result);

        outcome.Result.ShouldBe(result);
        outcome.Flow.ShouldBe(expectedFlow);
        outcome.Reason.ShouldBe(expectedReason);
        outcome.RecoveryDirective.ShouldBe(expectedRecovery);
        outcome.Ownership.ShouldBe(expectedOwnership);
        outcome.ImageAcquired.ShouldBe(expectedImageAcquired);
        outcome.IsTransientSkip.ShouldBe(expectedTransientSkip);
    }

    [TestCase(
        Result.Success,
        EVulkanDesktopPolicyFlow.Completed,
        EVulkanDesktopPolicyReason.PresentSuccess,
        EVulkanDesktopRecoveryDirective.None,
        true,
        true)]
    [TestCase(
        Result.SuboptimalKhr,
        EVulkanDesktopPolicyFlow.Completed,
        EVulkanDesktopPolicyReason.PresentSuboptimal,
        EVulkanDesktopRecoveryDirective.RecreateSwapchain,
        true,
        true)]
    [TestCase(
        Result.ErrorOutOfDateKhr,
        EVulkanDesktopPolicyFlow.Completed,
        EVulkanDesktopPolicyReason.PresentOutOfDate,
        EVulkanDesktopRecoveryDirective.RecreateSwapchain,
        false,
        true)]
    [TestCase(
        Result.ErrorSurfaceLostKhr,
        EVulkanDesktopPolicyFlow.Faulted,
        EVulkanDesktopPolicyReason.PresentSurfaceLost,
        EVulkanDesktopRecoveryDirective.RestartRenderer,
        false,
        true)]
    [TestCase(
        Result.ErrorDeviceLost,
        EVulkanDesktopPolicyFlow.TerminalDeviceLoss,
        EVulkanDesktopPolicyReason.PresentDeviceLost,
        EVulkanDesktopRecoveryDirective.TerminalDeviceLoss,
        false,
        false)]
    [TestCase(
        Result.NotReady,
        EVulkanDesktopPolicyFlow.Faulted,
        EVulkanDesktopPolicyReason.PresentUnexpected,
        EVulkanDesktopRecoveryDirective.RestartRenderer,
        false,
        true)]
    [TestCase(
        Result.Timeout,
        EVulkanDesktopPolicyFlow.Faulted,
        EVulkanDesktopPolicyReason.PresentUnexpected,
        EVulkanDesktopRecoveryDirective.RestartRenderer,
        false,
        true)]
    public void PresentClassification_SettlesSubmittedSlotExactlyOnce(
        Result result,
        EVulkanDesktopPolicyFlow expectedFlow,
        EVulkanDesktopPolicyReason expectedReason,
        EVulkanDesktopRecoveryDirective expectedRecovery,
        bool expectedAccepted,
        bool expectedAdvance)
    {
        VulkanDesktopPresentOutcome outcome = VulkanDesktopFramePolicy.ClassifyPresent(result);

        outcome.Result.ShouldBe(result);
        outcome.Flow.ShouldBe(expectedFlow);
        outcome.Reason.ShouldBe(expectedReason);
        outcome.RecoveryDirective.ShouldBe(expectedRecovery);
        outcome.PresentationAccepted.ShouldBe(expectedAccepted);
        outcome.AdvanceFrameSlot.ShouldBe(expectedAdvance);
    }

    [TestCase(
        EVulkanDesktopPostAcquireFailureStage.ImagePreparation,
        false,
        EVulkanDesktopRecoveryDirective.ResolveAcquiredWork,
        EVulkanDesktopPolicyReason.ImagePreparationFailed,
        EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent,
        EVulkanDesktopUploadOwnership.CancelledFreed,
        true,
        true)]
    [TestCase(
        EVulkanDesktopPostAcquireFailureStage.Recording,
        true,
        EVulkanDesktopRecoveryDirective.ResolveAcquiredWorkThenRecreateSwapchain,
        EVulkanDesktopPolicyReason.RecordingFailed,
        EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent,
        EVulkanDesktopUploadOwnership.CancelledFreed,
        true,
        true)]
    [TestCase(
        EVulkanDesktopPostAcquireFailureStage.Submission,
        false,
        EVulkanDesktopRecoveryDirective.ResolveAcquiredWork,
        EVulkanDesktopPolicyReason.SubmissionFailed,
        EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent,
        EVulkanDesktopUploadOwnership.CancelledFreed,
        true,
        true)]
    [TestCase(
        EVulkanDesktopPostAcquireFailureStage.PostSubmitAuxiliary,
        false,
        EVulkanDesktopRecoveryDirective.None,
        EVulkanDesktopPolicyReason.PostSubmitAuxiliaryFailed,
        EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent,
        EVulkanDesktopUploadOwnership.SubmittedDeferredFree,
        true,
        true)]
    [TestCase(
        EVulkanDesktopPostAcquireFailureStage.PostPresentAuxiliary,
        false,
        EVulkanDesktopRecoveryDirective.None,
        EVulkanDesktopPolicyReason.PostPresentAuxiliaryFailed,
        EVulkanDesktopAcquireOwnership.ResolvedByPresentation,
        EVulkanDesktopUploadOwnership.SubmittedDeferredFree,
        false,
        true)]
    public void HealthyPostAcquireFailure_RequiresExplicitSettlement(
        EVulkanDesktopPostAcquireFailureStage stage,
        bool recreateAfterResolution,
        EVulkanDesktopRecoveryDirective expectedRecovery,
        EVulkanDesktopPolicyReason expectedReason,
        EVulkanDesktopAcquireOwnership expectedAcquire,
        EVulkanDesktopUploadOwnership expectedUpload,
        bool expectedSettlePresentation,
        bool expectedAdvance)
    {
        VulkanDesktopRecoveryOutcome outcome = VulkanDesktopFramePolicy.ResolvePostAcquireFailure(
            stage,
            deviceLost: false,
            recreateAfterResolution);

        outcome.Flow.ShouldBe(EVulkanDesktopPolicyFlow.Faulted);
        outcome.Reason.ShouldBe(expectedReason);
        outcome.RecoveryDirective.ShouldBe(expectedRecovery);
        outcome.RequiredAcquireOwnership.ShouldBe(expectedAcquire);
        outcome.RequiredUploadOwnership.ShouldBe(expectedUpload);
        outcome.MustSettlePresentation.ShouldBe(expectedSettlePresentation);
        outcome.AdvanceFrameSlotAfterSettlement.ShouldBe(expectedAdvance);
    }

    [TestCase(EVulkanDesktopPostAcquireFailureStage.ImagePreparation)]
    [TestCase(EVulkanDesktopPostAcquireFailureStage.Recording)]
    [TestCase(EVulkanDesktopPostAcquireFailureStage.Submission)]
    [TestCase(EVulkanDesktopPostAcquireFailureStage.PostSubmitAuxiliary)]
    [TestCase(EVulkanDesktopPostAcquireFailureStage.PostPresentAuxiliary)]
    public void DeviceLoss_NeverRequiresFurtherGpuRecovery(EVulkanDesktopPostAcquireFailureStage stage)
    {
        VulkanDesktopRecoveryOutcome outcome = VulkanDesktopFramePolicy.ResolvePostAcquireFailure(
            stage,
            deviceLost: true,
            recreateSwapchainAfterResolution: true);

        outcome.Flow.ShouldBe(EVulkanDesktopPolicyFlow.TerminalDeviceLoss);
        outcome.RecoveryDirective.ShouldBe(EVulkanDesktopRecoveryDirective.TerminalDeviceLoss);
        outcome.RequiredAcquireOwnership.ShouldBe(EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss);
        outcome.RequiredUploadOwnership.ShouldBe(EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss);
        outcome.MustSettlePresentation.ShouldBeFalse();
        outcome.AdvanceFrameSlotAfterSettlement.ShouldBeFalse();
    }

    [TestCase(EVulkanDesktopAcquireOwnership.None, EVulkanDesktopAcquireOwnership.AcquiredUnresolved)]
    [TestCase(EVulkanDesktopAcquireOwnership.None, EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss)]
    [TestCase(EVulkanDesktopAcquireOwnership.AcquiredUnresolved, EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent)]
    [TestCase(EVulkanDesktopAcquireOwnership.AcquiredUnresolved, EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent)]
    [TestCase(EVulkanDesktopAcquireOwnership.AcquiredUnresolved, EVulkanDesktopAcquireOwnership.ResolvedBySwapchainInvalidation)]
    [TestCase(EVulkanDesktopAcquireOwnership.AcquiredUnresolved, EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss)]
    [TestCase(EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent, EVulkanDesktopAcquireOwnership.ResolvedByPresentation)]
    [TestCase(EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent, EVulkanDesktopAcquireOwnership.ResolvedByPresentation)]
    [TestCase(EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent, EVulkanDesktopAcquireOwnership.ResolvedBySwapchainInvalidation)]
    [TestCase(EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent, EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss)]
    public void AcquireOwnership_AllowsLegalTransitions(
        EVulkanDesktopAcquireOwnership current,
        EVulkanDesktopAcquireOwnership next)
        => VulkanDesktopFramePolicy.TryTransitionAcquireOwnership(current, next).ShouldBeTrue();

    [TestCase(EVulkanDesktopAcquireOwnership.None, EVulkanDesktopAcquireOwnership.ResolvedByPresentation)]
    [TestCase(EVulkanDesktopAcquireOwnership.AcquiredUnresolved, EVulkanDesktopAcquireOwnership.ResolvedByPresentation)]
    [TestCase(EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent, EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent)]
    [TestCase(EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent, EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent)]
    [TestCase(EVulkanDesktopAcquireOwnership.ResolvedByPresentation, EVulkanDesktopAcquireOwnership.AcquiredUnresolved)]
    [TestCase(EVulkanDesktopAcquireOwnership.ResolvedBySwapchainInvalidation, EVulkanDesktopAcquireOwnership.ResolvedByPresentation)]
    [TestCase(EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss, EVulkanDesktopAcquireOwnership.ResolvedByPresentation)]
    public void AcquireOwnership_RejectsUnresolvedDoubleConsumeAndTerminalReuse(
        EVulkanDesktopAcquireOwnership current,
        EVulkanDesktopAcquireOwnership next)
        => VulkanDesktopFramePolicy.TryTransitionAcquireOwnership(current, next).ShouldBeFalse();

    [TestCase(EVulkanDesktopUploadOwnership.None, EVulkanDesktopUploadOwnership.Recorded)]
    [TestCase(EVulkanDesktopUploadOwnership.None, EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss)]
    [TestCase(EVulkanDesktopUploadOwnership.Recorded, EVulkanDesktopUploadOwnership.SubmittedDeferredFree)]
    [TestCase(EVulkanDesktopUploadOwnership.Recorded, EVulkanDesktopUploadOwnership.CancelledFreed)]
    [TestCase(EVulkanDesktopUploadOwnership.Recorded, EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss)]
    [TestCase(EVulkanDesktopUploadOwnership.SubmittedDeferredFree, EVulkanDesktopUploadOwnership.Retired)]
    [TestCase(EVulkanDesktopUploadOwnership.SubmittedDeferredFree, EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss)]
    public void UploadOwnership_AllowsLegalTransitions(
        EVulkanDesktopUploadOwnership current,
        EVulkanDesktopUploadOwnership next)
        => VulkanDesktopFramePolicy.TryTransitionUploadOwnership(current, next).ShouldBeTrue();

    [TestCase(EVulkanDesktopUploadOwnership.None, EVulkanDesktopUploadOwnership.Retired)]
    [TestCase(EVulkanDesktopUploadOwnership.Recorded, EVulkanDesktopUploadOwnership.Retired)]
    [TestCase(EVulkanDesktopUploadOwnership.SubmittedDeferredFree, EVulkanDesktopUploadOwnership.CancelledFreed)]
    [TestCase(EVulkanDesktopUploadOwnership.CancelledFreed, EVulkanDesktopUploadOwnership.SubmittedDeferredFree)]
    [TestCase(EVulkanDesktopUploadOwnership.Retired, EVulkanDesktopUploadOwnership.Recorded)]
    [TestCase(EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss, EVulkanDesktopUploadOwnership.Retired)]
    public void UploadOwnership_RejectsDoubleCleanupAndTerminalReuse(
        EVulkanDesktopUploadOwnership current,
        EVulkanDesktopUploadOwnership next)
        => VulkanDesktopFramePolicy.TryTransitionUploadOwnership(current, next).ShouldBeFalse();

    [TestCase(EVulkanDesktopAcquireOwnership.None, true)]
    [TestCase(EVulkanDesktopAcquireOwnership.AcquiredUnresolved, false)]
    [TestCase(EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent, false)]
    [TestCase(EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent, false)]
    [TestCase(EVulkanDesktopAcquireOwnership.ResolvedByPresentation, true)]
    [TestCase(EVulkanDesktopAcquireOwnership.ResolvedBySwapchainInvalidation, true)]
    [TestCase(EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss, true)]
    public void AcquireFinalization_RejectsUnresolvedWork(
        EVulkanDesktopAcquireOwnership ownership,
        bool expectedLegal)
        => VulkanDesktopFramePolicy.IsAcquireFinalizationLegal(ownership).ShouldBe(expectedLegal);

    [TestCase(EVulkanDesktopUploadOwnership.None, true)]
    [TestCase(EVulkanDesktopUploadOwnership.Recorded, false)]
    [TestCase(EVulkanDesktopUploadOwnership.SubmittedDeferredFree, false)]
    [TestCase(EVulkanDesktopUploadOwnership.Retired, true)]
    [TestCase(EVulkanDesktopUploadOwnership.CancelledFreed, true)]
    [TestCase(EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss, true)]
    public void UploadFinalization_RejectsUnresolvedWork(
        EVulkanDesktopUploadOwnership ownership,
        bool expectedLegal)
        => VulkanDesktopFramePolicy.IsUploadFinalizationLegal(ownership).ShouldBe(expectedLegal);

    [Test]
    public void PolicyResults_AreReferenceFreeAndClassifiersAllocateNothing()
    {
        RuntimeHelpers.IsReferenceOrContainsReferences<VulkanDesktopPreflightOutcome>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<VulkanDesktopAcquireOutcome>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<VulkanDesktopPresentOutcome>().ShouldBeFalse();
        RuntimeHelpers.IsReferenceOrContainsReferences<VulkanDesktopRecoveryOutcome>().ShouldBeFalse();

        _ = VulkanDesktopFramePolicy.ClassifyPreflight(EVulkanDesktopPreflightStatus.Ready);
        _ = VulkanDesktopFramePolicy.ClassifyAcquire(Result.Success);
        _ = VulkanDesktopFramePolicy.ClassifyPresent(Result.Success);
        _ = VulkanDesktopFramePolicy.ResolvePostAcquireFailure(
            EVulkanDesktopPostAcquireFailureStage.Recording,
            deviceLost: false,
            recreateSwapchainAfterResolution: false);

        int checksum = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10_000; index++)
        {
            checksum += (int)VulkanDesktopFramePolicy
                .ClassifyPreflight(EVulkanDesktopPreflightStatus.Ready)
                .Reason;
            checksum += (int)VulkanDesktopFramePolicy
                .ClassifyAcquire(Result.SuboptimalKhr)
                .Reason;
            checksum += (int)VulkanDesktopFramePolicy
                .ClassifyPresent(Result.ErrorOutOfDateKhr)
                .Reason;
            checksum += (int)VulkanDesktopFramePolicy
                .ResolvePostAcquireFailure(
                    EVulkanDesktopPostAcquireFailureStage.PostSubmitAuxiliary,
                    deviceLost: false,
                    recreateSwapchainAfterResolution: false)
                .Reason;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        allocated.ShouldBe(0);
        checksum.ShouldBeGreaterThan(0);
    }
}
