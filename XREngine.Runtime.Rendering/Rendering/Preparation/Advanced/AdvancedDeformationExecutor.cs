namespace XREngine.Rendering;

/// <summary>
/// Executes the bounded plan and rejects implicit direct-vertex fallback on
/// every backend, including Vulkan.
/// </summary>
public sealed class AdvancedDeformationExecutor
{
    public AdvancedDeformationDispatchTelemetry Execute(
        AdvancedDeformationDispatchPlanner planner,
        IAdvancedDeformationDispatchBackend backend,
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs,
        EAdvancedPreparationConsumer consumers,
        EAdvancedDeformationExecutionMode mode,
        uint admissionOverflowCount)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(backend);

        if (mode == EAdvancedDeformationExecutionMode.DirectVertexDiagnostic)
        {
            return new AdvancedDeformationDispatchTelemetry(
                JobCount: checked((uint)jobs.Length),
                VertexCount: CountVertices(jobs),
                OutputBytes: CountBytes(jobs),
                DispatchCount: 0u,
                FamilyOverflowCount: planner.FamilyOverflowCount,
                AdmissionOverflowCount: admissionOverflowCount,
                GpuMilliseconds: 0.0);
        }

        if (!backend.SupportsAggregateCompute)
        {
            throw new NotSupportedException(
                $"{backend.Backend} does not provide the required aggregate deformation compute path. Select DirectVertexDiagnostic explicitly for compatibility diagnostics.");
        }

        ReadOnlySpan<AdvancedDeformationDispatchBatch> batches =
            planner.Batches;
        ReadOnlySpan<int> indices = planner.JobIndices;
        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            AdvancedDeformationDispatchBatch batch = batches[batchIndex];
            backend.Dispatch(
                batch,
                indices.Slice(
                    checked((int)batch.FirstJobIndex),
                    checked((int)batch.JobCount)));
        }

        Span<AdvancedPreparationBarrier> barriers =
            stackalloc AdvancedPreparationBarrier[9];
        if (!AdvancedDeformationBarrierContract.TryWriteRequired(
                consumers,
                barriers,
                out int barrierCount))
        {
            throw new InvalidOperationException(
                "The fixed deformation barrier plan is too small.");
        }

        for (int barrierIndex = 0; barrierIndex < barrierCount; barrierIndex++)
            backend.ApplyBarrier(barriers[barrierIndex]);

        return new AdvancedDeformationDispatchTelemetry(
            JobCount: checked((uint)jobs.Length),
            VertexCount: CountVertices(jobs),
            OutputBytes: CountBytes(jobs),
            DispatchCount: checked((uint)batches.Length),
            FamilyOverflowCount: planner.FamilyOverflowCount,
            AdmissionOverflowCount: admissionOverflowCount,
            GpuMilliseconds: backend.LastGpuMilliseconds);
    }

    private static ulong CountVertices(
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs)
    {
        ulong count = 0UL;
        for (int i = 0; i < jobs.Length; i++)
            count += jobs[i].VertexCount;
        return count;
    }

    private static ulong CountBytes(
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs)
    {
        ulong count = 0UL;
        for (int i = 0; i < jobs.Length; i++)
            count += (ulong)jobs[i].VertexCount * jobs[i].OutputStride;
        return count;
    }
}
