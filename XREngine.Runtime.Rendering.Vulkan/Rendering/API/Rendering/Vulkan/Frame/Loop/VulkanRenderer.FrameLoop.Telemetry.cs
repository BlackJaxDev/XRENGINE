using System;
using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        internal void RecordDesktopFrameGap(
            ref VulkanFrameAttempt attempt)
        {
            long previousTimestamp = LastDesktopFrameTickObservedTimestamp;
            if (previousTimestamp == 0)
                return;

            TimeSpan gap = Stopwatch.GetElapsedTime(
                previousTimestamp,
                attempt.StartTimestamp);
            if (gap <= TimeSpan.FromSeconds(5))
                return;

            Debug.VulkanWarning(
                $"[Vulkan] Frame {attempt.FrameNumber}: {gap.TotalSeconds:F1}s gap since the last observed desktop frame tick. " +
                $"Slot={attempt.FrameSlot} SlotTimelineValue={_commandRuntime.Synchronization._frameSlotTimelineValues?[attempt.FrameSlot]}");
        }

        internal void PublishDesktopFrameTelemetry(
            ref VulkanFrameAttempt attempt)
        {
            if (!VulkanDesktopFramePolicy.IsAcquireFinalizationLegal(
                    attempt.AcquireOwnership))
            {
                throw new InvalidOperationException(
                    $"Desktop frame finalized with unresolved acquire ownership {attempt.AcquireOwnership}.");
            }

            if (!VulkanDesktopFramePolicy.IsUploadFinalizationLegal(
                    attempt.UploadOwnership))
            {
                throw new InvalidOperationException(
                    $"Desktop frame finalized with unresolved upload ownership {attempt.UploadOwnership}.");
            }

            TimeSpan totalFrameTime =
                Stopwatch.GetElapsedTime(attempt.StartTimestamp);
            attempt.Timing.SetOutputIdentity(
                unchecked((int)attempt.ImageIndex),
                OutputRuntime.Desktop.Generation);
            attempt.Timing.PublishAfterFrame(
                totalFrameTime,
                ResolveDesktopFrameTelemetryOutcome(ref attempt));
            attempt.AdvanceTo(EDesktopFramePhase.Finalized);
        }

        private static EVulkanFrameOutcome ResolveDesktopFrameTelemetryOutcome(
            ref VulkanFrameAttempt attempt)
        {
            if (attempt.PrimaryFailure is not null || attempt.DeferredFailure is not null)
                return EVulkanFrameOutcome.Failed;

            return attempt.Reason switch
            {
                EDesktopFrameReason.Success or EDesktopFrameReason.PresentSuboptimal =>
                    EVulkanFrameOutcome.Completed,
                EDesktopFrameReason.ZeroSurface or EDesktopFrameReason.FrameSlotBusy =>
                    EVulkanFrameOutcome.Skipped,
                EDesktopFrameReason.ResizePending or
                EDesktopFrameReason.ResourceGenerationBlocked or
                EDesktopFrameReason.FrameGenerationModeChanged or
                EDesktopFrameReason.AcquireNotReady or
                EDesktopFrameReason.AcquireTimeout or
                EDesktopFrameReason.AcquireOutOfDate or
                EDesktopFrameReason.RecordingDeferred or
                EDesktopFrameReason.RecordingResourceRetired or
                EDesktopFrameReason.PresentOutOfDate =>
                    EVulkanFrameOutcome.Deferred,
                EDesktopFrameReason.RecordingDirtied =>
                    EVulkanFrameOutcome.Rejected,
                EDesktopFrameReason.None when attempt.Flow == EDesktopFrameFlow.Completed =>
                    EVulkanFrameOutcome.Completed,
                EDesktopFrameReason.None => EVulkanFrameOutcome.Deferred,
                _ => EVulkanFrameOutcome.Failed,
            };
        }
    }
}
