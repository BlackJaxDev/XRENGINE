namespace XREngine.Execution;

/// <summary>Provides generation-checked operations for a native pooled batch.</summary>
internal interface IRenderWorkBatchHandle
{
    int GetItemCount(long generation);
    void SetItem(long generation, int itemIndex, in RenderWorkItem item);
    void SetDependent(long generation, int dependentSlot, int dependentItemIndex);
    RenderWorkBatchResult GetResult(long generation);
    void Cancel(long generation);
    void ReleaseLease(long generation);
}
