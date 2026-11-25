namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    /// <summary>
    /// Settles a failed required world UI output without ever presenting its image.
    /// A submitted image keeps its GPU claims behind the normal submission fence.
    /// </summary>
    private EDesktopFrameFlow SettleFailedWorldUiOutput(
        ref VulkanFrameAttempt attempt,
        string reason)
    {
        attempt.AcceptedFramePlan?.FailRequiredWorldUiOutput();
        if (_deviceLost)
        {
            AbandonDesktopOwnershipAfterDeviceLoss(ref attempt);
            attempt.Stop(EDesktopFrameReason.PresentDeviceLost,
                EDesktopFrameRecoveryAction.DeviceLost);
            return EDesktopFrameFlow.Stop;
        }

        if (attempt.PresentReservation.Owner is { } completion &&
            completion.IsQuarantined(in attempt.PresentReservation))
        {
            QuarantineDesktopFrameAdmission(ref attempt,
                $"Required world UI present has uncertain native ownership: {reason}");
            attempt.Stop(EDesktopFrameReason.PresentUnexpectedFailure);
            return EDesktopFrameFlow.Stop;
        }

        try
        {
            if (attempt.AcquireOwnership ==
                EVulkanDesktopAcquireOwnership.AcquiredUnresolved &&
                !ConsumeDesktopAcquireForRecovery(ref attempt, reason))
            {
                QuarantineDesktopFrameAdmission(ref attempt,
                    $"Required world UI output failed and its acquire semaphore could not be consumed: {reason}");
                attempt.Stop(EDesktopFrameReason.RecordingFailed);
                return EDesktopFrameFlow.Stop;
            }

            if (attempt.AcquireOwnership is
                EVulkanDesktopAcquireOwnership.ConsumedBySubmissionImagePendingPresent or
                EVulkanDesktopAcquireOwnership.ConsumedByRecoveryImagePendingPresent)
            {
                // The acquire semaphore was already consumed. Never wait it again.
                ResolveDesktopAcquireBySwapchainRecreation(ref attempt, reason);
            }

            CompleteDesktopFrameSlot(ref attempt);
            ReleaseCollectForFailureSettlement(ref attempt);
            if (!attempt.Submitted)
            {
                ReleaseUnsubmittedDesktopUpload(ref attempt, reason);
                SettleRejectedDesktopCommandArtifacts(ref attempt, reason);
            }
            attempt.Stop(attempt.Submitted
                    ? EDesktopFrameReason.PresentUnexpectedFailure
                    : EDesktopFrameReason.RecordingFailed,
                EDesktopFrameRecoveryAction.RecreateSwapchain);
            return EDesktopFrameFlow.Stop;
        }
        catch
        {
            QuarantineDesktopFrameAdmission(ref attempt,
                $"Required world UI output could not settle its acquired image: {reason}");
            throw;
        }
    }
}
