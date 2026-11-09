using System.Diagnostics;
using XREngine.Execution;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Dispatches paired OpenXR primary recording through the process-owned render
/// domain. No OpenXR-specific threads or wait handles are retained.
/// </summary>
internal sealed class OpenXrEyeRecordWorkerScheduler : IDisposable
{
    private readonly OpenXrEyeRecordWorkExecutor _executor = new();

    /// <summary>
    /// Rents the paired eye batch and claims both eye lanes before the caller freezes
    /// worker inputs, because freezing touches the lanes' backend attachments. A lane
    /// that asynchronous work reserves fails here with a collision diagnostic.
    /// </summary>
    public static RenderWorkBatchLease RentAndClaimLanes(
        VulkanCommandRuntime runtime,
        int firstLaneId,
        int secondLaneId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        RenderWorkDomain domain = runtime.RenderDomain;
        RenderWorkBatchLease lease = domain.RentBatch(2);
        ulong laneMask = (1UL << firstLaneId) | (1UL << secondLaneId);
        RenderWorkAdmissionResult claim = domain.TryClaimSynchronousLanes(lease, laneMask);
        if (claim.IsAdmitted)
            return lease;

        lease.Dispose();
        throw new InvalidOperationException(
            $"OpenXR eye lanes {firstLaneId} and {secondLaneId} cannot be claimed: {claim.Reason}. " +
            "Asynchronous render work reserves one of them.");
    }

    /// <summary>
    /// Records both eyes on their claimed lanes and disposes <paramref name="lease"/>.
    /// The lease must come from <see cref="RentAndClaimLanes"/>.
    /// </summary>
    public OpenXrEyeRecordWorkerBatchResult Record(
        VulkanCommandRuntime runtime,
        VulkanOpenXrCommandRecordingService recordingService,
        RenderWorkBatchLease lease,
        in OpenXrPreparedEyeRecordWorkerInput leftEye,
        in OpenXrPreparedEyeRecordWorkerInput rightEye)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(recordingService);
        RenderWorkDomain domain = runtime.RenderDomain;
        try
        {
            _executor.Prepare(runtime, recordingService, in leftEye, in rightEye);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        lease.SetItem(
            0,
            new RenderWorkItem(
                OpenXrEyeRecordWorkExecutor.EyePrimaryOperationKind,
                SourceStart: 0,
                SourceCount: 1,
                PreferredLane: leftEye.RenderLaneId,
                EstimatedCost: 32));
        lease.SetItem(
            1,
            new RenderWorkItem(
                OpenXrEyeRecordWorkExecutor.EyePrimaryOperationKind,
                SourceStart: 1,
                SourceCount: 1,
                PreferredLane: rightEye.RenderLaneId,
                EstimatedCost: 32));

        long waitStart = Stopwatch.GetTimestamp();
        RenderWorkBatchResult dispatchResult;
        try
        {
            dispatchResult = domain.ExecuteAndWait(
                ref lease,
                _executor,
                leftEye.RenderFrameSlot,
                RenderWorkDomain.FatalBatchWait);
        }
        finally
        {
            lease.Dispose();
        }

        TimeSpan waitTime = Stopwatch.GetElapsedTime(waitStart);
        if (!dispatchResult.Succeeded)
        {
            throw new InvalidOperationException(
                "OpenXR render-domain eye recording failed before canonical result merge.",
                dispatchResult.Exception);
        }

        OpenXrEyeRecordWorkerResult left = _executor.GetResult(0);
        OpenXrEyeRecordWorkerResult right = _executor.GetResult(1);
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
        => _executor.Clear();

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
