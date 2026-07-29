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

    void ApplyBarrier(in AdvancedPreparationBarrier barrier);
}
