namespace XREngine.Rendering.Compute;

/// <summary>
/// Persistent GPU resources paired one-to-one with a readback-service staging slot.
/// </summary>
internal sealed class PhysicsChainSelectiveReadbackSlotResources : RenderResourceLeaseOwner, IDisposable
{
    private volatile bool _disposeRequested;
    public XRDataBuffer<PhysicsChainGpuReadbackGatherItem>? Items;
    public XRDataBuffer<uint>? PackedOutput;
    public XRDataBuffer<uint>? MappedStaging;
    public PhysicsChainMappedReadbackStagingSource StagingSource { get; } = new();
    public PhysicsChainGpuReadbackFence Fence { get; } = new();
    internal AbstractRenderer? ProducerRenderer;
    internal IPhysicsChainComputeBackend? ProducerBackend;
    internal XRGpuFence? CompletionFence;
    internal bool HasQueuedWork;
    internal bool Quarantined;

    internal bool CanRetire()
    {
        if (AuthoringUseCount != 0 || StagingSource.IsValid)
            return false;
        if (!HasQueuedWork)
            return true;
        if (Quarantined)
            return HasCompletedNativeBufferUses();
        return ProducerBackend is { } backend && TryBeginReuse(backend);
    }

    /// <summary>Releases a quarantined slot only when native buffer use has ended.</summary>
    private bool HasCompletedNativeBufferUses()
    {
        if (ProducerRenderer is not IRuntimeRendererHost host
            || host.BackendId != RendererBackendId.Vulkan
            || host.IsDeviceLost || !ProducerRenderer.AcceptsBackendWork
            || !host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var reuse)
            || reuse is null)
            return false;
        return IsReady(reuse, Items) && IsReady(reuse, PackedOutput) && IsReady(reuse, MappedStaging);
    }

    /// <summary>Reuses storage only after native work has finished or has no remaining buffer use.</summary>
    internal bool TryBeginReuse(IPhysicsChainComputeBackend backend, bool callerHasActiveUse = false)
    {
        if (Quarantined || _disposeRequested || StagingSource.IsValid
            || AuthoringUseCount != (callerHasActiveUse ? 1 : 0)
            || ProducerRenderer is not null && !ReferenceEquals(ProducerRenderer, backend.Renderer))
            return false;
        if (!HasQueuedWork)
            return true;

        bool completed = false;
        if (CompletionFence is not { IsDisposed: false } marker)
        {
            Quarantined = true;
            return false;
        }
        if (marker.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
            return false;
        if (marker.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted)
        {
            EGpuFenceStatus status = marker.Poll();
            if (status == EGpuFenceStatus.Pending)
                return false;
            completed = status == EGpuFenceStatus.Signaled;
        }

        if (backend.Renderer is IRuntimeRendererHost host && host.BackendId == RendererBackendId.Vulkan)
        {
            if (host.IsDeviceLost || !backend.Renderer.AcceptsBackendWork
                || !host.TryGetBackendCapability<IGpuBufferContentReuseCapability>(out var reuse) || reuse is null)
            {
                Quarantined = true;
                return false;
            }
            if (!IsReady(reuse, Items) || !IsReady(reuse, PackedOutput) || !IsReady(reuse, MappedStaging))
                return false;
        }
        else if (!completed)
        {
            Quarantined = true;
            return false;
        }

        CompletionFence?.Dispose();
        CompletionFence = null;
        StagingSource.Dispose();
        Fence.Dispose();
        HasQueuedWork = false;
        return true;
    }

    private static bool IsReady(IGpuBufferContentReuseCapability reuse, XRDataBuffer? buffer)
    {
        if (buffer is null)
            return true;
        XRBufferStateSnapshot state = buffer.GetStateSnapshot();
        return !state.IsApiObjectGenerated && state.UploadedByteCount == 0u
            || reuse.QueryBufferContentReuse(buffer) == EGpuBufferContentReuseStatus.Ready;
    }

    public void Dispose()
    {
        _disposeRequested = true;
        RetireAuthoringResources();
    }

    protected override void DisposeRetainedResources()
    {
        StagingSource.Dispose();
        Fence.Reject();
        Fence.Dispose();
        CompletionFence?.Dispose();
        CompletionFence = null;
        Items?.Dispose();
        PackedOutput?.Dispose();
        MappedStaging?.Dispose();
        Items = null;
        PackedOutput = null;
        MappedStaging = null;
    }
}
