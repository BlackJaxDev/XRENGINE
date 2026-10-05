namespace XREngine.Rendering.Vulkan;

/// <summary>Cross-worker work overlap and load spread within one frame.</summary>
public readonly record struct VulkanCpuFrameParallelism(
    long FrameId,
    int WorkerCount,
    long WorkUnionTicks,
    long WorkSumTicks,
    long ConcurrentWorkerTicks,
    long ImbalanceTicks,
    long CoordinatorWaitTicks);
