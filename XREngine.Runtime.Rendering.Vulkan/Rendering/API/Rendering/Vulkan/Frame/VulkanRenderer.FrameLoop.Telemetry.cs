using System;
using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private void RecordDesktopFrameGap(
            ref VulkanFrameAttempt attempt)
        {
            long previousTimestamp =
                Volatile.Read(
                    ref _lastDesktopFrameTickObservedTimestamp);
            if (previousTimestamp == 0)
                return;

            TimeSpan gap = Stopwatch.GetElapsedTime(
                previousTimestamp,
                attempt.StartTimestamp);
            if (gap <= TimeSpan.FromSeconds(5))
                return;

            Debug.VulkanWarning(
                $"[Vulkan] Frame {attempt.FrameNumber}: {gap.TotalSeconds:F1}s gap since the last observed desktop frame tick. " +
                $"Slot={attempt.FrameSlot} SlotTimelineValue={_frameSlotTimelineValues?[attempt.FrameSlot]}");
        }

        private void PublishDesktopFrameTelemetry(
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
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanFrameLifecycleTiming(
                    attempt.Timing.WaitFrameSlot,
                    attempt.Timing.AcquireImage,
                    attempt.Timing.RecordCommandBuffer,
                    attempt.Timing.SubmitQueue,
                    attempt.Timing.TrimStaging,
                    attempt.Timing.PresentQueue,
                    totalFrameTime);
            RuntimeEngine.Rendering.Stats.Vulkan
                .RecordVulkanFrameLifecycleDetailTiming(
                    attempt.Timing.SampleTimingQueries,
                    attempt.Timing.DrainRetiredResources,
                    attempt.Timing.AcquireBridgeSubmit,
                    attempt.Timing.WaitSwapchainImage,
                    attempt.Timing.ResetDynamicUniformRing,
                    attempt.Timing.SnapshotImGuiOverlay,
                    attempt.Timing.RecordSceneCommandBuffer,
                    attempt.Timing.RecordImGuiOverlay,
                    attempt.Timing.RecordDynamicUiTextOverlay);
            attempt.AdvanceTo(EDesktopFramePhase.Finalized);
        }
    }
}
