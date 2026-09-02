using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering;
using VulkanSemaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns asynchronous OpenXR Vulkan submission tracking, non-blocking completion
/// polling, and deferred recycling of command buffers, arena slots, staging uploads,
/// and input leases without synchronous render-thread stalls.
/// </summary>
internal sealed class OpenXrVulkanSubmissionTracker : IDisposable
{
    internal const int DefaultMaxInFlightSubmissions = 3;
    private const uint DefaultRecoveryWaitTimeoutMs = 100u;
    private const uint DefaultShutdownDrainTimeoutMs = 5000u;

    internal sealed class InFlightSubmission
    {
        public ulong FrameId;
        public long PredictedDisplayTime;
        public uint ViewMask;
        public uint LeftImageIndex;
        public uint RightImageIndex;
        public OpenXrRecordedEyeCommandBuffer FirstRecorded;
        public OpenXrRecordedEyeCommandBuffer SecondRecorded;
        public bool HasFirst;
        public bool HasSecond;
        public OpenXrPreparedEyeCommandBufferInput FirstPrepared;
        public OpenXrPreparedEyeCommandBufferInput SecondPrepared;
        public bool HasFirstPrepared;
        public bool HasSecondPrepared;
        public readonly List<VulkanImportedTexturePendingUpload> Uploads = new(4);
        public VulkanSemaphore TimelineSemaphore;
        public ulong TimelineValue;
        public VulkanMappedFrameArena? MappedFrameArena;
        public ulong MappedFrameGeneration;
        public VulkanFrameDataArena? FrameDataArena;
        public ulong FrameDataGeneration;
        public uint[] FrameSlots = [];
        public int FrameSlotCount;
        public long SubmitStartTimestamp;
        public long SubmitEndTimestamp;
        public long EnqueuedTimestamp;
        public bool CompletionProven;
        public bool Reopened;
    }

    private readonly VulkanCommandRuntime _commandRuntime;
    private readonly Action<OpenXrRecordedEyeCommandBuffer>? _freeCommandBuffer;
    private readonly Action<InFlightSubmission>? _onSubmissionRetired;
    private readonly List<InFlightSubmission> _inFlight = new(DefaultMaxInFlightSubmissions);
    private readonly object _gate = new();

    private int _forcedWaitCount;
    private int _completedSubmissionCount;
    private ulong _lastCompletedFrameId;
    private uint _lastAcquiredLeftImageIndex;
    private uint _lastAcquiredRightImageIndex;
    private ulong _leftImageLastFrame;
    private ulong _rightImageLastFrame;

    public OpenXrVulkanSubmissionTracker(
        VulkanCommandRuntime commandRuntime,
        Action<OpenXrRecordedEyeCommandBuffer>? freeCommandBuffer = null,
        Action<InFlightSubmission>? onSubmissionRetired = null)
    {
        _commandRuntime = commandRuntime ?? throw new ArgumentNullException(nameof(commandRuntime));
        _freeCommandBuffer = freeCommandBuffer;
        _onSubmissionRetired = onSubmissionRetired;
    }

    public int InFlightCount
    {
        get
        {
            lock (_gate)
                return _inFlight.Count;
        }
    }

    public int ForcedWaitCount => Volatile.Read(ref _forcedWaitCount);

    public int CompletedSubmissionCount => Volatile.Read(ref _completedSubmissionCount);

    public ulong LastCompletedFrameId => Volatile.Read(ref _lastCompletedFrameId);

    public bool HasInFlightWork
    {
        get
        {
            lock (_gate)
                return _inFlight.Count > 0;
        }
    }

    /// <summary>
    /// Atomically registers an asynchronous OpenXR eye queue submission.
    /// Transfers ownership of recorded command buffers, uploads, arena slots,
    /// and input leases until GPU timeline completion is proven.
    /// </summary>
    public void RegisterSubmission(
        ulong frameId,
        long predictedDisplayTime,
        uint viewMask,
        uint leftImageIndex,
        uint rightImageIndex,
        in OpenXrRecordedEyeCommandBuffer firstRecorded,
        bool hasFirst,
        in OpenXrRecordedEyeCommandBuffer secondRecorded,
        bool hasSecond,
        in OpenXrPreparedEyeCommandBufferInput firstPrepared,
        bool hasFirstPrepared,
        in OpenXrPreparedEyeCommandBufferInput secondPrepared,
        bool hasSecondPrepared,
        IReadOnlyList<VulkanImportedTexturePendingUpload>? uploads,
        VulkanSemaphore timelineSemaphore,
        ulong timelineValue,
        VulkanMappedFrameArena? mappedFrameArena,
        ulong mappedFrameGeneration,
        VulkanFrameDataArena? frameDataArena,
        ulong frameDataGeneration,
        ReadOnlySpan<uint> frameSlots,
        long submitStartTimestamp,
        long submitEndTimestamp)
    {
        InFlightSubmission entry = new()
        {
            FrameId = frameId,
            PredictedDisplayTime = predictedDisplayTime,
            ViewMask = viewMask,
            LeftImageIndex = leftImageIndex,
            RightImageIndex = rightImageIndex,
            FirstRecorded = firstRecorded,
            HasFirst = hasFirst,
            SecondRecorded = secondRecorded,
            HasSecond = hasSecond,
            FirstPrepared = firstPrepared,
            HasFirstPrepared = hasFirstPrepared,
            SecondPrepared = secondPrepared,
            HasSecondPrepared = hasSecondPrepared,
            TimelineSemaphore = timelineSemaphore,
            TimelineValue = timelineValue,
            MappedFrameArena = mappedFrameArena,
            MappedFrameGeneration = mappedFrameGeneration,
            FrameDataArena = frameDataArena,
            FrameDataGeneration = frameDataGeneration,
            FrameSlots = frameSlots.ToArray(),
            FrameSlotCount = frameSlots.Length,
            SubmitStartTimestamp = submitStartTimestamp,
            SubmitEndTimestamp = submitEndTimestamp,
            EnqueuedTimestamp = Stopwatch.GetTimestamp(),
            CompletionProven = false,
            Reopened = false
        };

        if (uploads is not null && uploads.Count > 0)
        {
            for (int i = 0; i < uploads.Count; i++)
                entry.Uploads.Add(uploads[i]);
        }

        uint oldestAge = 0;
        uint imageReuseAge = 0;
        int inFlightSnapshot;

        lock (_gate)
        {
            _inFlight.Add(entry);
            inFlightSnapshot = _inFlight.Count;
            if (_inFlight.Count > 0)
                oldestAge = checked((uint)Math.Max(0L, (long)(frameId - _inFlight[0].FrameId)));

            if (hasFirst)
            {
                if (_lastAcquiredLeftImageIndex == leftImageIndex && _leftImageLastFrame > 0)
                    imageReuseAge = checked((uint)Math.Max(0L, (long)(frameId - _leftImageLastFrame)));
                _lastAcquiredLeftImageIndex = leftImageIndex;
                _leftImageLastFrame = frameId;
            }
            if (hasSecond)
            {
                if (_lastAcquiredRightImageIndex == rightImageIndex && _rightImageLastFrame > 0)
                    imageReuseAge = Math.Max(imageReuseAge, checked((uint)Math.Max(0L, (long)(frameId - _rightImageLastFrame))));
                _lastAcquiredRightImageIndex = rightImageIndex;
                _rightImageLastFrame = frameId;
            }
        }

        TimeSpan queueSubmitElapsed = Stopwatch.GetElapsedTime(submitStartTimestamp, submitEndTimestamp);
        RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeQueueSubmitTime(queueSubmitElapsed);
        RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeInFlightStats((uint)inFlightSnapshot, oldestAge, imageReuseAge);

        if (_commandRuntime.IsOpenXrTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXR.Tracker] Registered submission frame={0} timelineValue={1} inFlight={2} queueSubmitMs={3:F3}",
                frameId,
                timelineValue,
                inFlightSnapshot,
                queueSubmitElapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Non-blockingly queries completion of in-flight submissions and retires
    /// completed resources, reopening arena slots and publishing texture uploads.
    /// </summary>
    public int PollCompletions()
    {
        if (!_commandRuntime.DeviceContext.IsOperational)
            return 0;

        List<InFlightSubmission> readyToRetire = new(2);

        lock (_gate)
        {
            for (int i = 0; i < _inFlight.Count; i++)
            {
                InFlightSubmission entry = _inFlight[i];
                if (!entry.CompletionProven)
                {
                    Result queryResult = _commandRuntime.Synchronization.QueryTimelineCompletion(
                        _commandRuntime.Api,
                        _commandRuntime.DeviceContext,
                        _commandRuntime.ResourceRuntime.Lifetime.Tracker,
                        entry.TimelineSemaphore,
                        entry.TimelineValue,
                        out bool completed);

                    if (queryResult != Result.Success)
                    {
                        Debug.VulkanWarning(
                            "[OpenXR.Tracker] QueryTimelineCompletion failed for frame {0} timeline {1}: {2}",
                            entry.FrameId,
                            entry.TimelineValue,
                            queryResult);
                        continue;
                    }

                    if (!completed)
                        continue;

                    entry.CompletionProven = true;
                    _commandRuntime.CompleteTrackedTimeline(entry.TimelineSemaphore, entry.TimelineValue);
                }

                readyToRetire.Add(entry);
            }

            for (int i = 0; i < readyToRetire.Count; i++)
                _inFlight.Remove(readyToRetire[i]);
        }

        int retiredCount = readyToRetire.Count;
        for (int i = 0; i < retiredCount; i++)
        {
            InFlightSubmission completed = readyToRetire[i];
            RetireCompletedSubmission(completed);
            Volatile.Write(ref _lastCompletedFrameId, completed.FrameId);
            Interlocked.Increment(ref _completedSubmissionCount);
        }

        return retiredCount;
    }

    private void RetireCompletedSubmission(InFlightSubmission entry)
    {
        if (entry.Reopened)
            return;

        // 1. Reopen mapped frame slots
        if (entry.MappedFrameArena is not null)
        {
            for (int i = 0; i < entry.FrameSlotCount; i++)
            {
                entry.MappedFrameArena.TryResetFrameSlot(
                    entry.FrameSlots[i],
                    entry.MappedFrameGeneration,
                    submissionCompletionProven: true);
            }
        }

        // 2. Reopen canonical frame data arena slots
        if (entry.FrameDataArena is not null)
        {
            for (int i = 0; i < entry.FrameSlotCount; i++)
            {
                entry.FrameDataArena.TryResetFrameSlot(
                    entry.FrameSlots[i],
                    entry.FrameDataGeneration,
                    submissionCompletionProven: true);
            }
        }

        // 3. Publish texture uploads now that GPU execution has finished
        if (entry.Uploads.Count > 0)
        {
            _commandRuntime.PublishOpenXrRecordedTextureUploads(
                entry.Uploads,
                "OpenXR eye async completion");
            entry.Uploads.Clear();
        }

        // 4. Free recorded primary command buffers back to pool
        if (entry.HasFirst)
        {
            if (_freeCommandBuffer is not null)
                _freeCommandBuffer(entry.FirstRecorded);
            else
                FreeRecordedCommandBufferDirect(entry.FirstRecorded);
        }
        if (entry.HasSecond)
        {
            if (_freeCommandBuffer is not null)
                _freeCommandBuffer(entry.SecondRecorded);
            else
                FreeRecordedCommandBufferDirect(entry.SecondRecorded);
        }

        // 5. Release prepared visibility input leases
        if (entry.HasFirstPrepared && entry.FirstPrepared.Ops is { } firstOps)
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(firstOps);
        if (entry.HasSecondPrepared && entry.SecondPrepared.Ops is { } secondOps)
            VulkanAdvancedVisibilityInputLease.ReleaseOperations(secondOps);

        // 6. Notify downstream frame completion callback
        _onSubmissionRetired?.Invoke(entry);

        entry.Reopened = true;

        if (_commandRuntime.IsOpenXrTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXR.Tracker] Retired submission frame={0} timelineValue={1} frameSlots={2}",
                entry.FrameId,
                entry.TimelineValue,
                entry.FrameSlotCount);
        }
    }

    private void FreeRecordedCommandBufferDirect(OpenXrRecordedEyeCommandBuffer recorded)
    {
        if (recorded.OwnedByOpenXrPrimaryCache)
            return;

        CommandBuffer commandBuffer = recorded.CommandBuffer;
        if (commandBuffer.Handle != 0)
        {
            _commandRuntime.FreeCommandBufferWithLifetime(
                (int)recorded.FrameDataSlotIndex,
                _commandRuntime.Pools.PrimaryGraphics,
                ref commandBuffer,
                "OpenXR.RecordedEye.AsyncTracker");
        }
    }

    /// <summary>
    /// Ensures that in-flight submissions do not exceed <paramref name="maxInFlight"/>.
    /// If the queue is full, executes a bounded recovery wait on the oldest pending
    /// submission and increments forced-wait telemetry.
    /// </summary>
    public bool EnsureInFlightBudget(
        int maxInFlight = DefaultMaxInFlightSubmissions,
        uint timeoutMs = DefaultRecoveryWaitTimeoutMs)
    {
        PollCompletions();

        InFlightSubmission? oldest = null;
        lock (_gate)
        {
            if (_inFlight.Count < maxInFlight)
                return true;

            oldest = _inFlight[0];
        }

        if (oldest is null || !_commandRuntime.DeviceContext.IsOperational)
            return true;

        long waitStart = Stopwatch.GetTimestamp();
        Result waitResult;
        using (RuntimeRenderingHostServices.Profiling.StartProfileScope(
                   "OpenXR.Vulkan.SubmitTimelineWait"))
        {
            waitResult = _commandRuntime.Synchronization.WaitForTimelineCompletion(
                _commandRuntime.Api,
                _commandRuntime.DeviceContext,
                _commandRuntime.ResourceRuntime.Lifetime.Tracker,
                oldest.TimelineSemaphore,
                oldest.TimelineValue,
                (ulong)timeoutMs * 1_000_000UL);
        }
        long waitEnd = Stopwatch.GetTimestamp();
        TimeSpan waitElapsed = Stopwatch.GetElapsedTime(waitStart, waitEnd);

        RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeCompletionWaitTime(waitElapsed);
        RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeFenceForcedWait();
        Interlocked.Increment(ref _forcedWaitCount);

        if (waitResult != Result.Success && waitResult != Result.Timeout)
        {
            Debug.VulkanWarning(
                "[OpenXR.Tracker] Recovery wait failed on frame {0} timeline {1}: {2}",
                oldest.FrameId,
                oldest.TimelineValue,
                waitResult);
            return false;
        }

        PollCompletions();
        return true;
    }

    /// <summary>
    /// Safely drains all outstanding in-flight submissions during session stop,
    /// runtime loss, or renderer teardown.
    /// </summary>
    public bool DrainAll(uint timeoutMs = DefaultShutdownDrainTimeoutMs)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        while (HasInFlightWork)
        {
            PollCompletions();
            InFlightSubmission? pending = null;
            lock (_gate)
            {
                if (_inFlight.Count == 0)
                    break;
                pending = _inFlight[0];
            }

            if (pending is null || !_commandRuntime.DeviceContext.IsOperational)
                break;

            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                Debug.VulkanWarning(
                    "[OpenXR.Tracker] DrainAll timed out after {0}ms with {1} submissions pending.",
                    timeoutMs,
                    InFlightCount);
                return false;
            }

            ulong timeoutNs = checked((ulong)Math.Max(1L, (long)(remainingTicks * 1_000_000_000.0 / Stopwatch.Frequency)));
            _ = _commandRuntime.Synchronization.WaitForTimelineCompletion(
                _commandRuntime.Api,
                _commandRuntime.DeviceContext,
                _commandRuntime.ResourceRuntime.Lifetime.Tracker,
                pending.TimelineSemaphore,
                pending.TimelineValue,
                timeoutNs);

            PollCompletions();
        }

        return true;
    }

    public void Dispose()
    {
        DrainAll(1000u);
    }
}
