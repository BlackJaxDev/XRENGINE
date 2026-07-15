using System;
using System.Diagnostics;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private OpenXrEyeRecordWorkerScheduler? _openXrEyeRecordWorkerScheduler;
    private readonly object _openXrParallelEyePrimaryRecordSharedStateLock = new();

    private bool TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(
        in OpenXrEyeSwapchainRenderRequest firstEye,
        in OpenXrEyeSwapchainRenderRequest secondEye)
    {
        ClearOpenXrEyeRecordedTextureUploads();
        OpenXrRecordedEyeCommandBuffer firstRecorded = default;
        OpenXrRecordedEyeCommandBuffer secondRecorded = default;
        OpenXrEyeRecordWorkerBatchResult workerBatch = default;
        bool hasFirst = false;
        bool hasSecond = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;

        try
        {
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.PrepareInputs"))
            {
                if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out OpenXrPreparedEyeCommandBufferInput preparedFirstEye))
                    return false;
                if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out OpenXrPreparedEyeCommandBufferInput preparedSecondEye))
                    return false;

                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.WorkerRecord"))
                {
                    workerBatch = DispatchOpenXrEyeRecordWorkers(preparedFirstEye, preparedSecondEye);
                    hasFirst = workerBatch.Left.Success;
                    hasSecond = workerBatch.Right.Success;
                    firstRecorded = workerBatch.Left.Recorded;
                    secondRecorded = workerBatch.Right.Recorded;
                }
            }

            if (!hasFirst || !hasSecond)
            {
                LogOpenXrEyeRecordWorkerFailure(workerBatch);
                return false;
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.SubmitAndWait"))
            {
                submitted = SubmitAndWaitOpenXrCommandBuffers(
                    firstRecorded.CommandBuffer,
                    secondRecorded.CommandBuffer,
                    out commandBuffersCompleted,
                    CreateOpenXrBatchSubmissionDiagnosticContext(
                        "OpenXrEyeParallelBatchSubmit",
                        "OpenXrEyeParallelBatch",
                        in firstRecorded,
                        in secondRecorded,
                        firstEye.Extent));
            }

            if (submitted)
            {
                int publishCount = CountOpenXrEyeRecordedTextureUploads();
                CompleteOpenXrGpuProfilerSubmission(in firstRecorded);
                CompleteOpenXrGpuProfilerSubmission(in secondRecorded);
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.PublishUploads"))
                    PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit("OpenXR eye parallel batch");
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.FlushRetired"))
                    ForceFlushCompletedNonImageRetiredResources();
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye parallel batch submit completed leftFrameSlot={0} rightFrameSlot={1} publishedUploads={2} retiredFlushSlots={3}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        publishCount,
                        MAX_FRAMES_IN_FLIGHT);
                }
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                int cancelCount = CountOpenXrEyeRecordedTextureUploads();
                CancelOpenXrEyeRecordedTextureUploads("OpenXR eye parallel batch command buffers did not complete");
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye parallel batch submit did not complete leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
            {
                int cancelCount = CountOpenXrEyeRecordedTextureUploads();
                CancelOpenXrEyeRecordedTextureUploads("OpenXR eye parallel batch command buffer submit failed");
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye parallel batch submit failed leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            ClearOpenXrEyeRecordedTextureUploads();
        }
    }

    private OpenXrEyeRecordWorkerBatchResult DispatchOpenXrEyeRecordWorkers(
        in OpenXrPreparedEyeCommandBufferInput leftEye,
        in OpenXrPreparedEyeCommandBufferInput rightEye)
    {
        if (!IsDeviceOperational)
            return new OpenXrEyeRecordWorkerBatchResult(
                new(false, default, Environment.CurrentManagedThreadId, TimeSpan.Zero, $"Vulkan device state is {DeviceState}"),
                new(false, default, Environment.CurrentManagedThreadId, TimeSpan.Zero, $"Vulkan device state is {DeviceState}"),
                TimeSpan.Zero);

        OpenXrEyeRecordWorkerScheduler scheduler = EnsureOpenXrEyeRecordWorkerScheduler();
        return scheduler.Record(this, leftEye, rightEye);
    }

    private OpenXrEyeRecordWorkerScheduler EnsureOpenXrEyeRecordWorkerScheduler()
        => _openXrEyeRecordWorkerScheduler ??= new OpenXrEyeRecordWorkerScheduler();

    private bool TryRecordOpenXrEyeSwapchainCommandBufferFromWorker(
        int workerIndex,
        in OpenXrPreparedEyeCommandBufferInput prepared,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        if (!IsDeviceOperational)
        {
            recorded = default;
            return false;
        }

        if (OpenXrVulkanTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXrVulkan] eye record worker={0} entering thread-scoped prepared primary record",
                workerIndex);
        }

        using IDisposable currentRendererScope = AbstractRenderer.PushThreadCurrent(this);
        // Resource-planner states are eye-scoped, but Vulkan texture/FBO wrapper
        // layout trackers are shared objects. Keep their oldLayout bookkeeping
        // ordered until primary recording has command-buffer-local layout state.
        lock (_openXrParallelEyePrimaryRecordSharedStateLock)
            return TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in prepared, out recorded);
    }

    private void DestroyOpenXrEyeRecordWorkers()
    {
        _openXrEyeRecordWorkerScheduler?.Dispose();
        _openXrEyeRecordWorkerScheduler = null;
    }

    private static void LogOpenXrEyeRecordWorkerFailure(in OpenXrEyeRecordWorkerBatchResult batch)
    {
        Debug.VulkanWarningEvery(
            "OpenXR.Vulkan.ParallelCommandBufferRecording.RecordFailure",
            TimeSpan.FromSeconds(1),
            "[OpenXR] Parallel eye primary recording failed. leftSuccess={0} rightSuccess={1} leftThread={2} rightThread={3} leftError={4} rightError={5}",
            batch.Left.Success,
            batch.Right.Success,
            batch.Left.ThreadId,
            batch.Right.ThreadId,
            batch.Left.ErrorMessage ?? "<none>",
            batch.Right.ErrorMessage ?? "<none>");
    }
}
