using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Exact query state inherited by a non-render-pass secondary command buffer.
/// XRENGINE currently admits secondaries only when the primary has no active
/// query, so query flags and pipeline statistics remain explicitly disabled.
/// </summary>
internal readonly record struct VulkanQuerySecondaryInheritanceContract(
    bool PrimaryQueryActive,
    bool InheritedQueriesEnabled,
    bool OcclusionQueryEnable,
    QueryControlFlags QueryFlags,
    QueryPipelineStatisticFlags PipelineStatistics)
{
    internal bool CanExecuteWithoutInheritedQueryState
        => !PrimaryQueryActive &&
           !OcclusionQueryEnable &&
           QueryFlags == QueryControlFlags.None &&
           PipelineStatistics == QueryPipelineStatisticFlags.None;

    internal static VulkanQuerySecondaryInheritanceContract Create(
        bool primaryQueryActive,
        bool inheritedQueriesEnabled)
        => new(
            primaryQueryActive,
            inheritedQueriesEnabled,
            OcclusionQueryEnable: false,
            QueryControlFlags.None,
            QueryPipelineStatisticFlags.None);
}
