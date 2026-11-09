using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    private uint[]? _lodTransitionActiveResetIndices;
    private uint _lodTransitionActiveCapacity;
    private XRDataBuffer? _lodTransitionCandidateBuffer;
    private XRDataBuffer? _lodTransitionZeroBuffer;
    private XRGpuFence? _lodTransitionPublicationFence;
    private bool _lodTransitionPublicationAccepted;
    private bool _lodTransitionPublicationUnsafe;
    private AbstractRenderer? _lodTransitionPublicationRenderer;
    private Func<bool>? _lodTransitionPublicationProducer;
    private string? _lodTransitionPublicationFailureReason;

    /// <summary>Publishes structural LOD resets with the command snapshot.</summary>
    private void PublishLodTransitionChangesAtSwap()
    {
        _lodTransitionPublishedCapacity = Math.Max(
            _lodTransitionPublishedCapacity,
            _lodTransitionUpdatingCapacity);
        if (_lodTransitionUpdatingResetIndices.Count == 0)
            return;

        foreach (uint index in _lodTransitionUpdatingResetIndices)
        {
            if (_lodTransitionPublishedResetIndexSet.Add(index))
                _lodTransitionPublishedResetIndices.Add(index);
        }
        _lodTransitionUpdatingResetIndices.Clear();
        _lodTransitionUpdatingResetIndexSet.Clear();
    }

    /// <summary>
    /// Applies published LOD edits on the render owner before any LOD consumer.
    /// Returns false while the required GPU copy is incomplete.
    /// </summary>
    internal bool TryPrepareLodTransitionBuffer(
        AbstractRenderer renderer,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!RuntimeEngine.IsRenderThread)
        {
            failureReason = "LOD transition publication requires the render thread.";
            return false;
        }
        if (_lodTransitionPublicationUnsafe)
        {
            failureReason = "LOD transition publication cannot retry after an unfenced partial copy.";
            return false;
        }

        if (_lodTransitionPublicationFence is { } fence)
        {
            if (fence.SubmissionStatus == EGpuFenceSubmissionStatus.AwaitingSubmission)
            {
                failureReason = "LOD transition publication awaits GPU submission.";
                return false;
            }

            bool submitted = fence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted;
            EGpuFenceStatus status = submitted ? fence.Poll() : EGpuFenceStatus.Failed;
            if (status == EGpuFenceStatus.Pending)
            {
                failureReason = "LOD transition publication awaits GPU completion.";
                return false;
            }

            bool provenUnsubmitted = !submitted &&
                fence.FirstFailureDiagnostic?.NativeSubmission == EGpuFenceNativeSubmission.NotCalled;
            fence.Dispose();
            _lodTransitionPublicationFence = null;
            if (!submitted || status == EGpuFenceStatus.Failed)
            {
                if (provenUnsubmitted)
                {
                    _lodTransitionPublicationAccepted = false;
                    failureReason = "LOD transition publication was discarded before GPU submission and will retry.";
                    return false;
                }

                _lodTransitionPublicationUnsafe = true;
                failureReason = "LOD transition publication has no proven GPU completion.";
                return false;
            }

            if (!_lodTransitionPublicationAccepted)
            {
                _lodTransitionCandidateBuffer?.RetireBindingStorage();
                _lodTransitionCandidateBuffer = null;
                _lodTransitionPublicationAccepted = false;
                failureReason = "LOD transition publication failed and will retry.";
                return false;
            }

            if (_lodTransitionCandidateBuffer is { } candidate)
            {
                XRDataBuffer old = LodTransitionBuffer;
                _lodTransitionBuffer = candidate;
                _lodTransitionCandidateBuffer = null;
                old.RetireBindingStorage();
            }

            _lodTransitionActiveResetIndices = null;
            _lodTransitionActiveCapacity = 0u;
            _lodTransitionPublicationAccepted = false;
        }

        if (_lodTransitionActiveResetIndices is null)
        {
            using (_lock.EnterScope())
            {
                if (_lodTransitionPublishedResetIndices.Count == 0 &&
                    _lodTransitionPublishedCapacity <= LodTransitionBuffer.ElementCount)
                    return true;

                _lodTransitionActiveCapacity = Math.Max(
                    _lodTransitionPublishedCapacity,
                    LodTransitionBuffer.ElementCount);
                uint[] resets = new uint[_lodTransitionPublishedResetIndices.Count];
                _lodTransitionPublishedResetIndices.CopyTo(resets);
                Array.Sort(resets);
                _lodTransitionActiveResetIndices = resets;
                _lodTransitionPublishedResetIndices.Clear();
                _lodTransitionPublishedResetIndexSet.Clear();
                _lodTransitionPublishedCapacity = 0u;
            }
        }

        if (_lodTransitionActiveCapacity > LodTransitionBuffer.ElementCount &&
            _lodTransitionCandidateBuffer is null)
            _lodTransitionCandidateBuffer = MakeLodTransitionBuffer(
                _lodTransitionActiveCapacity);

        _lodTransitionZeroBuffer ??= MakeLodTransitionBuffer(
            1u,
            "RenderLodTransitionZeroBuffer");
        _lodTransitionPublicationRenderer = renderer;
        _lodTransitionPublicationProducer ??= AuthorLodTransitionPublication;
        _lodTransitionPublicationFailureReason = null;
        bool accepted = renderer.TryExecuteRequiredGpuProducerBatch(
            _lodTransitionPublicationProducer,
            out XRGpuFence? retentionFence,
            out Exception? failure);
        if (failure is not null)
            _lodTransitionPublicationFailureReason = failure.Message;
        _lodTransitionPublicationAccepted = accepted;
        _lodTransitionPublicationFence = retentionFence;
        if (retentionFence is null)
        {
            if (accepted ||
                renderer.GetAdvancedRenderPipelineCapabilities().Backend !=
                RuntimeGraphicsApiKind.Vulkan)
                _lodTransitionPublicationUnsafe = true;
            failureReason = failure?.Message ?? _lodTransitionPublicationFailureReason ??
                "LOD transition publication has no GPU completion fence.";
            return false;
        }

        failureReason = accepted
            ? "LOD transition publication awaits GPU completion."
            : failure?.Message ?? _lodTransitionPublicationFailureReason ??
                "LOD transition publication was not accepted.";
        return false;
    }

    /// <summary>Authors the copy and reset operations in one ordered GPU batch.</summary>
    private bool AuthorLodTransitionPublication()
    {
        AbstractRenderer renderer = _lodTransitionPublicationRenderer
            ?? throw new InvalidOperationException("LOD transition publication has no renderer.");
        XRDataBuffer old = LodTransitionBuffer;
        XRDataBuffer target = _lodTransitionCandidateBuffer ?? old;
        uint[] resets = _lodTransitionActiveResetIndices
            ?? throw new InvalidOperationException("LOD transition publication has no reset snapshot.");
        uint stride = LodTransitionUIntCount * sizeof(uint);

        if (_lodTransitionCandidateBuffer is not null && old.ElementCount > 0u)
        {
            nuint byteCount = checked((nuint)old.ElementCount * stride);
            ERendererComputeEnqueueStatus copyStatus = renderer.TryEnqueueGpuBufferCopy(
                old, 0, target, 0, byteCount,
                "LodTransition.Preserve",
                new LodTransitionCopyLease(old, target));
            if (copyStatus != ERendererComputeEnqueueStatus.Enqueued)
            {
                _lodTransitionPublicationFailureReason =
                    $"LOD transition preserve copy was not enqueued: {copyStatus}.";
                return false;
            }
            ERendererComputeEnqueueStatus barrierStatus =
                renderer.TryMemoryBarrier(EMemoryBarrierMask.BufferUpdate);
            if (barrierStatus != ERendererComputeEnqueueStatus.Enqueued)
            {
                _lodTransitionPublicationFailureReason =
                    $"LOD transition preserve barrier was not enqueued: {barrierStatus}.";
                return false;
            }
        }

        XRDataBuffer zero = _lodTransitionZeroBuffer
            ?? throw new InvalidOperationException("LOD transition publication has no zero source.");
        uint lastIndex = uint.MaxValue;
        for (int index = 0; index < resets.Length; index++)
        {
            uint row = resets[index];
            if (row == lastIndex || row >= target.ElementCount ||
                (_lodTransitionCandidateBuffer is not null && row >= old.ElementCount))
                continue;
            lastIndex = row;
            nint byteOffset = checked((nint)(row * stride));
            ERendererComputeEnqueueStatus resetStatus = renderer.TryEnqueueGpuBufferCopy(
                zero, 0, target, byteOffset, stride,
                "LodTransition.Reset",
                new LodTransitionCopyLease(zero, target));
            if (resetStatus != ERendererComputeEnqueueStatus.Enqueued)
            {
                _lodTransitionPublicationFailureReason =
                    $"LOD transition reset copy for row {row} was not enqueued: {resetStatus}.";
                return false;
            }
        }

        ERendererComputeEnqueueStatus finalBarrierStatus = renderer.TryMemoryBarrier(
            EMemoryBarrierMask.ShaderStorage |
            EMemoryBarrierMask.BufferUpdate);
        if (finalBarrierStatus == ERendererComputeEnqueueStatus.Enqueued)
            return true;

        _lodTransitionPublicationFailureReason =
            $"LOD transition consumer barrier was not enqueued: {finalBarrierStatus}.";
        return false;
    }

    /// <summary>Releases source buffers and copy receipts during scene teardown.</summary>
    private void ReleaseLodTransitionPublicationState()
    {
        _lodTransitionPublicationFence?.Dispose();
        _lodTransitionPublicationFence = null;
        _lodTransitionCandidateBuffer?.RetireBindingStorage();
        _lodTransitionCandidateBuffer = null;
        _lodTransitionZeroBuffer?.RetireBindingStorage();
        _lodTransitionZeroBuffer = null;
        _lodTransitionActiveResetIndices = null;
        _lodTransitionActiveCapacity = 0u;
        _lodTransitionPublishedCapacity = 0u;
        _lodTransitionUpdatingCapacity = 0u;
        _lodTransitionUpdatingResetIndices.Clear();
        _lodTransitionUpdatingResetIndexSet.Clear();
        _lodTransitionPublishedResetIndices.Clear();
        _lodTransitionPublishedResetIndexSet.Clear();
        _lodTransitionPublicationAccepted = false;
        _lodTransitionPublicationUnsafe = false;
        _lodTransitionPublicationRenderer = null;
        _lodTransitionPublicationFailureReason = null;
    }

}
