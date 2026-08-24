namespace XREngine.Execution;

/// <summary>
/// Terminal result of a sealed render-work generation.
/// Partial output is valid only when <see cref="Succeeded"/> is true.
/// </summary>
public readonly record struct RenderWorkBatchResult(
    long Generation,
    RenderWorkBatchStatus Status,
    int ItemCount,
    Exception? Exception)
{
    public bool Succeeded => Status == RenderWorkBatchStatus.Completed;
    public bool IsCanceled => Status == RenderWorkBatchStatus.Canceled;
    public bool IsFaulted => Status == RenderWorkBatchStatus.Faulted;
}
