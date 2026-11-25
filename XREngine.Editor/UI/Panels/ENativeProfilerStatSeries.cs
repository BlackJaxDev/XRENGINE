namespace XREngine.Editor;

/// <summary>Identifies one render-stat value that the native profiler keeps as a per-frame history.</summary>
internal enum ENativeProfilerStatSeries
{
    None,
    VulkanPipelineBinds,
    VulkanDescriptorBinds,
    VulkanPipelineSkipPercent,
    VulkanDescriptorSkipPercent,
    VulkanPipelineCacheHitPercent,
    VulkanFrameTotalMs,
    VulkanWaitFenceMs,
    VulkanRecordCommandBufferMs,
    VulkanGpuCommandBufferMs,
    XrWaitFrameBlockMs,
    XrEndFrameSubmitMs,
    XrPredictedToLatePoseMm,
    XrPredictedDisplayLeadMs,
    Count,
}
