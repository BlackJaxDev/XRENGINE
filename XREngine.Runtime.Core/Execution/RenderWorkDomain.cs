using System.Diagnostics;
using XREngine.Data.Rendering;

namespace XREngine.Execution;

/// <summary>Provides the public render-work API over a native worker owner.</summary>
public sealed class RenderWorkDomain : IDisposable
{
    public const int DefaultFrameSlotCount = 3;
    public const int DefaultQueueCapacityPerLane = 4096;
    public const int DefaultBatchPoolCapacity = 8;
    public const int DefaultInitialItemCapacity = 256;
    public const int DefaultInitialDependencyCapacity = 512;
    public const int DefaultInlineItemThreshold = 4;
    public const int MaximumMigratableItemsPerLogicalLane = 4;
    public static readonly long LockWaitThresholdTicks = Math.Max(1L, Stopwatch.Frequency / 10_000L);
    public static readonly TimeSpan FatalBatchWait = TimeSpan.FromSeconds(2);

    private readonly IRenderWorkDomainOwner _owner;

    public RenderWorkDomain(int backgroundWorkerCount, ERenderWorkerQos qos,
        int frameSlotCount = DefaultFrameSlotCount,
        int queueCapacityPerLane = DefaultQueueCapacityPerLane,
        int batchPoolCapacity = DefaultBatchPoolCapacity,
        int initialItemCapacity = DefaultInitialItemCapacity,
        int initialDependencyCapacity = DefaultInitialDependencyCapacity,
        int inlineItemThreshold = DefaultInlineItemThreshold)
        : this(backgroundWorkerCount, qos, frameSlotCount, queueCapacityPerLane,
            batchPoolCapacity, initialItemCapacity, initialDependencyCapacity,
            inlineItemThreshold, null)
    {
    }

    internal RenderWorkDomain(int backgroundWorkerCount, ERenderWorkerQos qos,
        int frameSlotCount, int queueCapacityPerLane, int batchPoolCapacity,
        int initialItemCapacity, int initialDependencyCapacity, int inlineItemThreshold,
        IEngineWorkerDomainFactory? workerFactory)
    {
        if (RuntimeWorkScheduler.IsCallerThread || OperatingSystem.IsBrowser())
            throw new InvalidOperationException(
                "RenderWorkDomain requires worker-thread execution and cannot be constructed for a caller-thread host.");
        if (backgroundWorkerCount is < 0 or > EngineExecutionTopology.MaximumWorkerCount)
            throw new ArgumentOutOfRangeException(nameof(backgroundWorkerCount));
        if (!Enum.IsDefined(qos))
            throw new ArgumentOutOfRangeException(nameof(qos));
        if (inlineItemThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(inlineItemThreshold));
        if (frameSlotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSlotCount));
        _ = checked((backgroundWorkerCount + 1) * frameSlotCount);
        if (queueCapacityPerLane <= 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapacityPerLane));
        if (batchPoolCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchPoolCapacity));

        workerFactory ??= EngineWorkerDomainServices.GetRequiredFactory();
        if (workerFactory is not IEngineRenderWorkDomainFactory renderFactory)
            throw new InvalidOperationException(
                "The registered worker-domain factory does not provide native render-work execution.");
        _owner = renderFactory.CreateRenderDomain(backgroundWorkerCount, qos,
            frameSlotCount, queueCapacityPerLane, batchPoolCapacity,
            initialItemCapacity, initialDependencyCapacity, inlineItemThreshold);
    }

    public int BackgroundWorkerCount => _owner.BackgroundWorkerCount;
    public int LogicalLaneCount => _owner.LogicalLaneCount;
    public int InlineItemThreshold => _owner.InlineItemThreshold;
    public int MaxMigratableItemCount => _owner.MaxMigratableItemCount;
    public ERenderWorkerQos Qos => _owner.Qos;
    public RenderLaneBackendAttachments BackendAttachments => _owner.BackendAttachments;
    public RenderWorkDomainMetrics Metrics => _owner.Metrics;
    public RenderWorkLaneSnapshot GetLaneSnapshot(int laneId) => _owner.GetLaneSnapshot(laneId);
    public RenderWorkBatchLease RentBatch(int itemCount, int dependencyCount = 0)
        => _owner.RentBatch(itemCount, dependencyCount);
    public RenderWorkBatchResult ExecuteAndWait(ref RenderWorkBatchLease lease,
        IRenderWorkExecutor executor, int frameSlot = 0, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => _owner.ExecuteAndWait(ref lease, executor, frameSlot, timeout, cancellationToken);
    public bool Shutdown(bool waitForWorkers = true) => _owner.Shutdown(waitForWorkers, FatalBatchWait);
    internal bool Shutdown(bool waitForWorkers, TimeSpan timeout) => _owner.Shutdown(waitForWorkers, timeout);

    /// <summary>Releases native resources after all workers and leases stop.</summary>
    public void Dispose()
    {
        if (!Shutdown(waitForWorkers: true))
            throw new TimeoutException(
                "Render-work disposal timed out with live workers, batches, or leases. " +
                "Dependent executor/backend state must remain alive until a later clean shutdown.");
    }
}
