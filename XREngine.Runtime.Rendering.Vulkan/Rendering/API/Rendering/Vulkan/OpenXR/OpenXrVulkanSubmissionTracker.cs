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
    private const int MaxTrackedCommandBuffers = 3;
    internal const int MaxTrackedUploads = 64;
    // A full 100ms recovery wait exceeds several OpenXR display intervals. Keep
    // admission recovery bounded to one short scheduling opportunity, then
    // re-query completion before allowing another recording transaction.
    private const uint DefaultRecoveryWaitTimeoutMs = 8u;
    private const uint DefaultShutdownDrainTimeoutMs = 5000u;
    private const int MaxTrackedSwapchainImages = 64;

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
        public CommandBuffer TemporaryCommandBuffer;
        public bool HasTemporaryCommandBuffer;
        public readonly VulkanImportedTexturePendingUpload[] Uploads = new VulkanImportedTexturePendingUpload[MaxTrackedUploads];
        public int UploadCount;
        public VulkanSemaphore TimelineSemaphore;
        public ulong TimelineValue;
        public VulkanMappedFrameArena? MappedFrameArena;
        public ulong MappedFrameGeneration;
        public VulkanFrameDataArena? FrameDataArena;
        public ulong FrameDataGeneration;
        public readonly uint[] FrameSlots = new uint[MaxTrackedCommandBuffers];
        public int FrameSlotCount;
        public long SubmitStartTimestamp;
        public long SubmitEndTimestamp;
        public long EnqueuedTimestamp;
        public bool CompletionProven;
        public bool Reopened;
        public bool Active;
        public bool Retiring;
        public bool PendingCommit;
        public bool NativeSubmissionAccepted;
        public bool Cancelled;
        public int UploadSettlementIndex;
        public int MappedFrameSlotResetCount;
        public int FrameDataSlotResetCount;
        public bool RetiredCallbackInvoked;
        public ulong TicketGeneration;
    }

    private sealed class AdmissionSlot
    {
        internal bool Active;
        internal ulong Generation;
        internal int PreparedSlotIndex = -1;
    }

    /// <summary>
    /// Immutable reservation lease. Its generation is captured at admission, so
    /// a later reuse of the preallocated backing slot cannot let an old caller
    /// cancel or commit the new reservation.
    /// </summary>
    internal readonly struct SubmissionAdmissionTicket
    {
        private readonly OpenXrVulkanSubmissionTracker? _tracker;
        internal readonly int AdmissionSlotIndex;
        internal readonly ulong Generation;

        internal SubmissionAdmissionTicket(
            OpenXrVulkanSubmissionTracker tracker,
            int admissionSlotIndex,
            ulong generation)
        {
            _tracker = tracker;
            AdmissionSlotIndex = admissionSlotIndex;
            Generation = generation;
        }

        internal bool Active => _tracker?.IsTicketActive(this) == true;
    }

    /// <summary>
    /// Non-throwing ownership sink invoked by the common tracked-submit gateway
    /// immediately after vkQueueSubmit succeeds. It prevents any post-submit
    /// diagnostic or lifetime-publication fault from returning ownership to the
    /// recording caller.
    /// </summary>
    internal readonly struct AcceptedSubmissionSink
    {
        private readonly OpenXrVulkanSubmissionTracker _tracker;
        private readonly SubmissionAdmissionTicket _ticket;
        private readonly VulkanSemaphore _completionSemaphore;
        private readonly long _submitStartTimestamp;

        internal AcceptedSubmissionSink(
            OpenXrVulkanSubmissionTracker tracker,
            in SubmissionAdmissionTicket ticket,
            VulkanSemaphore completionSemaphore,
            long submitStartTimestamp)
        {
            _tracker = tracker;
            _ticket = ticket;
            _completionSemaphore = completionSemaphore;
            _submitStartTimestamp = submitStartTimestamp;
        }

        internal void Commit(ulong completionValue)
            => _tracker.CommitAcceptedSubmission(
                in _ticket,
                _completionSemaphore,
                completionValue,
                _submitStartTimestamp,
                Stopwatch.GetTimestamp());
    }

    private readonly VulkanCommandRuntime _commandRuntime;
    private readonly Action<OpenXrRecordedEyeCommandBuffer>? _freeCommandBuffer;
    private Action<InFlightSubmission>? _onSubmissionRetired;
    private readonly InFlightSubmission[] _inFlight = new InFlightSubmission[DefaultMaxInFlightSubmissions];
    private readonly AdmissionSlot[] _admissionSlots =
        new AdmissionSlot[DefaultMaxInFlightSubmissions];
    private readonly object _gate = new();

    private int _forcedWaitCount;
    private int _reservedSubmissionCount;
    private int _completedSubmissionCount;
    private ulong _lastCompletedFrameId;
    private readonly ulong[] _leftImageLastFrame = new ulong[MaxTrackedSwapchainImages];
    private readonly ulong[] _rightImageLastFrame = new ulong[MaxTrackedSwapchainImages];
    private VulkanSemaphore _latestAcceptedCompletionSemaphore;
    private ulong _latestAcceptedCompletionValue;

    public OpenXrVulkanSubmissionTracker(
        VulkanCommandRuntime commandRuntime,
        Action<OpenXrRecordedEyeCommandBuffer>? freeCommandBuffer = null,
        Action<InFlightSubmission>? onSubmissionRetired = null)
    {
        _commandRuntime = commandRuntime ?? throw new ArgumentNullException(nameof(commandRuntime));
        _freeCommandBuffer = freeCommandBuffer;
        _onSubmissionRetired = onSubmissionRetired;
        for (int i = 0; i < _admissionSlots.Length; i++)
            _admissionSlots[i] = new AdmissionSlot();
        for (int i = 0; i < _inFlight.Length; i++)
            _inFlight[i] = new InFlightSubmission();
    }

    internal void SetSubmissionRetiredCallback(Action<InFlightSubmission> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (_onSubmissionRetired is not null && _onSubmissionRetired != callback)
                throw new InvalidOperationException("OpenXR submission retirement already has a different owner.");

            _onSubmissionRetired = callback;
        }
    }

    public int InFlightCount
    {
        get
        {
            lock (_gate)
                return CountActiveSubmissionsNoLock();
        }
    }

    public int ForcedWaitCount => Volatile.Read(ref _forcedWaitCount);

    public int CompletedSubmissionCount => Volatile.Read(ref _completedSubmissionCount);

    public ulong LastCompletedFrameId => Volatile.Read(ref _lastCompletedFrameId);

    public bool TryGetLatestAcceptedCompletion(
        out VulkanSemaphore semaphore,
        out ulong value)
    {
        lock (_gate)
        {
            semaphore = _latestAcceptedCompletionSemaphore;
            value = _latestAcceptedCompletionValue;
            return semaphore.Handle != 0 && value != 0UL;
        }
    }

    public bool HasInFlightWork
    {
        get
        {
            lock (_gate)
                return CountOwnedSubmissionsNoLock() > 0;
        }
    }

    private int CountActiveSubmissionsNoLock()
    {
        int count = 0;
        for (int i = 0; i < _inFlight.Length; i++)
            if (_inFlight[i].Active || _inFlight[i].Retiring)
                count++;
        return count;
    }

    private int CountOwnedSubmissionsNoLock()
        => CountActiveSubmissionsNoLock() + _reservedSubmissionCount;

    private InFlightSubmission? FindReusableSubmissionNoLock()
    {
        for (int i = 0; i < _inFlight.Length; i++)
            if (!_inFlight[i].Active &&
                !_inFlight[i].Retiring &&
                !_inFlight[i].PendingCommit)
                return _inFlight[i];
        return null;
    }

    private ulong FindOldestFrameNoLock()
    {
        ulong oldest = ulong.MaxValue;
        for (int i = 0; i < _inFlight.Length; i++)
        {
            InFlightSubmission entry = _inFlight[i];
            if ((entry.Active || entry.Retiring) && entry.FrameId < oldest)
                oldest = entry.FrameId;
        }
        return oldest == ulong.MaxValue ? 0UL : oldest;
    }

    private InFlightSubmission? FindOldestSubmissionNoLock()
    {
        InFlightSubmission? oldest = null;
        for (int i = 0; i < _inFlight.Length; i++)
        {
            InFlightSubmission entry = _inFlight[i];
            if (!entry.Active || entry.Retiring || entry.PendingCommit || entry.Cancelled)
                continue;
            if (oldest is null || entry.FrameId < oldest.FrameId)
                oldest = entry;
        }
        return oldest;
    }

    /// <summary>
    /// Reserves one bounded ownership slot before any eye command buffer is
    /// recorded. The ticket must be consumed by <see cref="RegisterSubmission"/>
    /// after queue acceptance or cancelled by the caller before submission.
    /// </summary>
    public bool TryReserveSubmission(out SubmissionAdmissionTicket? ticket,
        int maxInFlight = DefaultMaxInFlightSubmissions,
        uint timeoutMs = DefaultRecoveryWaitTimeoutMs)
    {
        ticket = null;
        PollCompletions();
        lock (_gate)
        {
            if (CountActiveSubmissionsNoLock() + _reservedSubmissionCount < maxInFlight)
            {
                return TryTakeInactiveTicketNoLock(out ticket);
            }
        }

        if (!EnsureInFlightBudget(maxInFlight, timeoutMs))
            return false;

        lock (_gate)
        {
            if (CountActiveSubmissionsNoLock() + _reservedSubmissionCount >= maxInFlight)
                return false;

            return TryTakeInactiveTicketNoLock(out ticket);
        }
    }

    private bool TryTakeInactiveTicketNoLock(out SubmissionAdmissionTicket? ticket)
    {
        for (int i = 0; i < _admissionSlots.Length; i++)
        {
            AdmissionSlot candidate = _admissionSlots[i];
            if (candidate.Active)
                continue;

            candidate.Active = true;
            candidate.Generation++;
            candidate.PreparedSlotIndex = -1;
            _reservedSubmissionCount++;
            ticket = new SubmissionAdmissionTicket(this, i, candidate.Generation);
            return true;
        }

        ticket = null;
        return false;
    }

    public void CancelReservation(SubmissionAdmissionTicket? ticket)
    {
        if (ticket is null)
            return;

        lock (_gate)
        {
            if (!TryGetActiveAdmissionSlotNoLock(ticket.Value, out AdmissionSlot slot) ||
                slot.PreparedSlotIndex >= 0)
                return;

            slot.Active = false;
            _reservedSubmissionCount--;
        }
    }

    /// <summary>
    /// Atomically registers an asynchronous OpenXR eye queue submission.
    /// Transfers ownership of recorded command buffers, uploads, arena slots,
    /// and input leases until GPU timeline completion is proven.
    /// </summary>
    public bool RegisterSubmission(
        in SubmissionAdmissionTicket ticket,
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
        IReadOnlyList<VulkanImportedTexturePendingUpload>? additionalUploads,
        VulkanSemaphore timelineSemaphore,
        ulong timelineValue,
        VulkanMappedFrameArena? mappedFrameArena,
        ulong mappedFrameGeneration,
        VulkanFrameDataArena? frameDataArena,
        ulong frameDataGeneration,
        ReadOnlySpan<uint> frameSlots,
        long submitStartTimestamp,
        long submitEndTimestamp,
        CommandBuffer temporaryCommandBuffer = default)
    {
        if (frameSlots.Length > MaxTrackedCommandBuffers ||
            (uploads?.Count ?? 0) + (additionalUploads?.Count ?? 0) > MaxTrackedUploads)
            throw new InvalidOperationException(
                "OpenXR submission payload exceeds the fixed tracker ownership capacity.");

        uint oldestAge = 0;
        uint imageReuseAge = 0;
        int inFlightSnapshot;

        lock (_gate)
        {
            if (!TryGetActiveAdmissionSlotNoLock(ticket, out AdmissionSlot admissionSlot) || admissionSlot.PreparedSlotIndex >= 0)
                throw new InvalidOperationException("OpenXR submission registration requires an active admission ticket.");

            InFlightSubmission? entry = FindReusableSubmissionNoLock();
            if (entry is null)
                throw new InvalidOperationException("OpenXR admission ticket has no reusable tracker ownership slot.");

            entry.FrameId = frameId;
            entry.PredictedDisplayTime = predictedDisplayTime;
            entry.ViewMask = viewMask;
            entry.LeftImageIndex = leftImageIndex;
            entry.RightImageIndex = rightImageIndex;
            entry.FirstRecorded = firstRecorded;
            entry.HasFirst = hasFirst;
            entry.SecondRecorded = secondRecorded;
            entry.HasSecond = hasSecond;
            entry.FirstPrepared = firstPrepared;
            entry.HasFirstPrepared = hasFirstPrepared;
            entry.SecondPrepared = secondPrepared;
            entry.HasSecondPrepared = hasSecondPrepared;
            entry.TemporaryCommandBuffer = temporaryCommandBuffer;
            entry.HasTemporaryCommandBuffer = temporaryCommandBuffer.Handle != 0;
            entry.TimelineSemaphore = timelineSemaphore;
            entry.TimelineValue = timelineValue;
            entry.MappedFrameArena = mappedFrameArena;
            entry.MappedFrameGeneration = mappedFrameGeneration;
            entry.FrameDataArena = frameDataArena;
            entry.FrameDataGeneration = frameDataGeneration;
            entry.FrameSlotCount = frameSlots.Length;
            frameSlots.CopyTo(entry.FrameSlots);
            entry.UploadCount = uploads?.Count ?? 0;
            for (int i = 0; i < entry.UploadCount; i++)
                entry.Uploads[i] = uploads![i];
            int additionalUploadCount = additionalUploads?.Count ?? 0;
            for (int i = 0; i < additionalUploadCount; i++)
                entry.Uploads[entry.UploadCount + i] = additionalUploads![i];
            entry.UploadCount += additionalUploadCount;
            entry.SubmitStartTimestamp = submitStartTimestamp;
            entry.SubmitEndTimestamp = submitEndTimestamp;
            entry.EnqueuedTimestamp = Stopwatch.GetTimestamp();
            entry.CompletionProven = false;
            entry.Reopened = false;
            entry.Retiring = false;
            entry.PendingCommit = false;
            entry.NativeSubmissionAccepted = false;
            entry.Cancelled = false;
            entry.UploadSettlementIndex = 0;
            entry.MappedFrameSlotResetCount = 0;
            entry.FrameDataSlotResetCount = 0;
            entry.RetiredCallbackInvoked = false;
            entry.TicketGeneration = ticket.Generation;
            int preparedIndex = Array.IndexOf(_inFlight, entry);
            inFlightSnapshot = CountActiveSubmissionsNoLock() + 1;
            ulong oldestFrame = FindOldestFrameNoLock();
            if (oldestFrame != 0)
                oldestAge = (uint)Math.Min(frameId >= oldestFrame ? frameId - oldestFrame : 0UL, uint.MaxValue);

            if (hasFirst)
                RecordImageReuseAgeNoLock(
                    firstRecorded.OpenXrViewIndex,
                    leftImageIndex,
                    frameId,
                    ref imageReuseAge);
            if (hasSecond)
                RecordImageReuseAgeNoLock(
                    secondRecorded.OpenXrViewIndex,
                    rightImageIndex,
                    frameId,
                    ref imageReuseAge);

            // All validation, collection reads and telemetry calculations precede
            // the ownership boundary. Nothing after these assignments may throw
            // to the caller and return a registered payload to local cleanup.
            entry.PendingCommit = true;
            entry.Active = true;
            admissionSlot.PreparedSlotIndex = preparedIndex;
            _reservedSubmissionCount--;
        }

        try
        {
            RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeInFlightStats((uint)inFlightSnapshot, oldestAge, imageReuseAge);
            if (_commandRuntime.IsOpenXrTraceEnabled)
                Debug.Vulkan("[OpenXR.Tracker] Registered submission frame={0} pendingQueueAcceptance=True inFlight={1}", frameId, inFlightSnapshot);
        }
        catch (Exception ex)
        {
            try { Debug.VulkanWarning("[OpenXR.Tracker] Post-registration telemetry failed: {0}", ex.Message); }
            catch { /* Diagnostics cannot reverse ownership transfer. */ }
        }
        return true;
    }

    private void RecordImageReuseAgeNoLock(
        uint viewIndex,
        uint imageIndex,
        ulong frameId,
        ref uint imageReuseAge)
    {
        if (imageIndex >= MaxTrackedSwapchainImages)
            return;

        ulong[] imageLastFrames = viewIndex == 0
            ? _leftImageLastFrame
            : _rightImageLastFrame;
        ulong previousFrame = imageLastFrames[imageIndex];
        if (previousFrame > 0)
        {
            imageReuseAge = Math.Max(
                imageReuseAge,
                (uint)Math.Min(frameId >= previousFrame ? frameId - previousFrame : 0UL, uint.MaxValue));
        }

        imageLastFrames[imageIndex] = frameId;
    }

    /// <summary>Publishes the exact receipt for a pre-populated ownership slot.</summary>
    public void CommitAcceptedSubmission(
        in SubmissionAdmissionTicket ticket,
        VulkanSemaphore completionSemaphore,
        ulong completionValue,
        long submitStartTimestamp,
        long submitEndTimestamp)
    {
        lock (_gate)
        {

            if (!TryGetActiveAdmissionSlotNoLock(ticket, out AdmissionSlot admissionSlot) ||
                admissionSlot.PreparedSlotIndex < 0 ||
                admissionSlot.PreparedSlotIndex >= _inFlight.Length)
            {
                MarkCommitInvariantFailureNoThrow(ticket);
                return;
            }

            InFlightSubmission entry = _inFlight[admissionSlot.PreparedSlotIndex];
            if (!entry.Active || !entry.PendingCommit || entry.TicketGeneration != ticket.Generation)
            {
                MarkCommitInvariantFailureNoThrow(ticket);
                return;
            }

            // Queue acceptance is irrevocable even if the receipt is corrupt.
            // Quarantine must never allow cancellation to reclaim this payload.
            entry.NativeSubmissionAccepted = true;
            if (completionSemaphore.Handle == 0 || completionValue == 0)
            {
                MarkCommitInvariantFailureNoThrow(ticket);
                return;
            }
            entry.TimelineSemaphore = completionSemaphore;
            entry.TimelineValue = completionValue;
            entry.SubmitStartTimestamp = submitStartTimestamp;
            entry.SubmitEndTimestamp = submitEndTimestamp;
            entry.PendingCommit = false;
            _latestAcceptedCompletionSemaphore = completionSemaphore;
            _latestAcceptedCompletionValue = completionValue;
            admissionSlot.Active = false;
            admissionSlot.PreparedSlotIndex = -1;
        }
    }

    public void CancelPreparedSubmission(SubmissionAdmissionTicket? ticket)
    {
        if (ticket is null)
            return;

        InFlightSubmission? entry = null;
        lock (_gate)
        {
            if (!TryGetActiveAdmissionSlotNoLock(ticket.Value, out AdmissionSlot admissionSlot))
                return;

            bool releasesReservation = admissionSlot.PreparedSlotIndex < 0;
            if (admissionSlot.PreparedSlotIndex >= 0 && admissionSlot.PreparedSlotIndex < _inFlight.Length)
            {
                InFlightSubmission preparedEntry = _inFlight[admissionSlot.PreparedSlotIndex];
                if (preparedEntry.Active && preparedEntry.PendingCommit && preparedEntry.TicketGeneration == ticket.Value.Generation)
                {
                    if (preparedEntry.NativeSubmissionAccepted)
                        return;
                    preparedEntry.Cancelled = true;
                    preparedEntry.PendingCommit = false;
                    preparedEntry.Retiring = true;
                    entry = preparedEntry;
                }
            }
            admissionSlot.Active = false;
            admissionSlot.PreparedSlotIndex = -1;
            if (releasesReservation)
                _reservedSubmissionCount--;
        }

        if (entry is not null)
            SettleCancelledSubmission(entry);
    }

    private void MarkCommitInvariantFailureNoThrow(in SubmissionAdmissionTicket ticket)
    {
        // This is unreachable when a ticket is registered before submission,
        // but native queue acceptance cannot be rolled back if the invariant is
        // violated. Preserve the conservative device-lost quarantine instead of
        // throwing through the irrevocable ownership boundary.
        _commandRuntime.MarkTrackedDeviceLost();
        Debug.VulkanWarning(
            "[OpenXR.Tracker] Accepted submission lost its prepared ownership slot. ticket={0}:{1}",
            ticket.AdmissionSlotIndex,
            ticket.Generation);
    }

    private bool IsTicketActive(in SubmissionAdmissionTicket ticket)
    {
        lock (_gate)
            return TryGetActiveAdmissionSlotNoLock(ticket, out _);
    }

    private bool TryGetActiveAdmissionSlotNoLock(
        in SubmissionAdmissionTicket ticket,
        out AdmissionSlot slot)
    {
        if (ticket.AdmissionSlotIndex < 0 ||
            ticket.AdmissionSlotIndex >= _admissionSlots.Length)
        {
            slot = null!;
            return false;
        }

        slot = _admissionSlots[ticket.AdmissionSlotIndex];
        return slot.Active && slot.Generation == ticket.Generation;
    }

    /// <summary>
    /// Non-blockingly queries completion of in-flight submissions and retires
    /// completed resources, reopening arena slots and publishing texture uploads.
    /// </summary>
    public int PollCompletions()
    {
        if (!_commandRuntime.DeviceContext.IsOperational)
            return 0;

        Span<int> readyToRetire = stackalloc int[DefaultMaxInFlightSubmissions];
        int readyCount = 0;

        lock (_gate)
        {
            for (int i = 0; i < _inFlight.Length; i++)
            {
                InFlightSubmission entry = _inFlight[i];
                if (!entry.Active || entry.Retiring || entry.PendingCommit)
                    continue;
                if (entry.Cancelled)
                {
                    entry.Retiring = true;
                    readyToRetire[readyCount++] = i;
                    continue;
                }
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

                entry.Retiring = true;
                readyToRetire[readyCount++] = i;
            }
        }

        int retiredCount = 0;
        for (int i = 0; i < readyCount; i++)
        {
            InFlightSubmission completed = _inFlight[readyToRetire[i]];
            if (completed.Cancelled)
            {
                SettleCancelledSubmission(completed);
                continue;
            }
            bool retired = false;
            try
            {
                retired = RetireCompletedSubmission(completed);
                if (!retired)
                    continue;
                Volatile.Write(ref _lastCompletedFrameId, completed.FrameId);
                Interlocked.Increment(ref _completedSubmissionCount);
                retiredCount++;
            }
            finally
            {
                lock (_gate)
                {
                    completed.Active = !retired;
                    completed.Retiring = false;
                }
            }
        }

        return retiredCount;
    }

    private bool RetireCompletedSubmission(InFlightSubmission entry)
    {
        if (entry.Reopened)
            return true;

        if (!SettleUploads(entry, publish: true) ||
            !InvokeRetiredCallbackOnce(entry) ||
            !ReleaseRecordedCommandBuffers(entry) ||
            !ReleaseTemporaryCommandBuffer(entry) ||
            !ReleasePreparedInputs(entry) ||
            !ReopenArenas(entry))
            return false;

        entry.Reopened = true;

        if (_commandRuntime.IsOpenXrTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXR.Tracker] Retired submission frame={0} timelineValue={1} frameSlots={2}",
                entry.FrameId,
                entry.TimelineValue,
                entry.FrameSlotCount);
        }

        return true;
    }

    private void SettleCancelledSubmission(InFlightSubmission entry)
    {
        bool settled = SettleUploads(entry, publish: false) &&
            ReleaseRecordedCommandBuffers(entry) &&
            ReleaseTemporaryCommandBuffer(entry) &&
            ReleasePreparedInputs(entry) &&
            ReopenArenas(entry);
        lock (_gate)
        {
            entry.Active = !settled;
            entry.Retiring = false;
        }
    }

    private bool SettleUploads(InFlightSubmission entry, bool publish)
    {
        while (entry.UploadSettlementIndex < entry.UploadCount)
        {
            try
            {
                ReadOnlySpan<VulkanImportedTexturePendingUpload> upload =
                    entry.Uploads.AsSpan(entry.UploadSettlementIndex, 1);
                if (publish)
                    _commandRuntime.PublishOpenXrRecordedTextureUploads(upload, "OpenXR eye async completion");
                else
                    _commandRuntime.CancelOpenXrRecordedTextureUploads(upload, "OpenXR prepared submission rejected");
                entry.Uploads[entry.UploadSettlementIndex++] = null!;
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning("[OpenXR.Tracker] Upload settlement failed for frame {0}: {1}", entry.FrameId, ex.Message);
                return false;
            }
        }
        entry.UploadCount = 0;
        entry.UploadSettlementIndex = 0;
        return true;
    }

    private bool InvokeRetiredCallbackOnce(InFlightSubmission entry)
    {
        if (entry.RetiredCallbackInvoked)
            return true;

        entry.RetiredCallbackInvoked = true;
        try { _onSubmissionRetired?.Invoke(entry); }
        catch (Exception ex)
        {
            Debug.VulkanWarning("[OpenXR.Tracker] Retirement callback failed for frame {0}: {1}", entry.FrameId, ex.Message);
        }
        return true;
    }

    private bool ReleaseRecordedCommandBuffers(InFlightSubmission entry)
    {
        try
        {
            if (entry.HasFirst)
            {
                if (_freeCommandBuffer is not null) _freeCommandBuffer(entry.FirstRecorded);
                else FreeRecordedCommandBufferDirect(entry.FirstRecorded);
                entry.FirstRecorded = default;
                entry.HasFirst = false;
            }
            if (entry.HasSecond)
            {
                if (_freeCommandBuffer is not null) _freeCommandBuffer(entry.SecondRecorded);
                else FreeRecordedCommandBufferDirect(entry.SecondRecorded);
                entry.SecondRecorded = default;
                entry.HasSecond = false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning("[OpenXR.Tracker] Command-buffer release failed for frame {0}: {1}", entry.FrameId, ex.Message);
            return false;
        }
    }

    private bool ReleaseTemporaryCommandBuffer(InFlightSubmission entry)
    {
        if (!entry.HasTemporaryCommandBuffer)
            return true;
        try
        {
            _commandRuntime.ReleaseOpenXrTemporaryCommandBuffer(entry.TemporaryCommandBuffer, EVulkanQueueSubmissionDisposition.Completed);
            entry.TemporaryCommandBuffer = default;
            entry.HasTemporaryCommandBuffer = false;
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning("[OpenXR.Tracker] Temporary command-buffer release failed for frame {0}: {1}", entry.FrameId, ex.Message);
            return false;
        }
    }

    private static bool ReleasePreparedInputs(InFlightSubmission entry)
    {
        try
        {
            if (entry.HasFirstPrepared && entry.FirstPrepared.Ops is { } firstOps)
                VulkanAdvancedVisibilityInputLease.ReleaseOperations(firstOps);
            entry.FirstPrepared = default;
            entry.HasFirstPrepared = false;
            if (entry.HasSecondPrepared && entry.SecondPrepared.Ops is { } secondOps)
                VulkanAdvancedVisibilityInputLease.ReleaseOperations(secondOps);
            entry.SecondPrepared = default;
            entry.HasSecondPrepared = false;
            return true;
        }
        catch { return false; }
    }

    private static bool ReopenArenas(InFlightSubmission entry)
    {
        if (entry.MappedFrameArena is not null)
            while (entry.MappedFrameSlotResetCount < entry.FrameSlotCount)
            {
                int index = entry.MappedFrameSlotResetCount;
                if (!entry.MappedFrameArena.TryResetFrameSlot(entry.FrameSlots[index], entry.MappedFrameGeneration, submissionCompletionProven: true))
                    return false;
                entry.MappedFrameSlotResetCount++;
            }
        if (entry.FrameDataArena is not null)
            while (entry.FrameDataSlotResetCount < entry.FrameSlotCount)
            {
                int index = entry.FrameDataSlotResetCount;
                if (!entry.FrameDataArena.TryResetFrameSlot(entry.FrameSlots[index], entry.FrameDataGeneration, submissionCompletionProven: true))
                    return false;
                entry.FrameDataSlotResetCount++;
            }
        return true;
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
            if (CountOwnedSubmissionsNoLock() < maxInFlight)
                return true;

            oldest = FindOldestSubmissionNoLock();
        }

        if (oldest is null || !_commandRuntime.DeviceContext.IsOperational)
            return false;

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
        lock (_gate)
            return CountOwnedSubmissionsNoLock() < maxInFlight;
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
            int ownedCount;
            lock (_gate)
            {
                if (CountOwnedSubmissionsNoLock() == 0)
                    break;
                pending = FindOldestSubmissionNoLock();
                ownedCount = CountOwnedSubmissionsNoLock();
            }

            if (!_commandRuntime.DeviceContext.IsOperational)
            {
                Debug.VulkanWarning(
                    "[OpenXR.Tracker] DrainAll cannot settle {0} owned submission(s) because the Vulkan device is not operational.",
                    ownedCount);
                return false;
            }

            if (pending is null)
            {
                long pendingRemainingTicks = deadline - Stopwatch.GetTimestamp();
                if (pendingRemainingTicks <= 0)
                {
                    Debug.VulkanWarning(
                        "[OpenXR.Tracker] DrainAll timed out after {0}ms with {1} reserved or pending-commit submission(s).",
                        timeoutMs,
                        ownedCount);
                    return false;
                }

                Thread.Yield();
                continue;
            }
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
