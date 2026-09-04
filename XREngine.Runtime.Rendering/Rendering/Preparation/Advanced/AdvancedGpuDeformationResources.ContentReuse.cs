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
        XRGpuFence? fence = _slotProducerFences[slot];
        if (fence is null)
        {
            LastOutputReuseStatus =
                EGpuBufferContentReuseStatus.Ready;
            return true;
        }

        switch (fence.SubmissionStatus)
        {
            case EGpuFenceSubmissionStatus.AwaitingSubmission:
                LastOutputReuseStatus =
                    EGpuBufferContentReuseStatus.AwaitingSubmission;
                return false;
            case EGpuFenceSubmissionStatus.Failed:
                LastOutputReuseStatus = ResolveFenceFailureStatus();
                return false;
        }

        switch (fence.Poll())
        {
            case EGpuFenceStatus.Pending:
                LastOutputReuseStatus =
                    EGpuBufferContentReuseStatus.PendingCompletion;
                return false;
            case EGpuFenceStatus.Failed:
                LastOutputReuseStatus = ResolveFenceFailureStatus();
                return false;
        }

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (_backend == RuntimeGraphicsApiKind.Vulkan)
        {
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

        fence.Dispose();
        _slotProducerFences[slot] = null;
        return true;
    }

    private static EGpuBufferContentReuseStatus ResolveFenceFailureStatus()
        => AbstractRenderer.Current is IRuntimeRendererHost
            { IsDeviceLost: true }
            ? EGpuBufferContentReuseStatus.DeviceLost
            : EGpuBufferContentReuseStatus.Superseded;
}