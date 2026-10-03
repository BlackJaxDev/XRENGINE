namespace XREngine.Rendering.Vulkan;

/// <summary>Distribution for one selected CPU stage, in stopwatch ticks.</summary>
public readonly record struct VulkanCpuStageDistribution(
    EVulkanCpuStage Stage,
    int InvocationCount,
    long InclusiveTicks,
    long ExclusiveTicks,
    long MinimumTicks,
    long MedianTicks,
    long P95Ticks,
    long MaximumTicks,
    long AllocatedBytes);
