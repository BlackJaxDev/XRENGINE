using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

internal sealed class AdvancedDeformationDispatchBackendProbe
    : IAdvancedDeformationDispatchBackend
{
    private readonly AdvancedDeformationDispatchBatch[] _batches = new AdvancedDeformationDispatchBatch[32];
    private readonly AdvancedPreparationBarrier[] _barriers = new AdvancedPreparationBarrier[16];

    public AdvancedDeformationDispatchBackendProbe(
        RuntimeGraphicsApiKind backend,
        bool supportsAggregateCompute = true)
    {
        Backend = backend;
        SupportsAggregateCompute = supportsAggregateCompute;
    }

    public RuntimeGraphicsApiKind Backend { get; }
    public bool SupportsAggregateCompute { get; }
    public double LastGpuMilliseconds { get; set; }
    public int DispatchCount { get; private set; }
    public int BarrierCount { get; private set; }
    public ReadOnlySpan<AdvancedDeformationDispatchBatch> Batches
        => _batches.AsSpan(0, DispatchCount);
    public ReadOnlySpan<AdvancedPreparationBarrier> Barriers
        => _barriers.AsSpan(0, BarrierCount);

    public void Dispatch(
        in AdvancedDeformationDispatchBatch batch,
        ReadOnlySpan<int> jobIndices)
    {
        if (jobIndices.Length != batch.JobCount)
            throw new InvalidOperationException("Probe received a partial aggregate batch.");
        _batches[DispatchCount++] = batch;
    }

    public void ApplyBarrier(in AdvancedPreparationBarrier barrier)
        => _barriers[BarrierCount++] = barrier;
}
