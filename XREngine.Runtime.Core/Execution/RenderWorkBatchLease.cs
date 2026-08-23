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
        => GetBatch().SetItem(Generation, itemIndex, item);

    public void SetDependent(int dependentSlot, int dependentItemIndex)
        => GetBatch().SetDependent(Generation, dependentSlot, dependentItemIndex);

    public RenderWorkBatchResult GetResult()
        => GetBatch().GetResult(Generation);

    public void Cancel()
        => GetBatch().Cancel(Generation);

    public void Dispose()
        => _batch?.ReleaseLease(Generation);

    internal RenderWorkBatch Batch => GetBatch();

    private RenderWorkBatch GetBatch()
        => _batch ?? throw new ObjectDisposedException(nameof(RenderWorkBatchLease));
}
