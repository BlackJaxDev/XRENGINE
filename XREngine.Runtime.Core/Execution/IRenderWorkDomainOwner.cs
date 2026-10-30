using XREngine.Data.Rendering;

namespace XREngine.Execution;

/// <summary>Owns the physical render workers, queues, batches, and shutdown state.</summary>
internal interface IRenderWorkDomainOwner
{
    int BackgroundWorkerCount { get; }
    int LogicalLaneCount { get; }
    int InlineItemThreshold { get; }
    int MaxMigratableItemCount { get; }
    ERenderWorkerQos Qos { get; }
    RenderLaneBackendAttachments BackendAttachments { get; }
    RenderWorkDomainMetrics Metrics { get; }
    RenderWorkLaneSnapshot GetLaneSnapshot(int laneId);
    RenderWorkBatchLease RentBatch(int itemCount, int dependencyCount);
    RenderWorkBatchResult ExecuteAndWait(ref RenderWorkBatchLease lease, IRenderWorkExecutor executor,
        int frameSlot, TimeSpan? timeout, CancellationToken cancellationToken);
    bool Shutdown(bool waitForWorkers, TimeSpan timeout);
}
