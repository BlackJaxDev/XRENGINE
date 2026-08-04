using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Pure desktop Vulkan lifecycle policies shared by the current callback and its future phase owners.
/// </summary>
internal static class VulkanDesktopFramePolicy
{
    internal static VulkanDesktopPreflightOutcome ClassifyPreflight(EVulkanDesktopPreflightStatus status)
        => status switch
        {
            EVulkanDesktopPreflightStatus.Ready => new(
                EVulkanDesktopPolicyFlow.Continue,
                EVulkanDesktopPolicyReason.Ready,
                EVulkanDesktopRecoveryDirective.None),
            EVulkanDesktopPreflightStatus.Reentrant => new(
                EVulkanDesktopPolicyFlow.Stop,
                EVulkanDesktopPolicyReason.Reentrant,
                EVulkanDesktopRecoveryDirective.None),
            EVulkanDesktopPreflightStatus.ZeroSurface => new(
                EVulkanDesktopPolicyFlow.Stop,
                EVulkanDesktopPolicyReason.ZeroSurface,
                EVulkanDesktopRecoveryDirective.None),
            EVulkanDesktopPreflightStatus.ResizePending => new(
                EVulkanDesktopPolicyFlow.Stop,
                EVulkanDesktopPolicyReason.ResizePending,
                EVulkanDesktopRecoveryDirective.RecreateSwapchain),
            EVulkanDesktopPreflightStatus.ResourceMismatch => new(
                EVulkanDesktopPolicyFlow.Stop,
                EVulkanDesktopPolicyReason.ResourceMismatch,
                EVulkanDesktopRecoveryDirective.RecreateSwapchain),
            EVulkanDesktopPreflightStatus.InteractiveSlotBusy => new(
                EVulkanDesktopPolicyFlow.Stop,
                EVulkanDesktopPolicyReason.InteractiveSlotBusy,
                EVulkanDesktopRecoveryDirective.None),
            EVulkanDesktopPreflightStatus.SurfaceUnavailable => new(
                EVulkanDesktopPolicyFlow.Faulted,
                EVulkanDesktopPolicyReason.SurfaceUnavailable,
                EVulkanDesktopRecoveryDirective.RestartRenderer),
            _ => new(
                EVulkanDesktopPolicyFlow.Faulted,
                EVulkanDesktopPolicyReason.SurfaceUnavailable,
                EVulkanDesktopRecoveryDirective.RestartRenderer),
        };

    /// <remarks>
    /// Vulkan defines both <see cref="Result.Success"/> and <see cref="Result.SuboptimalKhr"/>
    /// as image-acquiring outcomes. The latter therefore carries an acquired-work obligation
    /// and defers swapchain recreation until that obligation is resolved.
    /// </remarks>
    internal static VulkanDesktopAcquireOutcome ClassifyAcquire(Result result)
        => result switch
        {
            Result.Success => new(
                result,
                EVulkanDesktopPolicyFlow.Continue,
                EVulkanDesktopPolicyReason.AcquireSuccess,
                EVulkanDesktopRecoveryDirective.None,
                EVulkanDesktopAcquireOwnership.AcquiredUnresolved),
            Result.SuboptimalKhr => new(
                result,
                EVulkanDesktopPolicyFlow.Continue,
                EVulkanDesktopPolicyReason.AcquireSuboptimal,
                EVulkanDesktopRecoveryDirective.ResolveAcquiredWorkThenRecreateSwapchain,
                EVulkanDesktopAcquireOwnership.AcquiredUnresolved),
            Result.NotReady => new(
                result,
                EVulkanDesktopPolicyFlow.Stop,
                EVulkanDesktopPolicyReason.AcquireNotReady,
                EVulkanDesktopRecoveryDirective.None,
                EVulkanDesktopAcquireOwnership.None),
            Result.Timeout => new(
                result,
                EVulkanDesktopPolicyFlow.Stop,
                EVulkanDesktopPolicyReason.AcquireTimeout,
                EVulkanDesktopRecoveryDirective.None,
                EVulkanDesktopAcquireOwnership.None),
            Result.ErrorOutOfDateKhr => new(
                result,
                EVulkanDesktopPolicyFlow.Stop,
                EVulkanDesktopPolicyReason.AcquireOutOfDate,
                EVulkanDesktopRecoveryDirective.RecreateSwapchain,
                EVulkanDesktopAcquireOwnership.None),
            Result.ErrorSurfaceLostKhr => new(
                result,
                EVulkanDesktopPolicyFlow.Faulted,
                EVulkanDesktopPolicyReason.AcquireSurfaceLost,
                EVulkanDesktopRecoveryDirective.RestartRenderer,
                EVulkanDesktopAcquireOwnership.None),
            Result.ErrorDeviceLost => new(
                result,
                EVulkanDesktopPolicyFlow.TerminalDeviceLoss,
                EVulkanDesktopPolicyReason.AcquireDeviceLost,
                EVulkanDesktopRecoveryDirective.TerminalDeviceLoss,
                EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss),
            _ => new(
                result,
                EVulkanDesktopPolicyFlow.Faulted,
                EVulkanDesktopPolicyReason.AcquireUnexpected,
                EVulkanDesktopRecoveryDirective.None,
                EVulkanDesktopAcquireOwnership.None),
        };

    internal static VulkanDesktopPresentOutcome ClassifyPresent(Result result)
        => result switch
        {
            Result.Success => new(
                result,
                EVulkanDesktopPolicyFlow.Completed,
                EVulkanDesktopPolicyReason.PresentSuccess,
                EVulkanDesktopRecoveryDirective.None,
                PresentationAccepted: true,
                AdvanceFrameSlot: true),
            Result.SuboptimalKhr => new(
                result,
                EVulkanDesktopPolicyFlow.Completed,
                EVulkanDesktopPolicyReason.PresentSuboptimal,
                EVulkanDesktopRecoveryDirective.RecreateSwapchain,
                PresentationAccepted: true,
                AdvanceFrameSlot: true),
            Result.ErrorOutOfDateKhr => new(
                result,
                EVulkanDesktopPolicyFlow.Completed,
                EVulkanDesktopPolicyReason.PresentOutOfDate,
                EVulkanDesktopRecoveryDirective.RecreateSwapchain,
                PresentationAccepted: false,
                AdvanceFrameSlot: true),
            Result.ErrorSurfaceLostKhr => new(
                result,
                EVulkanDesktopPolicyFlow.Faulted,
                EVulkanDesktopPolicyReason.PresentSurfaceLost,
                EVulkanDesktopRecoveryDirective.RestartRenderer,
                PresentationAccepted: false,
                AdvanceFrameSlot: true),
            Result.ErrorDeviceLost => new(
                result,
                EVulkanDesktopPolicyFlow.TerminalDeviceLoss,
                EVulkanDesktopPolicyReason.PresentDeviceLost,
                EVulkanDesktopRecoveryDirective.TerminalDeviceLoss,
                PresentationAccepted: false,
                AdvanceFrameSlot: false),
            _ => new(
                result,
                EVulkanDesktopPolicyFlow.Faulted,
                EVulkanDesktopPolicyReason.PresentUnexpected,
                EVulkanDesktopRecoveryDirective.RestartRenderer,
                PresentationAccepted: false,
                AdvanceFrameSlot: true),
        };

    internal static VulkanDesktopRecoveryOutcome ResolvePostAcquireFailure(
        EVulkanDesktopPostAcquireFailureStage stage,
        bool deviceLost,
        bool recreateSwapchainAfterResolution)
    {
        EVulkanDesktopPolicyReason reason = GetFailureReason(stage);
        if (deviceLost)
        {
            return new(
                EVulkanDesktopPolicyFlow.TerminalDeviceLoss,
                reason,
                EVulkanDesktopRecoveryDirective.TerminalDeviceLoss,
                EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss,
                EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss,
                MustSettlePresentation: false,
                AdvanceFrameSlotAfterSettlement: false);
        }

        if (stage == EVulkanDesktopPostAcquireFailureStage.PostPresentAuxiliary)
        {
            return new(
                EVulkanDesktopPolicyFlow.Faulted,
                reason,
                EVulkanDesktopRecoveryDirective.None,
                EVulkanDesktopAcquireOwnership.ResolvedByPresentation,
                EVulkanDesktopUploadOwnership.SubmittedDeferredFree,
                MustSettlePresentation: false,
                AdvanceFrameSlotAfterSettlement: true);
        }

        if (stage == EVulkanDesktopPostAcquireFailureStage.PostSubmitAuxiliary)
        {
            return new(
                EVulkanDesktopPolicyFlow.Faulted,
                reason,
                EVulkanDesktopRecoveryDirective.None,
                EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent,
                EVulkanDesktopUploadOwnership.SubmittedDeferredFree,
                MustSettlePresentation: true,
                AdvanceFrameSlotAfterSettlement: true);
        }

        EVulkanDesktopRecoveryDirective directive = recreateSwapchainAfterResolution
            ? EVulkanDesktopRecoveryDirective.ResolveAcquiredWorkThenRecreateSwapchain
            : EVulkanDesktopRecoveryDirective.ResolveAcquiredWork;
        return new(
            EVulkanDesktopPolicyFlow.Faulted,
            reason,
            directive,
            EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent,
            EVulkanDesktopUploadOwnership.CancelledFreed,
            MustSettlePresentation: true,
            AdvanceFrameSlotAfterSettlement: true);
    }

    internal static bool TryTransitionAcquireOwnership(
        EVulkanDesktopAcquireOwnership current,
        EVulkanDesktopAcquireOwnership next)
        => current switch
        {
            EVulkanDesktopAcquireOwnership.None =>
                next is EVulkanDesktopAcquireOwnership.AcquiredUnresolved
                    or EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss,
            EVulkanDesktopAcquireOwnership.AcquiredUnresolved =>
                next is EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent
                    or EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent
                    or EVulkanDesktopAcquireOwnership.ResolvedBySwapchainInvalidation
                    or EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss,
            EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent
                or EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent =>
                next is EVulkanDesktopAcquireOwnership.ResolvedByPresentation
                    or EVulkanDesktopAcquireOwnership.ResolvedBySwapchainInvalidation
                    or EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss,
            _ => false,
        };

    internal static bool TryTransitionUploadOwnership(
        EVulkanDesktopUploadOwnership current,
        EVulkanDesktopUploadOwnership next)
        => current switch
        {
            EVulkanDesktopUploadOwnership.None =>
                next is EVulkanDesktopUploadOwnership.Recorded
                    or EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss,
            EVulkanDesktopUploadOwnership.Recorded =>
                next is EVulkanDesktopUploadOwnership.SubmittedDeferredFree
                    or EVulkanDesktopUploadOwnership.CancelledFreed
                    or EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss,
            EVulkanDesktopUploadOwnership.SubmittedDeferredFree =>
                next is EVulkanDesktopUploadOwnership.Retired
                    or EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss,
            _ => false,
        };

    internal static bool IsAcquireFinalizationLegal(EVulkanDesktopAcquireOwnership ownership)
        => ownership is EVulkanDesktopAcquireOwnership.None
            or EVulkanDesktopAcquireOwnership.ResolvedByPresentation
            or EVulkanDesktopAcquireOwnership.ResolvedBySwapchainInvalidation
            or EVulkanDesktopAcquireOwnership.IndeterminateAfterDeviceLoss;

    internal static bool IsUploadFinalizationLegal(EVulkanDesktopUploadOwnership ownership)
        => ownership is EVulkanDesktopUploadOwnership.None
            or EVulkanDesktopUploadOwnership.Retired
            or EVulkanDesktopUploadOwnership.CancelledFreed
            or EVulkanDesktopUploadOwnership.AbandonedAfterDeviceLoss;

    internal static bool IsLegalPhaseTransition(
        EDesktopFramePhase current,
        EDesktopFramePhase next)
    {
        if (next == EDesktopFramePhase.Finalized)
            return current != EDesktopFramePhase.Finalized;

        return current switch
        {
            EDesktopFramePhase.Entered =>
                next == EDesktopFramePhase.PreflightComplete,
            EDesktopFramePhase.PreflightComplete =>
                next == EDesktopFramePhase.SlotReady,
            EDesktopFramePhase.SlotReady =>
                next == EDesktopFramePhase.ImageAcquired,
            EDesktopFramePhase.ImageAcquired =>
                next is EDesktopFramePhase.ImageReady or
                    EDesktopFramePhase.Recovered,
            EDesktopFramePhase.ImageReady =>
                next is EDesktopFramePhase.Recorded or
                    EDesktopFramePhase.Recovered,
            EDesktopFramePhase.Recorded =>
                next is EDesktopFramePhase.Validated or
                    EDesktopFramePhase.Recovered,
            EDesktopFramePhase.Validated =>
                next is EDesktopFramePhase.Submitted or
                    EDesktopFramePhase.Recovered,
            EDesktopFramePhase.Submitted =>
                next is EDesktopFramePhase.Presented or
                    EDesktopFramePhase.Recovered,
            _ => false,
        };
    }

    private static EVulkanDesktopPolicyReason GetFailureReason(EVulkanDesktopPostAcquireFailureStage stage)
        => stage switch
        {
            EVulkanDesktopPostAcquireFailureStage.ImagePreparation =>
                EVulkanDesktopPolicyReason.ImagePreparationFailed,
            EVulkanDesktopPostAcquireFailureStage.Recording =>
                EVulkanDesktopPolicyReason.RecordingFailed,
            EVulkanDesktopPostAcquireFailureStage.Submission =>
                EVulkanDesktopPolicyReason.SubmissionFailed,
            EVulkanDesktopPostAcquireFailureStage.PostSubmitAuxiliary =>
                EVulkanDesktopPolicyReason.PostSubmitAuxiliaryFailed,
            EVulkanDesktopPostAcquireFailureStage.PostPresentAuxiliary =>
                EVulkanDesktopPolicyReason.PostPresentAuxiliaryFailed,
            _ => EVulkanDesktopPolicyReason.RecordingFailed,
        };
}
