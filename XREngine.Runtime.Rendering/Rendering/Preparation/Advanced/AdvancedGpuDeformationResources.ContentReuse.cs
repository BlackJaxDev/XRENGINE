namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    /// <summary>
    /// Last exact backend state observed while attempting to reuse an output
    /// slot. This is diagnostic only and never grants reuse by itself.
    /// </summary>
    public EGpuBufferContentReuseStatus LastOutputReuseStatus
        { get; private set; } = EGpuBufferContentReuseStatus.Ready;

    public uint LastOutputReuseSlot { get; private set; }

    /// <summary>Identifies the completion authority responsible for the last reuse decision.</summary>
    public string LastOutputReuseAuthority { get; private set; } = "None";

    /// <summary>
    /// Diagnostics only: the backend's description of the last native reuse refusal (the
    /// completion frontier that held the output buffer), written only while the deformation slot
    /// diagnostics are on.
    /// </summary>
    public string? LastNativeReuseDetail { get; private set; }

    /// <summary>The check that refused the last output slot acquisition, or None.</summary>
    public EAdvancedDeformationSlotDeferral LastOutputReuseDeferral { get; private set; }

    /// <summary>
    /// The check that made the last <c>TryBeginFrame</c> call fail, or None when the frame opened.
    /// </summary>
    public EAdvancedDeformationSlotDeferral LastBeginFrameDeferral { get; private set; }

    private readonly long[] _beginFrameDeferralCounts = new long[(int)EAdvancedDeformationSlotDeferral.Count];

    /// <summary>Process total of failed <c>TryBeginFrame</c> calls whose reason was <paramref name="reason"/>.</summary>
    public long GetBeginFrameDeferralCount(EAdvancedDeformationSlotDeferral reason)
        => (uint)reason < (uint)_beginFrameDeferralCounts.Length ? Volatile.Read(ref _beginFrameDeferralCounts[(int)reason]) : 0L;

    /// <summary>
    /// Diagnostics only: the frame id of the preparation that produced <paramref name="slot"/>, and
    /// the producer fence's timeline target and the backend's completed value (0 and 0 when the
    /// slot has no fence or the backend has no timeline). Reads the backend counter.
    /// </summary>
    public bool TryDescribeOutputSlot(uint slot, out ulong producerFrameId, out ulong fenceTarget, out ulong fenceCompleted)
    {
        producerFrameId = 0UL;
        fenceTarget = 0UL;
        fenceCompleted = 0UL;
        if (slot >= (uint)_frameSlotCount)
            return false;
        producerFrameId = _slotProducerFrameIds[slot];
        XRGpuFence? fence = _slotProducerFences[slot];
        return fence is null || fence.IsDisposed ||
            fence.TryGetTimelineDiagnostic(out fenceTarget, out fenceCompleted);
    }

    private readonly long[] _slotPoisonCounts = new long[(int)EAdvancedDeformationSlotPoisonCause.Count];

    // Frame id of the preparation that poisoned each slot. A producer fence of a later frame that
    // has signaled proves that the poisoned slot's work is done.
    private readonly ulong[] _slotPoisonFrameIds = new ulong[MaximumSelectableOutputSlots];
    private long _slotPoisonRecoveries;

    /// <summary>Process total of poisoned slots that a later completed producer fence released.</summary>
    public long SlotPoisonRecoveryCount => Volatile.Read(ref _slotPoisonRecoveries);

    /// <summary>
    /// Releases the poison of <paramref name="slot"/> when another slot's producer fence from a
    /// later frame was submitted and has signaled. A signal operation covers every command that was
    /// submitted to its queue before it, so the poisoned slot's work, which was recorded in an
    /// earlier frame, is complete. The slot's own fence (if any) is dropped; the caller then still
    /// runs the backend's buffer reuse query before the slot is written again.
    /// </summary>
    private bool TryRecoverPoisonedSlot(uint slot)
    {
        ulong poisonFrame = _slotPoisonFrameIds[slot];
        for (int other = 0; other < _frameSlotCount; other++)
        {
            if (other == slot || _slotReusePoisoned[other] ||
                _slotProducerFrameIds[other] <= poisonFrame)
                continue;
            XRGpuFence? laterFence = _slotProducerFences[other];
            if (laterFence is null || laterFence.IsDisposed ||
                laterFence.SubmissionStatus != EGpuFenceSubmissionStatus.Submitted ||
                laterFence.Poll() != EGpuFenceStatus.Signaled)
                continue;

            _slotProducerFences[slot]?.Dispose();
            _slotProducerFences[slot] = null;
            _slotOutputValid[slot] = false;
            _slotReusePoisoned[slot] = false;
            Interlocked.Increment(ref _slotPoisonRecoveries);
            return true;
        }
        return false;
    }

    /// <summary>The output slot of the last frame that opened, or -1 before the first frame.</summary>
    private int _lastOpenedOutputSlot = -1;

    /// <summary>Upper bound of output slots that <see cref="TrySelectOutputSlot"/> orders on the stack.</summary>
    private const int MaximumSelectableOutputSlots = 16;

    /// <summary>
    /// Chooses the output slot that the next deformation frame writes, by availability instead of
    /// by frame id. The previous slot is the slot of the last frame that opened, because that frame
    /// wrote the history that this frame reads, so it is never a candidate. The other slots are
    /// tried from the oldest producer frame to the newest (unused slots first), and the first slot
    /// whose producer fence is signaled and whose output buffer no command buffer still uses (the
    /// same checks as <c>TryAcquireOutputSlot</c>) is taken. When every candidate is busy, the method
    /// returns false and <see cref="LastOutputReuseDeferral"/> describes the oldest candidate.
    /// </summary>
    public bool TrySelectOutputSlot(out uint currentFrameSlot, out uint previousFrameSlot)
    {
        int slotCount = _frameSlotCount;
        if (slotCount < 2 || slotCount > MaximumSelectableOutputSlots)
            throw new InvalidOperationException(
                $"Deformation output slot selection supports 2 to {MaximumSelectableOutputSlots} slots, not {slotCount}.");

        int previous = _lastOpenedOutputSlot;
        Span<int> candidates = stackalloc int[MaximumSelectableOutputSlots];
        int candidateCount = 0;
        for (int slot = 0; slot < slotCount; slot++)
        {
            if (slot == previous)
                continue;
            // Insertion by producer frame keeps the order stable for equal frames (unused slots
            // have frame 0 and keep their index order).
            int insert = candidateCount;
            while (insert > 0 && _slotProducerFrameIds[candidates[insert - 1]] > _slotProducerFrameIds[slot])
            {
                candidates[insert] = candidates[insert - 1];
                insert--;
            }
            candidates[insert] = slot;
            candidateCount++;
        }

        EAdvancedDeformationSlotDeferral firstDeferral = EAdvancedDeformationSlotDeferral.None;
        EGpuBufferContentReuseStatus firstStatus = EGpuBufferContentReuseStatus.Ready;
        string firstAuthority = "None";
        uint firstSlot = 0u;
        for (int index = 0; index < candidateCount; index++)
        {
            uint slot = (uint)candidates[index];
            if (TryAcquireOutputSlot(slot))
            {
                currentFrameSlot = slot;
                previousFrameSlot = previous >= 0
                    ? (uint)previous
                    : (slot + (uint)slotCount - 1u) % (uint)slotCount;
                return true;
            }
            if (index == 0)
            {
                firstDeferral = LastOutputReuseDeferral;
                firstStatus = LastOutputReuseStatus;
                firstAuthority = LastOutputReuseAuthority;
                firstSlot = slot;
            }
        }

        // Report the oldest candidate: it is the slot that frees first.
        LastOutputReuseDeferral = firstDeferral;
        LastOutputReuseStatus = firstStatus;
        LastOutputReuseAuthority = firstAuthority;
        LastOutputReuseSlot = firstSlot;
        currentFrameSlot = firstSlot;
        previousFrameSlot = previous >= 0 ? (uint)previous : 0u;
        return RecordBeginFrameDeferral(firstDeferral);
    }

    /// <summary>Process total of slots poisoned for <paramref name="cause"/>.</summary>
    public long GetSlotPoisonCount(EAdvancedDeformationSlotPoisonCause cause)
        => (uint)cause < (uint)_slotPoisonCounts.Length ? Volatile.Read(ref _slotPoisonCounts[(int)cause]) : 0L;

    /// <summary>Bit <c>n</c> is set when output slot <c>n</c> is poisoned (never reusable again).</summary>
    public uint PoisonedSlotMask
    {
        get
        {
            uint mask = 0u;
            for (int slot = 0; slot < _slotReusePoisoned.Length && slot < 32; slot++)
                if (_slotReusePoisoned[slot])
                    mask |= 1u << slot;
            return mask;
        }
    }

    /// <summary>Number of deformation output slots.</summary>
    public int OutputSlotCount => _frameSlotCount;

    /// <summary>
    /// Marks the current output slot as never reusable, counts the cause, and writes one
    /// diagnostic line when <see cref="XREngineEnvironmentVariables.AdvancedDeformationSlotDiagnostics"/>
    /// is on. A poisoned slot defers every later preparation that lands on it.
    /// </summary>
    private void PoisonCurrentSlot(EAdvancedDeformationSlotPoisonCause cause, Exception? exception)
    {
        _slotReusePoisoned[_currentFrameSlot] = true;
        _slotPoisonFrameIds[_currentFrameSlot] = _frameId;
        if ((uint)cause < (uint)_slotPoisonCounts.Length)
            Interlocked.Increment(ref _slotPoisonCounts[(int)cause]);
        if (!AdvancedPreparationExtractor.DeformationSlotDiagnosticsEnabled)
            return;
        Debug.WriteAuxiliaryLog(
            AdvancedPreparationExtractor.DeformationSlotDiagnosticsLogFileName,
            FormattableString.Invariant(
                $"POISON frame={_frameId} renderFrame={RuntimeEngine.Rendering.State.RenderFrameId} slot={_currentFrameSlot} cause={cause} poisonedMask=0x{PoisonedSlotMask:X} slots={_frameSlotCount} exception={(exception is null ? "none" : exception.GetType().Name + ": " + exception.Message)}"));
    }

    private bool RecordBeginFrameDeferral(EAdvancedDeformationSlotDeferral reason)
    {
        LastBeginFrameDeferral = reason;
        if ((uint)reason < (uint)_beginFrameDeferralCounts.Length)
            Interlocked.Increment(ref _beginFrameDeferralCounts[(int)reason]);
        return false;
    }

    /// <summary>
    /// Accepts ordered submitted output as history without waiting for its GPU
    /// completion. Failed, unsubmitted, and untracked output cannot be history.
    /// </summary>
    private bool HasUsableOutputHistory(uint slot)
    {
        if (!_slotOutputValid[slot])
            return false;

        XRGpuFence? fence = _slotProducerFences[slot];
        if (_slotReusePoisoned[slot] || fence is null || fence.IsDisposed ||
            AbstractRenderer.Current is IRuntimeRendererHost { IsDeviceLost: true })
        {
            _slotOutputValid[slot] = false;
            return false;
        }

        switch (fence.SubmissionStatus)
        {
            case EGpuFenceSubmissionStatus.AwaitingSubmission:
                return false;
            case EGpuFenceSubmissionStatus.Failed:
                _slotOutputValid[slot] = false;
                return false;
            case EGpuFenceSubmissionStatus.Submitted:
                if (fence.Poll() != EGpuFenceStatus.Failed)
                    return true;
                _slotOutputValid[slot] = false;
                return false;
            default:
                _slotOutputValid[slot] = false;
                return false;
        }
    }

    private bool TryAcquireAllOutputSlots()
    {
        for (uint slot = 0u; slot < (uint)_frameSlotCount; slot++)
            if (!TryAcquireOutputSlot(slot))
                return false;

        return true;
    }

    private bool TryAcquireOutputSlot(uint slot)
    {
        LastOutputReuseSlot = slot;
        LastOutputReuseAuthority = "ProducerFence";
        LastOutputReuseDeferral = EAdvancedDeformationSlotDeferral.None;
        if (_slotReusePoisoned[slot] && !TryRecoverPoisonedSlot(slot))
        {
            LastOutputReuseAuthority = "MissingProducerFence";
            LastOutputReuseStatus = EGpuBufferContentReuseStatus.Superseded;
            LastOutputReuseDeferral = EAdvancedDeformationSlotDeferral.SlotPoisoned;
            return false;
        }
        XRGpuFence? fence = _slotProducerFences[slot];
        if (fence is null)
        {
            LastOutputReuseAuthority = "UnsubmittedSlot";
            LastOutputReuseStatus =
                EGpuBufferContentReuseStatus.Ready;
            return true;
        }

        bool skipPollForRejectedSubmission = false;
        switch (fence.SubmissionStatus)
        {
            case EGpuFenceSubmissionStatus.AwaitingSubmission:
                LastOutputReuseStatus =
                    EGpuBufferContentReuseStatus.AwaitingSubmission;
                LastOutputReuseDeferral = EAdvancedDeformationSlotDeferral.AwaitingSubmission;
                return false;
            case EGpuFenceSubmissionStatus.Failed:
                // A marker can be failed before queue submission when a healthy
                // frame attempt is rejected. Its output is invalid, but a partial
                // native dispatch may still be pinned by consumers, so reuse is
                // authorized only by the exact native-buffer query below.
                if (AbstractRenderer.Current is IRuntimeRendererHost { IsDeviceLost: true })
                {
                    LastOutputReuseStatus = EGpuBufferContentReuseStatus.DeviceLost;
                    LastOutputReuseDeferral = EAdvancedDeformationSlotDeferral.DeviceLost;
                    return false;
                }

                _slotOutputValid[slot] = false;
                // Keep the failed marker as the slot's ownership state until
                // the native consumer query proves that partial GPU work is no
                // longer pinned. Clearing it here would make a later pending
                // query look like an unsubmitted, reusable slot.
                skipPollForRejectedSubmission = true;
                break;
        }

        if (!skipPollForRejectedSubmission)
            switch (fence.Poll())
            {
                case EGpuFenceStatus.Pending:
                    LastOutputReuseStatus =
                        EGpuBufferContentReuseStatus.PendingCompletion;
                    LastOutputReuseDeferral = EAdvancedDeformationSlotDeferral.PendingCompletion;
                    return false;
                case EGpuFenceStatus.Failed:
                    // Poll failures do not prove that no commands reached the
                    // queue, so retain the fence and fail closed.
                    _slotOutputValid[slot] = false;
                    LastOutputReuseStatus = ResolveFenceFailureStatus();
                    LastOutputReuseDeferral = LastOutputReuseStatus == EGpuBufferContentReuseStatus.DeviceLost
                        ? EAdvancedDeformationSlotDeferral.DeviceLost
                        : EAdvancedDeformationSlotDeferral.FencePollFailed;
                    return false;
            }

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (_backend == RuntimeGraphicsApiKind.Vulkan)
        {
            LastOutputReuseAuthority = "NativeConsumers";
            if (renderer is not IRuntimeRendererHost host ||
                !host.TryGetBackendCapability<
                    IGpuBufferContentReuseCapability>(
                        out IGpuBufferContentReuseCapability? capability) ||
                capability is null)
            {
                LastOutputReuseStatus =
                    EGpuBufferContentReuseStatus.Unsupported;
                LastOutputReuseDeferral = EAdvancedDeformationSlotDeferral.NativeQueryUnsupported;
                return false;
            }

            LastOutputReuseStatus = capability.QueryBufferContentReuse(
                _outputBuffers.Buffers[slot]);
            if (LastOutputReuseStatus !=
                EGpuBufferContentReuseStatus.Ready)
            {
                LastOutputReuseDeferral = EAdvancedDeformationSlotDeferral.NativeConsumers;
                LastNativeReuseDetail = AdvancedPreparationExtractor.DeformationSlotDiagnosticsEnabled
                    ? capability.DescribeBufferContentReuse(_outputBuffers.Buffers[slot])
                    : null;
                return false;
            }
        }
        else
        {
            LastOutputReuseStatus =
                EGpuBufferContentReuseStatus.Ready;
        }

        fence?.Dispose();
        _slotProducerFences[slot] = null;
        return true;
    }

    private static EGpuBufferContentReuseStatus ResolveFenceFailureStatus()
        => AbstractRenderer.Current is IRuntimeRendererHost
            { IsDeviceLost: true }
            ? EGpuBufferContentReuseStatus.DeviceLost
            : EGpuBufferContentReuseStatus.Superseded;
}
