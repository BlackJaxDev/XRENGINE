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
                    out commandBuffersCompleted);
            }

            if (submitted)
            {
                int publishCount = CountOpenXrEyeRecordedTextureUploads();
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

    private readonly record struct OpenXrEyeRecordWorkerBatchResult(
        OpenXrEyeRecordWorkerResult Left,
        OpenXrEyeRecordWorkerResult Right,
        TimeSpan WaitForWorkersTime);

    private readonly record struct OpenXrEyeRecordWorkerResult(
        bool Success,
        OpenXrRecordedEyeCommandBuffer Recorded,
        int ThreadId,
        TimeSpan RecordTime,
        string? ErrorMessage);

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

            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] eye record workers completed leftSuccess={0} rightSuccess={1} leftThread={2} rightThread={3} leftMs={4:F3} rightMs={5:F3} waitMs={6:F3}",
                    left.Success,
                    right.Success,
                    left.ThreadId,
                    right.ThreadId,
                    left.RecordTime.TotalMilliseconds,
                    right.RecordTime.TotalMilliseconds,
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

    private sealed class OpenXrEyeRecordWorker : IDisposable
    {
        private readonly int _workerIndex;
        private readonly AutoResetEvent _workAvailable = new(false);
        private readonly ManualResetEventSlim _workCompleted = new(true);
        private readonly Thread _thread;
        private VulkanRenderer? _renderer;
        private OpenXrPreparedEyeCommandBufferInput _prepared;
        private OpenXrEyeRecordWorkerResult _result;
        private bool _stopping;

        public OpenXrEyeRecordWorker(int workerIndex)
        {
            _workerIndex = workerIndex;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"OpenXR Vulkan eye record worker {workerIndex}"
            };
            _thread.Start();
        }

        public void Start(VulkanRenderer renderer, in OpenXrPreparedEyeCommandBufferInput prepared)
        {
            _workCompleted.Reset();
            _renderer = renderer;
            _prepared = prepared;
            _result = default;
            _workAvailable.Set();
        }

        public OpenXrEyeRecordWorkerResult Wait()
        {
            _workCompleted.Wait();
            return _result;
        }

        private void Run()
        {
            while (true)
            {
                _workAvailable.WaitOne();
                if (_stopping)
                    return;

                VulkanRenderer? renderer = _renderer;
                if (renderer is null)
                {
                    _result = new OpenXrEyeRecordWorkerResult(false, default, Environment.CurrentManagedThreadId, TimeSpan.Zero, "worker has no renderer");
                    _workCompleted.Set();
                    continue;
                }

                long start = Stopwatch.GetTimestamp();
                int threadId = Environment.CurrentManagedThreadId;
                try
                {
                    bool success = renderer.TryRecordOpenXrEyeSwapchainCommandBufferFromWorker(
                        _workerIndex,
                        _prepared,
                        out OpenXrRecordedEyeCommandBuffer recorded);
                    _result = new OpenXrEyeRecordWorkerResult(
                        success,
                        recorded,
                        threadId,
                        Stopwatch.GetElapsedTime(start),
                        null);
                }
                catch (Exception ex)
                {
                    _result = new OpenXrEyeRecordWorkerResult(
                        false,
                        default,
                        threadId,
                        Stopwatch.GetElapsedTime(start),
                        ex.Message);
                }
                finally
                {
                    _renderer = null;
                    _workCompleted.Set();
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _workAvailable.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(2)))
            {
                Debug.VulkanWarning(
                    "[OpenXR] Timed out waiting for Vulkan eye record worker {0} to stop.",
                    _workerIndex);
            }

            _workCompleted.Dispose();
            _workAvailable.Dispose();
        }
    }
}
