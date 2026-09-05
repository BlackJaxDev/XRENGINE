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
                return false;
            case EGpuFenceSubmissionStatus.Failed:
                // A marker can be failed before queue submission when a healthy
                // frame attempt is rejected. Its output is invalid, but a partial
                // native dispatch may still be pinned by consumers, so reuse is
                // authorized only by the exact native-buffer query below.
                if (AbstractRenderer.Current is IRuntimeRendererHost { IsDeviceLost: true })
                {
                    LastOutputReuseStatus = EGpuBufferContentReuseStatus.DeviceLost;
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
                    return false;
                case EGpuFenceStatus.Failed:
                    // Poll failures do not prove that no commands reached the
                    // queue, so retain the fence and fail closed.
                    LastOutputReuseStatus = ResolveFenceFailureStatus();
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
                return false;
            }

            LastOutputReuseStatus = capability.QueryBufferContentReuse(
                _outputBuffers.Buffers[slot]);
            if (LastOutputReuseStatus !=
                EGpuBufferContentReuseStatus.Ready)
            {
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
