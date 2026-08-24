using System.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed class OpenXrEyeRecordWorkerScheduler : IDisposable
{
    private readonly OpenXrEyeRecordWorker _left = new(0);
    private readonly OpenXrEyeRecordWorker _right = new(1);

    public OpenXrEyeRecordWorkerBatchResult Record(
        VulkanOpenXrCommandRecordingService recordingService,
        in OpenXrPreparedEyeRecordWorkerInput leftEye,
        in OpenXrPreparedEyeRecordWorkerInput rightEye)
    {
        _left.Start(recordingService, leftEye);
        _right.Start(recordingService, rightEye);

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

        return new OpenXrEyeRecordWorkerBatchResult(left, right, waitTime);
    }

    public void Dispose()
    {
        _left.Dispose();
        _right.Dispose();
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
