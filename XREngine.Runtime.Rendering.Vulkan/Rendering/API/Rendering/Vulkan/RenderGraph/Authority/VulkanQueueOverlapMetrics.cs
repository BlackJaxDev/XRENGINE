namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct VulkanQueueOverlapMetrics(
    int ComputePassCount,
    int TransferUsageCount,
    int OverlapCandidatePassCount,
    int TransferCost,
    int QueueOwnershipTransfers,
    int BarrierStageFlushes,
    TimeSpan FrameDelta);
