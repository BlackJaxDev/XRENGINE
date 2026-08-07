using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns bounded desktop/capture-output queues and capture readiness.</summary>
internal sealed class VulkanCaptureOutputState(int readbackSlotCount)
{
    internal VulkanScreenshotReadbackSlot?[] ScreenshotReadbackSlots { get; } =
        new VulkanScreenshotReadbackSlot?[readbackSlotCount];
    internal int ScreenshotReadbackCursor;
    internal long ScreenshotReadbackReservedRawBytes;
    internal long ScreenshotReadbackQueuedCount;
    internal long ScreenshotReadbackCompletedCount;
    internal long ScreenshotReadbackFailedCount;
    internal long ScreenshotReadbackRejectedCount;
    internal long ScreenshotReadbackTimeoutCount;
    internal bool ObsHookDeviceCaptureReady;
    internal string? ObsHookDeviceCaptureFailure;

    internal void FailPendingScreenshotReadbacksForDeviceLoss(string reason)
    {
        for (int index = 0; index < ScreenshotReadbackSlots.Length; index++)
        {
            VulkanScreenshotReadbackSlot? slot = ScreenshotReadbackSlots[index];
            if (slot is null ||
                Interlocked.CompareExchange(
                    ref slot.State,
                    (int)EVulkanScreenshotReadbackSlotState.Abandoned,
                    (int)EVulkanScreenshotReadbackSlotState.Submitted) !=
                (int)EVulkanScreenshotReadbackSlotState.Submitted)
            {
                continue;
            }

            DeliverScreenshotReadbackFailure(
                slot,
                index,
                $"Vulkan device loss aborted screenshot readback slot {index}: {reason}");
            ReleaseScreenshotReadbackReservation(slot);
        }
    }

    private void DeliverScreenshotReadbackFailure(
        VulkanScreenshotReadbackSlot slot,
        int slotIndex,
        string error)
    {
        if (Interlocked.Exchange(ref slot.CallbackDelivered, 1) != 0)
            return;

        Action<ScreenshotReadbackResult>? callback =
            Interlocked.Exchange(ref slot.Callback, null);
        Interlocked.Increment(ref ScreenshotReadbackFailedCount);
        if (callback is null)
            return;

        double? gpuCompletionSeconds = slot.SubmittedTimestamp == 0
            ? null
            : Stopwatch.GetElapsedTime(slot.SubmittedTimestamp).TotalSeconds;
        try
        {
            callback(ScreenshotReadbackResult.Failure(
                error,
                "VulkanRenderer",
                slot.Width,
                slot.Height,
                slot.SourceFormat.ToString(),
                checked((long)slot.RawByteCount),
                slot.UsedMultisampleResolve,
                slotIndex,
                slot.SubmittedAtUtc == default ? null : slot.SubmittedAtUtc,
                DateTimeOffset.UtcNow,
                gpuCompletionSeconds));
        }
        catch (Exception exception)
        {
            Debug.VulkanWarning(
                "[Vulkan] Screenshot readback failure callback threw: {0}",
                exception.Message);
        }
    }

    private void ReleaseScreenshotReadbackReservation(
        VulkanScreenshotReadbackSlot slot)
    {
        if (Interlocked.Exchange(ref slot.ReservationReleased, 1) != 0 ||
            slot.RawByteCount == 0)
        {
            return;
        }

        long remaining = Interlocked.Add(
            ref ScreenshotReadbackReservedRawBytes,
            -checked((long)slot.RawByteCount));
        if (remaining < 0)
            Interlocked.Exchange(ref ScreenshotReadbackReservedRawBytes, 0);
    }
}
