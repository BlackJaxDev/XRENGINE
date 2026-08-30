namespace XREngine.Execution;

/// <summary>
/// Generation-checked lease over one reusable render-work control block.
/// Dispose only after execution; a copied/stale lease cannot reset a newer batch.
/// </summary>
public readonly struct RenderWorkBatchLease : IDisposable
{
    private readonly RenderWorkBatch? _batch;

    internal RenderWorkBatchLease(RenderWorkBatch batch, long generation)
    {
        _batch = batch;
        Generation = generation;
    }

    public long Generation { get; }
    public int ItemCount => GetBatch().GetItemCount(Generation);
    public bool IsValid => _batch is not null;

    public void SetItem(int itemIndex, in RenderWorkItem item)
    {
        RenderWorkBatch batch = GetBatch();
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        batch.SetItem(Generation, itemIndex, item);
        batch.Domain.RecordBuildAllocation(allocationBefore);
    }

    public void SetDependent(int dependentSlot, int dependentItemIndex)
    {
        RenderWorkBatch batch = GetBatch();
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        batch.SetDependent(Generation, dependentSlot, dependentItemIndex);
        batch.Domain.RecordBuildAllocation(allocationBefore);
    }

    public RenderWorkBatchResult GetResult()
        => GetBatch().GetResult(Generation);

    public void Cancel()
        => GetBatch().Cancel(Generation);

    public void Dispose()
    {
        if (_batch is not { } batch)
            return;

        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        long mergeCostStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        batch.ReleaseLease(Generation);
        batch.Domain.RecordMergeOperation(allocationBefore, mergeCostStarted);
    }

    internal RenderWorkBatch Batch => GetBatch();

    private RenderWorkBatch GetBatch()
        => _batch ?? throw new ObjectDisposedException(nameof(RenderWorkBatchLease));
}
