namespace XREngine.Execution;

/// <summary>
/// One sealed-batch executor. Dispatch is by compact operation kind and source
/// range, avoiding a delegate or managed object per work item.
/// </summary>
public interface IRenderWorkExecutor
{
    /// <summary>
    /// Executes one bounded CPU preparation item. Implementations must not wait
    /// on GPU completion, tasks, fences, or other externally completed work and
    /// must return before <see cref="RenderWorkDomain.FatalBatchWait"/>. The
    /// scheduler cannot preempt arbitrary synchronous executor code on lane 0.
    /// </summary>
    void Execute(in RenderWorkItem item, ref RenderWorkerContext context);

    /// <summary>
    /// Invalidates or quarantines backend artifacts touched by a faulted batch.
    /// The callback must be bounded and nonthrowing. A thrown exception poisons
    /// the domain and retains the batch instead of silently reusing its storage.
    /// The default is sufficient for preparation-only batches with no native output.
    /// </summary>
    void QuarantineFaultedBatch(in RenderWorkBatchFaultContext context)
    {
    }
}
