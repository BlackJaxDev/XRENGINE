using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanQueryPoolKey(
        QueryType QueryType,
        QueryPipelineStatisticFlags PipelineStatistics,
        EVulkanQueryRecordingProvider Provider,
        uint QueueFamily,
        uint ValuesPerQuery,
        ERenderQueryProperty Property);
}
