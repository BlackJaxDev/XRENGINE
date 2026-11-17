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
        if (_slotReusePoisoned[slot])
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
