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
        if (!TryExecute(
                planner,
                backend,
                jobs,
                consumers,
                mode,
                admissionOverflowCount,
                out AdvancedDeformationDispatchTelemetry telemetry,
                out ERendererComputeEnqueueStatus status,
                out _))
        {
            throw new InvalidOperationException(
                $"Aggregate deformation enqueue failed with {status}.");
        }

        return telemetry;
    }

    public bool TryExecute(
        AdvancedDeformationDispatchPlanner planner,
        IAdvancedDeformationDispatchBackend backend,
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs,
        EAdvancedPreparationConsumer consumers,
        EAdvancedDeformationExecutionMode mode,
        uint admissionOverflowCount,
        out AdvancedDeformationDispatchTelemetry telemetry,
        out ERendererComputeEnqueueStatus status,
        out uint enqueuedDispatchCount)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(backend);
        enqueuedDispatchCount = 0u;

        if (mode == EAdvancedDeformationExecutionMode.DirectVertexDiagnostic)
        {
            telemetry = CreateTelemetry(
                planner,
                backend,
                jobs,
                admissionOverflowCount,
                dispatchCount: 0u);
            status = ERendererComputeEnqueueStatus.Enqueued;
            return true;
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
            status = backend.TryDispatch(
                in batch,
                indices.Slice(
                    checked((int)batch.FirstJobIndex),
                    checked((int)batch.JobCount)));
            if (status != ERendererComputeEnqueueStatus.Enqueued)
            {
                telemetry = CreateTelemetry(
                    planner,
                    backend,
                    jobs,
                    admissionOverflowCount,
                    enqueuedDispatchCount);
                return false;
            }

            enqueuedDispatchCount++;
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
        {
            status = backend.TryApplyBarrier(in barriers[barrierIndex]);
            if (status != ERendererComputeEnqueueStatus.Enqueued)
            {
                telemetry = CreateTelemetry(
                    planner,
                    backend,
                    jobs,
                    admissionOverflowCount,
                    enqueuedDispatchCount);
                return false;
            }
        }

        telemetry = CreateTelemetry(
            planner,
            backend,
            jobs,
            admissionOverflowCount,
            enqueuedDispatchCount);
        status = ERendererComputeEnqueueStatus.Enqueued;
        return true;
    }

    private static AdvancedDeformationDispatchTelemetry CreateTelemetry(
        AdvancedDeformationDispatchPlanner planner,
        IAdvancedDeformationDispatchBackend backend,
        ReadOnlySpan<AdvancedDeformationJobRecord> jobs,
        uint admissionOverflowCount,
        uint dispatchCount)
        => new(
            JobCount: checked((uint)jobs.Length),
            VertexCount: CountVertices(jobs),
            OutputBytes: CountBytes(jobs),
            DispatchCount: dispatchCount,
            FamilyOverflowCount: planner.FamilyOverflowCount,
            AdmissionOverflowCount: admissionOverflowCount,
            GpuMilliseconds: backend.LastGpuMilliseconds);

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