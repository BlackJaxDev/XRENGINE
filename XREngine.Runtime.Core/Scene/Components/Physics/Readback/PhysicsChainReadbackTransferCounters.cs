namespace XREngine.Components;

/// <summary>
/// Cumulative selective-transfer accounting. Elements are typed gather items;
/// bytes are exact packed layout bytes.
/// </summary>
public readonly record struct PhysicsChainReadbackTransferCounters(
    long RequestedElements,
    long RequestedBytes,
    long GatheredElements,
    long GatheredBytes,
    long TransferredElements,
    long TransferredBytes,
    long DiscardedStaleElements,
    long DiscardedStaleBytes,
    long DeliveredElements,
    long DeliveredBytes,
    PhysicsChainReadbackLatencyHistogram Latency);
