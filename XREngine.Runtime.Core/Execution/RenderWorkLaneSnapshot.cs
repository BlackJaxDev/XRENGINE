using XREngine.Data.Rendering;

namespace XREngine.Execution;

/// <summary>
/// Allocation-free diagnostic snapshot for one stable logical render lane.
/// </summary>
public readonly record struct RenderWorkLaneSnapshot(
    int LaneId,
    int ManagedThreadId,
    ERenderWorkerQos EffectiveQos,
    int MigratableQueueDepth,
    int AffineQueueDepth,
    int QueueCapacity,
    long ExecutedItemCount,
    long WakeCount,
    long EmptyWakeCount);
