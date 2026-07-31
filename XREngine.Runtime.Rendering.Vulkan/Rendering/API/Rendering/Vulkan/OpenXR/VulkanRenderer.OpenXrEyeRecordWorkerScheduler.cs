using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class OpenXrEyeRecordWorkerScheduler : IDisposable
    {
        private readonly OpenXrEyeRecordWorker _left = new(0);
        private readonly OpenXrEyeRecordWorker _right = new(1);

        public OpenXrEyeRecordWorkerBatchResult Record(
            VulkanRenderer renderer,
            in OpenXrPreparedEyeCommandBufferInput leftEye,
            in OpenXrPreparedEyeCommandBufferInput rightEye)
        {
            _left.Start(renderer, leftEye);
            _right.Start(renderer, rightEye);

            long waitStart = Stopwatch.GetTimestamp();
            OpenXrEyeRecordWorkerResult left = _left.Wait();
            OpenXrEyeRecordWorkerResult right = _right.Wait();
            TimeSpan waitTime = Stopwatch.GetElapsedTime(waitStart);
            TimeSpan recordSpan = ComputeOpenXrEyeRecordSpan(
                left.StartTimestamp,
                left.EndTimestamp,
                right.StartTimestamp,
                right.EndTimestamp);
            TimeSpan recordOverlap = ComputeOpenXrEyeRecordOverlap(
                left.StartTimestamp,
                left.EndTimestamp,
                right.StartTimestamp,
                right.EndTimestamp);
            RuntimeEngine.Rendering.Stats.Vr.RecordVrOpenXrEyePrimaryRecordTiming(
                recordSpan,
                recordOverlap);

            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] eye record workers completed leftSuccess={0} rightSuccess={1} leftThread={2} rightThread={3} leftMs={4:F3} rightMs={5:F3} spanMs={6:F3} overlapMs={7:F3} waitMs={8:F3}",
                    left.Success,
                    right.Success,
                    left.ThreadId,
                    right.ThreadId,
                    left.RecordTime.TotalMilliseconds,
                    right.RecordTime.TotalMilliseconds,
                    recordSpan.TotalMilliseconds,
                    recordOverlap.TotalMilliseconds,
                    waitTime.TotalMilliseconds);
            }

            return new OpenXrEyeRecordWorkerBatchResult(left, right, waitTime);
        }

        public void Dispose()
        {
            _left.Dispose();
            _right.Dispose();
        }
    }

    internal static TimeSpan ComputeOpenXrEyeRecordSpan(
        long leftStart,
        long leftEnd,
        long rightStart,
        long rightEnd)
    {
        if (leftEnd <= leftStart || rightEnd <= rightStart)
            return TimeSpan.Zero;

        return Stopwatch.GetElapsedTime(
            Math.Min(leftStart, rightStart),
            Math.Max(leftEnd, rightEnd));
    }

    internal static TimeSpan ComputeOpenXrEyeRecordOverlap(
        long leftStart,
        long leftEnd,
        long rightStart,
        long rightEnd)
    {
        if (leftEnd <= leftStart || rightEnd <= rightStart)
            return TimeSpan.Zero;

        long overlapStart = Math.Max(leftStart, rightStart);
        long overlapEnd = Math.Min(leftEnd, rightEnd);
        return overlapEnd > overlapStart
            ? Stopwatch.GetElapsedTime(overlapStart, overlapEnd)
            : TimeSpan.Zero;
    }
}
