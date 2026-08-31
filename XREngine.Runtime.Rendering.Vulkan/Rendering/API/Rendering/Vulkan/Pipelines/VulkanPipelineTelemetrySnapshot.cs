namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Cumulative, device-lifetime pipeline telemetry. It is deliberately separate
/// from per-frame renderer statistics so fresh-process cache scenarios can
/// compare an explicit steady-state interval without inferring totals from a
/// rolling frame counter.
/// </summary>
public sealed record VulkanPipelineTelemetrySnapshot
{
    public long GraphicsPipelineCreateCount { get; init; }
    public long ComputePipelineCreateCount { get; init; }
    public long WorkerPipelineCreateCount { get; init; }
    public long ForegroundPipelineWaitCount { get; init; }
    public long AsyncQueueCount { get; init; }
    public long RenderThreadShaderCompileCount { get; init; }
    public int PendingGraphicsPipelineCount { get; init; }
    public int PendingComputePipelineCount { get; init; }

    public static VulkanPipelineTelemetrySnapshot operator -(
        VulkanPipelineTelemetrySnapshot current,
        VulkanPipelineTelemetrySnapshot baseline)
        => new()
        {
            GraphicsPipelineCreateCount = current.GraphicsPipelineCreateCount - baseline.GraphicsPipelineCreateCount,
            ComputePipelineCreateCount = current.ComputePipelineCreateCount - baseline.ComputePipelineCreateCount,
            WorkerPipelineCreateCount = current.WorkerPipelineCreateCount - baseline.WorkerPipelineCreateCount,
            ForegroundPipelineWaitCount = current.ForegroundPipelineWaitCount - baseline.ForegroundPipelineWaitCount,
            AsyncQueueCount = current.AsyncQueueCount - baseline.AsyncQueueCount,
            RenderThreadShaderCompileCount = current.RenderThreadShaderCompileCount - baseline.RenderThreadShaderCompileCount,
            PendingGraphicsPipelineCount = current.PendingGraphicsPipelineCount,
            PendingComputePipelineCount = current.PendingComputePipelineCount,
        };
}
