using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Fully validated Vulkan representation of one engine query descriptor.
/// </summary>
public readonly record struct VulkanQueryPlan(
    bool Supported,
    QueryType QueryType,
    QueryPipelineStatisticFlags PipelineStatistics,
    QueryControlFlags ControlFlags,
    RenderQueryResultLayout ResultLayout,
    EVulkanQueryRecordingProvider Provider,
    string? UnsupportedReason)
{
    public static VulkanQueryPlan Unsupported(
        in RenderQueryDescriptor descriptor,
        ERenderQueryReadStatus status,
        string reason)
        => new(
            false,
            default,
            QueryPipelineStatisticFlags.None,
            QueryControlFlags.None,
            new(
                descriptor.Kind,
                0u,
                0u,
                0u,
                -1,
                ERenderQueryIntegerWidth.UInt64,
                ERenderQueryAggregation.ProviderDefined,
                descriptor.Statistics,
                descriptor.Property),
            EVulkanQueryRecordingProvider.Unsupported,
            $"{status}: {reason}");
}
