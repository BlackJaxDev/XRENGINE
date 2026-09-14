using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.API.Rendering.OpenXR;
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
    internal const int MaxSubmissionValidationEntries = 32;
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
        public bool FirstPreparedReleased;
        public bool SecondPreparedReleased;
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
        public int ResidentLifetimeReleaseIndex;
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
        public bool AbandonedAfterDeviceLoss;
        public int UploadSettlementIndex;
        public int MappedFrameSlotResetCount;
        public int FrameDataSlotResetCount;
        public bool RetiredCallbackInvoked;
        public ulong TicketGeneration;
        public int AdmissionSlotIndex = -1;
        public int ValidationLedgerIndex = -1;
        public bool SubmissionValidationTracked;
        public bool SubmissionValidationAcceptedCounted;
        public bool SubmissionValidationRejectedCounted;
        public bool SubmissionValidationPublicationFailureCounted;
        public bool SubmissionValidationCompletionCounted;
        public bool SubmissionValidationRetiredCounted;
        public bool SubmissionValidationAbandonedCounted;
    }

    private struct SubmissionValidationLedgerRecord
    {
        public long Serial;
        public long RuntimeEpoch;
        public int AdmissionSlotIndex;
        public ulong TicketGeneration;
        public ulong FrameId;
        public long PredictedDisplayTime;
        public EOpenXrSubmissionShape Shape;
        public EOpenXrSubmissionPayloadKind PayloadKinds;
        public uint CommandCount;
        public bool AcceptedCommandShapeMatches;
        public uint ViewMask;
        public uint LeftImageIndex;
        public uint RightImageIndex;
        public uint FirstViewIndex;
        public uint FirstImageIndex;
        public uint SecondViewIndex;
        public uint SecondImageIndex;
        public int RecordedCommandCount;
        public int PreparedInputCount;
        public int TemporaryCommandCount;
        public int UploadCount;
        public int FrameSlotCount;
        public int ExternalTargetCount;
        public ulong TimelineValue;
        public int ReceiptResult;
        public bool LifetimePinsTransferred;
        public bool PostSubmissionPublicationSucceeded;
        public EOpenXrSubmissionDisposition Disposition;
        public bool SubmissionAccepted;
        public bool AcceptedIncompleteObserved;
        public bool OwnershipIntactWhenAcceptedIncomplete;
        public bool CompletionProven;
        public bool Cancelled;
        public bool AbandonedAfterDeviceLoss;
        public int UploadSettlementCount;
        public int RecordedReleaseCount;
        public int PreparedReleaseCount;
        public int TemporaryReleaseCount;
        public int MappedFrameSlotResetCount;
        public int FrameDataSlotResetCount;
        public int RetiredCallbackCount;
        public bool Retired;
        public int EarlySettlementViolationCount;
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
    private readonly object _settlementGate = new();
    private int _settlementDepth;
    private bool _deviceLossAbandonRequested;

    private int _forcedWaitCount;
    private int _reservedSubmissionCount;
    private int _completedSubmissionCount;
    private ulong _lastCompletedFrameId;
    private readonly ulong[] _leftImageLastFrame = new ulong[MaxTrackedSwapchainImages];
    private readonly ulong[] _rightImageLastFrame = new ulong[MaxTrackedSwapchainImages];
    private VulkanSemaphore _latestAcceptedCompletionSemaphore;
    private ulong _latestAcceptedCompletionValue;
    private readonly SubmissionValidationLedgerRecord[] _submissionValidationLedger =
        new SubmissionValidationLedgerRecord[MaxSubmissionValidationEntries];
    private OpenXrSubmissionValidationRequest _submissionValidationRequest;
    private int _submissionValidationLedgerCount;
    private int _submissionValidationOverflowCount;
    private long _submissionValidationSerial;
    private long _submissionValidationRuntimeEpoch;
    private int _submissionValidationAdmissionHighWater;
    private int _submissionValidationAcceptedCount;
    private int _submissionValidationRejectedCount;
    private int _submissionValidationPublicationFailureCount;
    private int _submissionValidationRealCompletionCount;
    private int _submissionValidationRetiredCount;
    private long _submissionValidationAbandonedCount;
    private int _submissionValidationEnabled;
    private int _disposed;
    private Action<InFlightSubmission, uint>? _onSubmissionFrameSlotLifetimeSettled;

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
        VulkanOpenXrSubmissionValidationState.ConfigureNewTracker(this);
    }

    internal void ConfigureSubmissionValidation(in OpenXrSubmissionValidationRequest request)
    {
        lock (_gate)
        {
            _submissionValidationRequest = request with
            {
                LedgerCapacity = Math.Clamp(request.LedgerCapacity, 0, MaxSubmissionValidationEntries),
            };
            _submissionValidationLedgerCount = 0;
            _submissionValidationOverflowCount = 0;
            _submissionValidationSerial = 0;
            _submissionValidationAdmissionHighWater = 0;
            _submissionValidationAcceptedCount = 0;
            _submissionValidationRejectedCount = 0;
            _submissionValidationPublicationFailureCount = 0;
            _submissionValidationRealCompletionCount = 0;
            _submissionValidationRetiredCount = 0;
            _submissionValidationAbandonedCount = 0;
            Array.Clear(_submissionValidationLedger);
            for (int i = 0; i < _inFlight.Length; i++)
            {
                InFlightSubmission entry = _inFlight[i];
                entry.ValidationLedgerIndex = -1;
                entry.SubmissionValidationTracked = false;
                entry.SubmissionValidationAcceptedCounted = false;
                entry.SubmissionValidationRejectedCounted = false;
                entry.SubmissionValidationPublicationFailureCounted = false;
                entry.SubmissionValidationCompletionCounted = false;
                entry.SubmissionValidationRetiredCounted = false;
                entry.SubmissionValidationAbandonedCounted = false;
            }
            Volatile.Write(ref _submissionValidationEnabled, _submissionValidationRequest.Enabled ? 1 : 0);
        }
        VulkanOpenXrSubmissionValidationState.UpdateRegistration(this, request.Enabled);
    }

    internal void SetSubmissionValidationRuntimeEpoch(long runtimeEpoch)
    {
        lock (_gate)
            _submissionValidationRuntimeEpoch = runtimeEpoch;
    }

    internal OpenXrSubmissionValidationSnapshot CaptureSubmissionValidation()
    {
        lock (_gate)
        {
            var snapshot = new OpenXrSubmissionValidationSnapshot
            {
                Request = _submissionValidationRequest,
                OverflowCount = _submissionValidationOverflowCount,
                AdmissionHighWater = _submissionValidationAdmissionHighWater,
                AdmissionCapacity = DefaultMaxInFlightSubmissions,
                ForcedWaitCount = _forcedWaitCount,
                ActiveCount = CountActiveSubmissionsNoLock(),
                ReservedCount = _reservedSubmissionCount,
                AcceptedCount = _submissionValidationAcceptedCount,
                RejectedCount = _submissionValidationRejectedCount,
                PublicationFailureCount = _submissionValidationPublicationFailureCount,
                RealCompletionCount = _submissionValidationRealCompletionCount,
                RetiredCount = _submissionValidationRetiredCount,
                AbandonedSubmissionCount = _submissionValidationAbandonedCount,
            };
            snapshot.Entries = new OpenXrSubmissionOwnershipLedgerEntry[_submissionValidationLedgerCount];
            for (int i = 0; i < _submissionValidationLedgerCount; i++)
            {
                SubmissionValidationLedgerRecord record = _submissionValidationLedger[i];
                snapshot.Entries[i] = new OpenXrSubmissionOwnershipLedgerEntry
                {
                    Serial = record.Serial,
                    RuntimeEpoch = record.RuntimeEpoch,
                    AdmissionSlotIndex = record.AdmissionSlotIndex,
                    TicketGeneration = record.TicketGeneration,
                    FrameId = record.FrameId,
                    PredictedDisplayTime = record.PredictedDisplayTime,
                    Shape = record.Shape,
                    PayloadKinds = record.PayloadKinds,
                    CommandCount = record.CommandCount,
                    AcceptedCommandShapeMatches = record.AcceptedCommandShapeMatches,
                    ViewMask = record.ViewMask,
                    LeftImageIndex = record.LeftImageIndex,
                    RightImageIndex = record.RightImageIndex,
                    FirstViewIndex = record.FirstViewIndex,
                    FirstImageIndex = record.FirstImageIndex,
                    SecondViewIndex = record.SecondViewIndex,
                    SecondImageIndex = record.SecondImageIndex,
                    RecordedCommandCount = record.RecordedCommandCount,
                    PreparedInputCount = record.PreparedInputCount,
                    TemporaryCommandCount = record.TemporaryCommandCount,
                    UploadCount = record.UploadCount,
                    FrameSlotCount = record.FrameSlotCount,
                    ExternalTargetCount = record.ExternalTargetCount,
                    TimelineValue = record.TimelineValue,
                    ReceiptResult = record.ReceiptResult,
                    SubmissionAccepted = record.SubmissionAccepted,
                    LifetimePinsTransferred = record.LifetimePinsTransferred,
                    PostSubmissionPublicationSucceeded = record.PostSubmissionPublicationSucceeded,
                    Disposition = record.Disposition,
                    AcceptedIncompleteObserved = record.AcceptedIncompleteObserved,
                    OwnershipIntactWhenAcceptedIncomplete = record.OwnershipIntactWhenAcceptedIncomplete,
                    CompletionProven = record.CompletionProven,
                    Cancelled = record.Cancelled,
                    AbandonedAfterDeviceLoss = record.AbandonedAfterDeviceLoss,
                    UploadSettlementCount = record.UploadSettlementCount,
                    RecordedReleaseCount = record.RecordedReleaseCount,
                    PreparedReleaseCount = record.PreparedReleaseCount,
                    TemporaryReleaseCount = record.TemporaryReleaseCount,
                    MappedFrameSlotResetCount = record.MappedFrameSlotResetCount,
                    FrameDataSlotResetCount = record.FrameDataSlotResetCount,
                    RetiredCallbackCount = record.RetiredCallbackCount,
                    Retired = record.Retired,
                    EarlySettlementViolationCount = record.EarlySettlementViolationCount,
                };
            }
            for (int i = 0; i < _inFlight.Length; i++)
                if (_inFlight[i].PendingCommit)
                    snapshot.PendingCommitCount++;
            return snapshot;
        }
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
        if (Volatile.Read(ref _disposed) != 0 || !_commandRuntime.DeviceContext.IsOperational)
            return false;
        PollCompletions();
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_commandRuntime.DeviceContext.IsOperational)
                return false;
            if (CountActiveSubmissionsNoLock() + _reservedSubmissionCount < maxInFlight)
            {
                return TryTakeInactiveTicketNoLock(out ticket);
            }
        }

        if (!EnsureInFlightBudget(maxInFlight, timeoutMs))
            return false;

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_commandRuntime.DeviceContext.IsOperational)
                return false;
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
            if (_submissionValidationRequest.Enabled)
                _submissionValidationAdmissionHighWater = Math.Max(
                    _submissionValidationAdmissionHighWater,
                    CountOwnedSubmissionsNoLock());
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
        CommandBuffer temporaryCommandBuffer = default,
        EOpenXrSubmissionShape submissionShape = EOpenXrSubmissionShape.Unknown)
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
            if (Volatile.Read(ref _disposed) != 0 ||
                !_commandRuntime.DeviceContext.IsOperational ||
                !TryGetActiveAdmissionSlotNoLock(ticket, out AdmissionSlot admissionSlot) ||
                admissionSlot.PreparedSlotIndex >= 0)
                return false;

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
            entry.FirstPreparedReleased = false;
            entry.SecondPreparedReleased = false;
            entry.TemporaryCommandBuffer = temporaryCommandBuffer;
            entry.HasTemporaryCommandBuffer = temporaryCommandBuffer.Handle != 0;
            entry.TimelineSemaphore = timelineSemaphore;
            entry.TimelineValue = timelineValue;
            entry.MappedFrameArena = mappedFrameArena;
            entry.MappedFrameGeneration = mappedFrameGeneration;
            entry.FrameDataArena = frameDataArena;
            entry.FrameDataGeneration = frameDataGeneration;
            entry.FrameSlotCount = frameSlots.Length;
            entry.ResidentLifetimeReleaseIndex = 0;
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
            entry.AbandonedAfterDeviceLoss = false;
            entry.UploadSettlementIndex = 0;
            entry.MappedFrameSlotResetCount = 0;
            entry.FrameDataSlotResetCount = 0;
            entry.RetiredCallbackInvoked = false;
            entry.TicketGeneration = ticket.Generation;
            entry.AdmissionSlotIndex = ticket.AdmissionSlotIndex;
            entry.ValidationLedgerIndex = -1;
            entry.SubmissionValidationTracked = false;
            entry.SubmissionValidationAcceptedCounted = false;
            entry.SubmissionValidationRejectedCounted = false;
            entry.SubmissionValidationPublicationFailureCounted = false;
            entry.SubmissionValidationCompletionCounted = false;
            entry.SubmissionValidationRetiredCounted = false;
            entry.SubmissionValidationAbandonedCounted = false;
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
            RecordSubmissionValidationRegistrationNoLock(
                entry,
                submissionShape,
                hasFirst,
                hasSecond,
                hasFirstPrepared,
                hasSecondPrepared,
                temporaryCommandBuffer.Handle != 0,
                inFlightSnapshot);
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

    internal void SetSubmissionFrameSlotLifetimeSettledCallback(Action<InFlightSubmission, uint> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
            _onSubmissionFrameSlotLifetimeSettled = callback;
    }

    private void RecordSubmissionValidationRegistrationNoLock(
        InFlightSubmission entry,
        EOpenXrSubmissionShape shape,
        bool hasFirst,
        bool hasSecond,
        bool hasFirstPrepared,
        bool hasSecondPrepared,
        bool hasTemporaryCommandBuffer,
        int inFlightCount)
    {
        if (!_submissionValidationRequest.Enabled ||
            (_submissionValidationRequest.RequiredShape != EOpenXrSubmissionShape.Unknown &&
             _submissionValidationRequest.RequiredShape != shape))
            return;

        entry.SubmissionValidationTracked = true;
        _submissionValidationAdmissionHighWater = Math.Max(_submissionValidationAdmissionHighWater, inFlightCount);
        int capacity = _submissionValidationRequest.LedgerCapacity;
        if (_submissionValidationLedgerCount >= capacity)
        {
            _submissionValidationOverflowCount++;
            return;
        }

        int index = _submissionValidationLedgerCount++;
        EOpenXrSubmissionPayloadKind payloadKinds = EOpenXrSubmissionPayloadKind.None;
        if (hasFirst)
            payloadKinds |= entry.FirstRecorded.OpenXrViewIndex == 0
                ? EOpenXrSubmissionPayloadKind.Eye0
                : entry.FirstRecorded.OpenXrViewIndex == 1
                    ? EOpenXrSubmissionPayloadKind.Eye1
                    : EOpenXrSubmissionPayloadKind.None;
        if (hasSecond)
            payloadKinds |= entry.SecondRecorded.OpenXrViewIndex == 0
                ? EOpenXrSubmissionPayloadKind.Eye0
                : entry.SecondRecorded.OpenXrViewIndex == 1
                    ? EOpenXrSubmissionPayloadKind.Eye1
                    : EOpenXrSubmissionPayloadKind.None;
        if (hasTemporaryCommandBuffer)
            payloadKinds |= EOpenXrSubmissionPayloadKind.OtherTemporary;
        if (shape is EOpenXrSubmissionShape.EyeWithPublish or
            EOpenXrSubmissionShape.PairedEyesWithPublish or
            EOpenXrSubmissionShape.MirrorPublish)
            payloadKinds |= EOpenXrSubmissionPayloadKind.Publish;
        if (shape == EOpenXrSubmissionShape.PreviewCopy)
            payloadKinds |= EOpenXrSubmissionPayloadKind.Preview;
        _submissionValidationLedger[index] = new SubmissionValidationLedgerRecord
        {
            Serial = ++_submissionValidationSerial,
            RuntimeEpoch = _submissionValidationRuntimeEpoch,
            AdmissionSlotIndex = entry.AdmissionSlotIndex,
            TicketGeneration = entry.TicketGeneration,
            FrameId = entry.FrameId,
            PredictedDisplayTime = entry.PredictedDisplayTime,
            Shape = shape,
            PayloadKinds = payloadKinds,
            CommandCount = (uint)((hasFirst ? 1 : 0) + (hasSecond ? 1 : 0) + (hasTemporaryCommandBuffer ? 1 : 0)),
            ViewMask = entry.ViewMask,
            LeftImageIndex = entry.LeftImageIndex,
            RightImageIndex = entry.RightImageIndex,
            FirstViewIndex = hasFirst ? entry.FirstRecorded.OpenXrViewIndex : 0U,
            FirstImageIndex = hasFirst ? entry.FirstRecorded.OpenXrImageIndex : 0U,
            SecondViewIndex = hasSecond ? entry.SecondRecorded.OpenXrViewIndex : 0U,
            SecondImageIndex = hasSecond ? entry.SecondRecorded.OpenXrImageIndex : 0U,
            RecordedCommandCount = (hasFirst ? 1 : 0) + (hasSecond ? 1 : 0),
            PreparedInputCount = (hasFirstPrepared ? 1 : 0) + (hasSecondPrepared ? 1 : 0),
            TemporaryCommandCount = hasTemporaryCommandBuffer ? 1 : 0,
            UploadCount = entry.UploadCount,
            FrameSlotCount = entry.FrameSlotCount,
            ExternalTargetCount =
                ((hasFirstPrepared && entry.FirstPrepared.TargetContext.IsValid) ||
                 (hasFirst && entry.FirstRecorded.FrameContext.HasExternalTarget) ? 1 : 0) +
                ((hasSecondPrepared && entry.SecondPrepared.TargetContext.IsValid) ||
                 (hasSecond && entry.SecondRecorded.FrameContext.HasExternalTarget) ? 1 : 0),
        };
        entry.ValidationLedgerIndex = index;
    }

    private void SynchronizeSubmissionValidation(InFlightSubmission entry, bool retired = false)
    {
        if (entry.ValidationLedgerIndex < 0)
            return;
        lock (_gate)
        {
            if ((uint)entry.ValidationLedgerIndex < (uint)_submissionValidationLedgerCount)
            {
                ref SubmissionValidationLedgerRecord record = ref _submissionValidationLedger[entry.ValidationLedgerIndex];
                record.TimelineValue = entry.TimelineValue;
                record.SubmissionAccepted = entry.NativeSubmissionAccepted;
                record.CompletionProven = entry.CompletionProven;
                if (entry.CompletionProven)
                    record.Disposition = EOpenXrSubmissionDisposition.Completed;
                record.Cancelled = entry.Cancelled;
                record.UploadSettlementCount = record.UploadCount - entry.UploadCount + entry.UploadSettlementIndex;
                record.RecordedReleaseCount = record.RecordedCommandCount -
                    (entry.HasFirst ? 1 : 0) - (entry.HasSecond ? 1 : 0);
                record.PreparedReleaseCount = (entry.FirstPreparedReleased ? 1 : 0) + (entry.SecondPreparedReleased ? 1 : 0);
                record.TemporaryReleaseCount = entry.HasTemporaryCommandBuffer ? 0 : record.TemporaryCommandCount;
                record.MappedFrameSlotResetCount = entry.MappedFrameSlotResetCount;
                record.FrameDataSlotResetCount = entry.FrameDataSlotResetCount;
                record.RetiredCallbackCount = entry.RetiredCallbackInvoked ? 1 : 0;
                record.Retired |= retired;
            }
        }
    }

    private void RecordEarlySettlementViolationIfNeeded(InFlightSubmission entry)
    {
        if (!entry.NativeSubmissionAccepted || entry.CompletionProven || entry.ValidationLedgerIndex < 0)
            return;
        lock (_gate)
            if ((uint)entry.ValidationLedgerIndex < (uint)_submissionValidationLedgerCount)
                _submissionValidationLedger[entry.ValidationLedgerIndex].EarlySettlementViolationCount++;
    }

    internal void ObserveSubmissionReceipt(
        in SubmissionAdmissionTicket ticket,
        in VulkanSubmissionReceipt receipt,
        bool acceptedIncomplete,
        EOpenXrSubmissionShape shape,
        uint commandCount,
        CommandBuffer firstCommandBuffer,
        CommandBuffer secondCommandBuffer,
        CommandBuffer thirdCommandBuffer)
    {
        if (Volatile.Read(ref _submissionValidationEnabled) == 0)
            return;

        lock (_gate)
        {
            for (int i = 0; i < _inFlight.Length; i++)
            {
                InFlightSubmission entry = _inFlight[i];
                if (!entry.Active ||
                    entry.AdmissionSlotIndex != ticket.AdmissionSlotIndex ||
                    entry.TicketGeneration != ticket.Generation ||
                    !entry.SubmissionValidationTracked)
                    continue;

                if (receipt.SubmissionAccepted)
                {
                    if (!entry.SubmissionValidationAcceptedCounted)
                    {
                        entry.SubmissionValidationAcceptedCounted = true;
                        _submissionValidationAcceptedCount++;
                    }
                    if (!receipt.PostSubmissionPublicationSucceeded && !entry.SubmissionValidationPublicationFailureCounted)
                    {
                        entry.SubmissionValidationPublicationFailureCounted = true;
                        _submissionValidationPublicationFailureCount++;
                    }
                }
                else if (!entry.SubmissionValidationRejectedCounted)
                {
                    entry.SubmissionValidationRejectedCounted = true;
                    _submissionValidationRejectedCount++;
                }

                if ((uint)entry.ValidationLedgerIndex >= (uint)_submissionValidationLedgerCount)
                    return;

                ref SubmissionValidationLedgerRecord record = ref _submissionValidationLedger[entry.ValidationLedgerIndex];
                record.ReceiptResult = (int)receipt.Result;
                record.SubmissionAccepted = receipt.SubmissionAccepted;
                record.LifetimePinsTransferred = receipt.LifetimePinsTransferred;
                record.PostSubmissionPublicationSucceeded = receipt.PostSubmissionPublicationSucceeded;
                CommandBuffer expectedFirst = entry.HasFirst
                    ? entry.FirstRecorded.CommandBuffer
                    : entry.TemporaryCommandBuffer;
                CommandBuffer expectedSecond = entry.HasSecond
                    ? entry.SecondRecorded.CommandBuffer
                    : entry.HasFirst && entry.HasTemporaryCommandBuffer
                        ? entry.TemporaryCommandBuffer
                        : default;
                CommandBuffer expectedThird = entry.HasSecond && entry.HasTemporaryCommandBuffer
                    ? entry.TemporaryCommandBuffer
                    : default;
                record.AcceptedCommandShapeMatches =
                    record.Shape == shape &&
                    record.CommandCount == commandCount &&
                    expectedFirst.Handle == firstCommandBuffer.Handle &&
                    expectedSecond.Handle == secondCommandBuffer.Handle &&
                    expectedThird.Handle == thirdCommandBuffer.Handle;
                record.Disposition = receipt.SubmissionAccepted
                    ? EOpenXrSubmissionDisposition.SubmittedIncomplete
                    : EOpenXrSubmissionDisposition.NotSubmitted;
                if (acceptedIncomplete && receipt.SubmissionAccepted)
                {
                    record.AcceptedIncompleteObserved = true;
                    record.OwnershipIntactWhenAcceptedIncomplete =
                        entry.Active &&
                        !entry.Retiring &&
                        !entry.Cancelled &&
                        !entry.CompletionProven &&
                        entry.UploadSettlementIndex == 0 &&
                        entry.UploadCount == record.UploadCount &&
                        entry.MappedFrameSlotResetCount == 0 &&
                        entry.FrameDataSlotResetCount == 0 &&
                        !entry.RetiredCallbackInvoked &&
                        (entry.HasFirst ? 1 : 0) + (entry.HasSecond ? 1 : 0) == record.RecordedCommandCount &&
                        (entry.HasFirstPrepared ? 1 : 0) + (entry.HasSecondPrepared ? 1 : 0) == record.PreparedInputCount &&
                        (entry.HasTemporaryCommandBuffer ? 1 : 0) == record.TemporaryCommandCount;
                }
                return;
            }
        }
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
            _latestAcceptedCompletionSemaphore = completionSemaphore;
            _latestAcceptedCompletionValue = completionValue;
            admissionSlot.Active = false;
            admissionSlot.PreparedSlotIndex = -1;
            SynchronizeSubmissionValidation(entry);
        }
    }

    /// <summary>
    /// Opens an accepted submission to completion polling only after its caller
    /// has finished publication and receipt processing. A gateway exception
    /// deliberately leaves the committed entry pending so ownership remains
    /// fail-closed rather than being reclaimed as a rejected submission.
    /// </summary>
    internal void FinalizeAcceptedSubmissionReceipt(in SubmissionAdmissionTicket ticket)
    {
        lock (_gate)
        {
            for (int i = 0; i < _inFlight.Length; i++)
            {
                InFlightSubmission entry = _inFlight[i];
                if (!entry.Active ||
                    !entry.PendingCommit ||
                    !entry.NativeSubmissionAccepted ||
                    entry.AdmissionSlotIndex != ticket.AdmissionSlotIndex ||
                    entry.TicketGeneration != ticket.Generation)
                    continue;

                entry.PendingCommit = false;
                SynchronizeSubmissionValidation(entry);
                return;
            }
        }
    }

    public void CancelPreparedSubmission(SubmissionAdmissionTicket? ticket)
    {
        if (ticket is null)
            return;

        lock (_settlementGate)
        {
            _settlementDepth++;
            try { CancelPreparedSubmissionCore(ticket.Value); }
            finally { EndSettlementNoLock(); }
        }
    }

    private void CancelPreparedSubmissionCore(in SubmissionAdmissionTicket ticket)
    {
        if (ShouldStopNormalSettlement(null))
            return;

        InFlightSubmission? entry = null;
        lock (_gate)
        {
            if (!TryGetActiveAdmissionSlotNoLock(ticket, out AdmissionSlot admissionSlot))
                return;

            bool releasesReservation = admissionSlot.PreparedSlotIndex < 0;
            if (admissionSlot.PreparedSlotIndex >= 0 && admissionSlot.PreparedSlotIndex < _inFlight.Length)
            {
                InFlightSubmission preparedEntry = _inFlight[admissionSlot.PreparedSlotIndex];
                if (preparedEntry.Active && preparedEntry.PendingCommit && preparedEntry.TicketGeneration == ticket.Generation)
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
        lock (_settlementGate)
        {
            _settlementDepth++;
            try { return PollCompletionsCore(); }
            finally { EndSettlementNoLock(); }
        }
    }

    private int PollCompletionsCore()
    {
        if (ShouldStopNormalSettlement(null))
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
                if (ShouldStopNormalSettlement(entry))
                    break;
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

                    if (ShouldStopNormalSettlement(entry))
                        break;

                    if (!completed)
                        continue;

                    if (ShouldStopNormalSettlement(entry))
                        break;
                    entry.CompletionProven = true;
                    if (entry.SubmissionValidationTracked && !entry.SubmissionValidationCompletionCounted)
                    {
                        entry.SubmissionValidationCompletionCounted = true;
                        _submissionValidationRealCompletionCount++;
                    }
                    _commandRuntime.CompleteTrackedTimeline(entry.TimelineSemaphore, entry.TimelineValue);
                    SynchronizeSubmissionValidation(entry);
                }

                entry.Retiring = true;
                readyToRetire[readyCount++] = i;
            }
        }

        int retiredCount = 0;
        for (int i = 0; i < readyCount; i++)
        {
            InFlightSubmission completed = _inFlight[readyToRetire[i]];
            if (ShouldStopNormalSettlement(completed))
                break;
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
                lock (_gate)
                {
                    if (completed.SubmissionValidationTracked && !completed.SubmissionValidationRetiredCounted)
                    {
                        completed.SubmissionValidationRetiredCounted = true;
                        _submissionValidationRetiredCount++;
                    }
                }
                Volatile.Write(ref _lastCompletedFrameId, completed.FrameId);
                Interlocked.Increment(ref _completedSubmissionCount);
                retiredCount++;
            }
            finally
            {
                lock (_gate)
                {
                    if (!completed.AbandonedAfterDeviceLoss)
                    {
                        completed.Active = !retired;
                        completed.Retiring = false;
                    }
                }
            }
        }

        return retiredCount;
    }

    private bool RetireCompletedSubmission(InFlightSubmission entry)
    {
        if (entry.Reopened)
            return true;
        if (ShouldStopNormalSettlement(entry))
            return false;

        if (!SettleUploads(entry, publish: true) ||
            ShouldStopNormalSettlement(entry) ||
            !ReleaseSubmissionFrameSlotLifetimes(entry) ||
            ShouldStopNormalSettlement(entry) ||
            !InvokeRetiredCallbackOnce(entry) ||
            ShouldStopNormalSettlement(entry) ||
            !ReleaseRecordedCommandBuffers(entry) ||
            ShouldStopNormalSettlement(entry) ||
            !ReleaseTemporaryCommandBuffer(entry) ||
            ShouldStopNormalSettlement(entry) ||
            !ReleasePreparedInputs(entry) ||
            ShouldStopNormalSettlement(entry) ||
            !ReopenArenas(entry) ||
            ShouldStopNormalSettlement(entry))
            return false;

        entry.Reopened = true;
        SynchronizeSubmissionValidation(entry, retired: true);

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

    private bool ReleaseSubmissionFrameSlotLifetimes(InFlightSubmission entry)
    {
        Action<InFlightSubmission, uint>? callback = _onSubmissionFrameSlotLifetimeSettled;
        if (callback is null)
            return true;
        try
        {
            while (entry.ResidentLifetimeReleaseIndex < entry.FrameSlotCount)
            {
                if (ShouldStopNormalSettlement(entry))
                    return false;
                int index = entry.ResidentLifetimeReleaseIndex;
                uint frameSlot = entry.FrameSlots[index];
                bool duplicate = false;
                for (int prior = 0; prior < index; prior++)
                    if (entry.FrameSlots[prior] == frameSlot)
                    {
                        duplicate = true;
                        break;
                    }
                if (!duplicate)
                    callback(entry, frameSlot);
                entry.ResidentLifetimeReleaseIndex++;
                if (ShouldStopNormalSettlement(entry))
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning("[OpenXR.Tracker] Resident frame-slot lifetime release failed for frame {0}: {1}", entry.FrameId, ex.Message);
            return false;
        }
    }

    private bool ShouldStopNormalSettlement(InFlightSubmission? entry)
        => _deviceLossAbandonRequested ||
           !_commandRuntime.DeviceContext.IsOperational ||
           Volatile.Read(ref _disposed) != 0 ||
           entry?.AbandonedAfterDeviceLoss == true;

    private void EndSettlementNoLock()
    {
        if (--_settlementDepth != 0 || !_deviceLossAbandonRequested || Volatile.Read(ref _disposed) != 0)
            return;
        _deviceLossAbandonRequested = false;
        _settlementDepth = 1;
        try { AbandonAfterDeviceLossCore(); }
        finally { _settlementDepth = 0; }
    }

    private bool SettleCancelledSubmission(InFlightSubmission entry)
    {
        if (ShouldStopNormalSettlement(entry) || entry.NativeSubmissionAccepted)
            return false;

        bool settled = SettleUploads(entry, publish: false);
        if (settled && !ShouldStopNormalSettlement(entry))
            settled = ReleaseSubmissionFrameSlotLifetimes(entry);
        if (settled && !ShouldStopNormalSettlement(entry))
            settled = ReleaseRecordedCommandBuffers(entry);
        if (settled && !ShouldStopNormalSettlement(entry))
            settled = ReleaseTemporaryCommandBuffer(entry);
        if (settled && !ShouldStopNormalSettlement(entry))
            settled = ReleasePreparedInputs(entry);
        if (settled && !ShouldStopNormalSettlement(entry))
            settled = ReopenArenas(entry);
        settled &= !ShouldStopNormalSettlement(entry);
        lock (_gate)
        {
            if (!entry.AbandonedAfterDeviceLoss)
            {
                entry.Active = !settled;
                entry.Retiring = false;
                if (settled && entry.SubmissionValidationTracked && !entry.SubmissionValidationRetiredCounted)
                {
                    entry.SubmissionValidationRetiredCounted = true;
                    _submissionValidationRetiredCount++;
                }
            }
        }
        if (!entry.AbandonedAfterDeviceLoss)
            SynchronizeSubmissionValidation(entry, retired: settled);
        return settled;
    }

    private bool SettleUploads(InFlightSubmission entry, bool publish)
    {
        if (entry.UploadCount != 0)
            RecordEarlySettlementViolationIfNeeded(entry);
        while (entry.UploadSettlementIndex < entry.UploadCount)
        {
            if (ShouldStopNormalSettlement(entry))
                return false;
            try
            {
                ReadOnlySpan<VulkanImportedTexturePendingUpload> upload =
                    entry.Uploads.AsSpan(entry.UploadSettlementIndex, 1);
                if (publish)
                    _commandRuntime.PublishOpenXrRecordedTextureUploads(upload, "OpenXR eye async completion");
                else
                    _commandRuntime.CancelOpenXrRecordedTextureUploads(upload, "OpenXR prepared submission rejected");
                entry.Uploads[entry.UploadSettlementIndex++] = null!;
                if (ShouldStopNormalSettlement(entry))
                    return false;
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
        if (entry.HasFirst || entry.HasSecond)
            RecordEarlySettlementViolationIfNeeded(entry);
        try
        {
            if (entry.HasFirst)
            {
                if (_freeCommandBuffer is not null) _freeCommandBuffer(entry.FirstRecorded);
                else FreeRecordedCommandBufferDirect(entry.FirstRecorded);
                entry.FirstRecorded = default;
                entry.HasFirst = false;
                if (ShouldStopNormalSettlement(entry))
                    return false;
            }
            if (entry.HasSecond)
            {
                if (_freeCommandBuffer is not null) _freeCommandBuffer(entry.SecondRecorded);
                else FreeRecordedCommandBufferDirect(entry.SecondRecorded);
                entry.SecondRecorded = default;
                entry.HasSecond = false;
                if (ShouldStopNormalSettlement(entry))
                    return false;
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
        RecordEarlySettlementViolationIfNeeded(entry);
        try
        {
            _commandRuntime.ReleaseOpenXrTemporaryCommandBuffer(entry.TemporaryCommandBuffer, EVulkanQueueSubmissionDisposition.Completed);
            entry.TemporaryCommandBuffer = default;
            entry.HasTemporaryCommandBuffer = false;
            if (ShouldStopNormalSettlement(entry))
                return false;
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning("[OpenXR.Tracker] Temporary command-buffer release failed for frame {0}: {1}", entry.FrameId, ex.Message);
            return false;
        }
    }

    private bool ReleasePreparedInputs(InFlightSubmission entry)
    {
        if ((!entry.FirstPreparedReleased && entry.HasFirstPrepared) ||
            (!entry.SecondPreparedReleased && entry.HasSecondPrepared))
            RecordEarlySettlementViolationIfNeeded(entry);
        try
        {
            if (!entry.FirstPreparedReleased && entry.HasFirstPrepared)
            {
                if (entry.FirstPrepared.Ops is { } firstOps)
                    VulkanAdvancedVisibilityInputLease.ReleaseOperations(firstOps);
                entry.FirstPrepared = default;
                entry.HasFirstPrepared = false;
                entry.FirstPreparedReleased = true;
                if (ShouldStopNormalSettlement(entry))
                    return false;
            }
            if (!entry.SecondPreparedReleased && entry.HasSecondPrepared)
            {
                if (entry.SecondPrepared.Ops is { } secondOps)
                    VulkanAdvancedVisibilityInputLease.ReleaseOperations(secondOps);
                entry.SecondPrepared = default;
                entry.HasSecondPrepared = false;
                entry.SecondPreparedReleased = true;
                if (ShouldStopNormalSettlement(entry))
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static void ReleasePreparedInputsAfterDeviceLoss(InFlightSubmission entry)
    {
        if (!entry.FirstPreparedReleased && entry.HasFirstPrepared)
        {
            try
            {
                if (entry.FirstPrepared.Ops is { } firstOps)
                    VulkanAdvancedVisibilityInputLease.ReleaseOperations(firstOps);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning("[OpenXR.Tracker] Device-loss first prepared-input lease release failed for frame {0}: {1}", entry.FrameId, ex.Message);
            }
            finally
            {
                entry.FirstPrepared = default;
                entry.HasFirstPrepared = false;
                entry.FirstPreparedReleased = true;
            }
        }
        if (!entry.SecondPreparedReleased && entry.HasSecondPrepared)
        {
            try
            {
                if (entry.SecondPrepared.Ops is { } secondOps)
                    VulkanAdvancedVisibilityInputLease.ReleaseOperations(secondOps);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning("[OpenXR.Tracker] Device-loss second prepared-input lease release failed for frame {0}: {1}", entry.FrameId, ex.Message);
            }
            finally
            {
                entry.SecondPrepared = default;
                entry.HasSecondPrepared = false;
                entry.SecondPreparedReleased = true;
            }
        }
    }

    private static void AbandonUploadsAfterDeviceLoss(InFlightSubmission entry)
    {
        for (int i = entry.UploadSettlementIndex; i < entry.UploadCount; i++)
        {
            VulkanImportedTexturePendingUpload? upload = entry.Uploads[i];
            if (upload is null)
                continue;
            try
            {
                if (upload.OwnerJob is { } ownerJob)
                    ownerJob.InvokeCanceledOnce();
                else
                    upload.OnCanceled?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning("[OpenXR.Tracker] Device-loss upload cancellation callback failed for frame {0}: {1}", entry.FrameId, ex.Message);
            }
            entry.Uploads[i] = null!;
        }
        entry.UploadSettlementIndex = entry.UploadCount;
        entry.UploadCount = 0;
    }

    private bool ReopenArenas(InFlightSubmission entry)
    {
        if (entry.FrameSlotCount != 0 && (entry.MappedFrameArena is not null || entry.FrameDataArena is not null))
            RecordEarlySettlementViolationIfNeeded(entry);
        if (entry.MappedFrameArena is not null)
            while (entry.MappedFrameSlotResetCount < entry.FrameSlotCount)
            {
                if (ShouldStopNormalSettlement(entry))
                    return false;
                int index = entry.MappedFrameSlotResetCount;
                if (!entry.MappedFrameArena.TryResetFrameSlot(entry.FrameSlots[index], entry.MappedFrameGeneration, submissionCompletionProven: true))
                    return false;
                entry.MappedFrameSlotResetCount++;
                if (ShouldStopNormalSettlement(entry))
                    return false;
            }
        if (entry.FrameDataArena is not null)
            while (entry.FrameDataSlotResetCount < entry.FrameSlotCount)
            {
                if (ShouldStopNormalSettlement(entry))
                    return false;
                int index = entry.FrameDataSlotResetCount;
                if (!entry.FrameDataArena.TryResetFrameSlot(entry.FrameSlots[index], entry.FrameDataGeneration, submissionCompletionProven: true))
                    return false;
                entry.FrameDataSlotResetCount++;
                if (ShouldStopNormalSettlement(entry))
                    return false;
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

    /// <summary>
    /// Records device-loss abandonment without claiming native completion. Only
    /// CPU-side prepared-input operation leases and upload owner cancellation
    /// callbacks may run; native upload resources, command buffers, arena slots,
    /// and retirement callbacks remain untouched.
    /// </summary>
    public int AbandonAfterDeviceLoss()
    {
        if (_commandRuntime.DeviceContext.IsOperational)
            throw new InvalidOperationException("OpenXR submission abandonment requires device loss.");

        if (Monitor.IsEntered(_settlementGate))
        {
            _deviceLossAbandonRequested = true;
            return 0;
        }

        lock (_settlementGate)
        {
            _settlementDepth++;
            try { return AbandonAfterDeviceLossCore(); }
            finally { EndSettlementNoLock(); }
        }
    }

    private int AbandonAfterDeviceLossCore()
    {
        int abandonedCount = 0;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return 0;

            for (int i = 0; i < _inFlight.Length; i++)
            {
                InFlightSubmission entry = _inFlight[i];
                if ((!entry.Active && !entry.Retiring && !entry.PendingCommit) || entry.AbandonedAfterDeviceLoss)
                    continue;

                entry.AbandonedAfterDeviceLoss = true;
                if (entry.SubmissionValidationTracked && !entry.SubmissionValidationAbandonedCounted)
                {
                    entry.SubmissionValidationAbandonedCounted = true;
                    _submissionValidationAbandonedCount++;
                }
                entry.Retiring = true;
                entry.PendingCommit = false;
                entry.Cancelled = false;
                if ((uint)entry.ValidationLedgerIndex < (uint)_submissionValidationLedgerCount)
                    _submissionValidationLedger[entry.ValidationLedgerIndex].AbandonedAfterDeviceLoss = true;
                abandonedCount++;
            }

            _reservedSubmissionCount = 0;
            _latestAcceptedCompletionSemaphore = default;
            _latestAcceptedCompletionValue = 0;
            for (int i = 0; i < _admissionSlots.Length; i++)
            {
                _admissionSlots[i].Active = false;
                _admissionSlots[i].PreparedSlotIndex = -1;
            }
        }

        for (int i = 0; i < _inFlight.Length; i++)
        {
            InFlightSubmission entry = _inFlight[i];
            if (!entry.AbandonedAfterDeviceLoss)
                continue;
            ReleasePreparedInputsAfterDeviceLoss(entry);
            AbandonUploadsAfterDeviceLoss(entry);
            lock (_gate)
            {
                entry.Active = false;
                entry.Retiring = false;
                entry.FirstRecorded = default;
                entry.SecondRecorded = default;
                entry.HasFirst = false;
                entry.HasSecond = false;
                entry.TemporaryCommandBuffer = default;
                entry.HasTemporaryCommandBuffer = false;
                entry.MappedFrameArena = null;
                entry.FrameDataArena = null;
                entry.FrameSlotCount = 0;
            }
        }

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            VulkanOpenXrSubmissionValidationState.Detach(this);
        return abandonedCount;
    }

    /// <summary>
    /// Detaches this tracker only after all its submission-owned native resources
    /// have been settled. A failed drain deliberately retains registration and
    /// evidence for the caller's device-loss or teardown policy.
    /// </summary>
    public bool TryDisposeAfterDrain(uint timeoutMs = DefaultShutdownDrainTimeoutMs)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return true;
        if (!DrainAll(timeoutMs))
            return false;
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return true;
        VulkanOpenXrSubmissionValidationState.Detach(this);
        return true;
    }

    public void Dispose() => _ = TryDisposeAfterDrain(1000u);
}
