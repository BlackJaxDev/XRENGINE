namespace XREngine.Rendering;

/// <summary>
/// Native backend lowering for aggregate deformation. Production Vulkan and
/// OpenGL implementations must advertise aggregate compute explicitly.
/// </summary>
public interface IAdvancedDeformationDispatchBackend
{
    RuntimeGraphicsApiKind Backend { get; }
    bool SupportsAggregateCompute { get; }
    double LastGpuMilliseconds { get; }

    void Dispatch(
        in AdvancedDeformationDispatchBatch batch,
        ReadOnlySpan<int> jobIndices);

    /// <summary>
    /// Attempts to enqueue one aggregate batch. The default preserves
    /// diagnostic probes that execute synchronously; production backends must
    /// override this method and return their renderer's exact enqueue receipt.
    /// </summary>
    ERendererComputeEnqueueStatus TryDispatch(
        in AdvancedDeformationDispatchBatch batch,
        ReadOnlySpan<int> jobIndices)
    {
        Dispatch(in batch, jobIndices);
        return ERendererComputeEnqueueStatus.Enqueued;
    }

    void ApplyBarrier(in AdvancedPreparationBarrier barrier);

    /// <summary>
    /// Attempts to enqueue one consumer barrier after every deformation batch
    /// has been accepted.
    /// </summary>
    ERendererComputeEnqueueStatus TryApplyBarrier(
        in AdvancedPreparationBarrier barrier)
    {
        ApplyBarrier(in barrier);
        return ERendererComputeEnqueueStatus.Enqueued;
    }
}