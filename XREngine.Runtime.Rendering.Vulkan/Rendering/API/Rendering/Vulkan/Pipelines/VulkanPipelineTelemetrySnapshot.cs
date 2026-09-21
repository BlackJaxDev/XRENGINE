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
    public double ForegroundPipelineWaitMilliseconds { get; init; }
    public long AsyncQueueCount { get; init; }
    public long RenderThreadShaderCompileCount { get; init; }
    public int PendingGraphicsPipelineCount { get; init; }
    public int PendingComputePipelineCount { get; init; }
    public long AdditiveProgramLinkCount { get; init; }
    public long DependencyMutationCount { get; init; }
    public long ScopedDependencyMutationCount { get; init; }
    public long GlobalDependencyInvalidationCount { get; init; }
    public long DrainedGraphicsPipelineJobCount { get; init; }
    public long DrainedComputePipelineJobCount { get; init; }
    public long MutationPublicationWaitCount { get; init; }
    public double MutationPublicationWaitMilliseconds { get; init; }
    public long StaleCompletionCount { get; init; }
    public long StalePipelineDisposalCount { get; init; }
    public long ForegroundNativePipelineCreateCount { get; init; }
    public double ForegroundNativePipelineCreateMilliseconds { get; init; }
    public double ForegroundNativePipelineCreateMaxMilliseconds { get; init; }
    public long BackgroundNativePipelineCreateCount { get; init; }
    public double BackgroundNativePipelineCreateMilliseconds { get; init; }
    public double BackgroundNativePipelineCreateMaxMilliseconds { get; init; }
    public long ForegroundPipelineCacheHostWaitCount { get; init; }
    public double ForegroundPipelineCacheHostWaitMilliseconds { get; init; }
    public double ForegroundPipelineCacheHostWaitMaxMilliseconds { get; init; }
    public long BackgroundPipelineCacheHostWaitCount { get; init; }
    public double BackgroundPipelineCacheHostWaitMilliseconds { get; init; }
    public double BackgroundPipelineCacheHostWaitMaxMilliseconds { get; init; }
    public long PipelineCacheProbeCount { get; init; }
    public long PipelineCacheProbeHitCount { get; init; }
    public long PipelineCacheProbeMissCount { get; init; }
    public long PipelineCacheProbeFailureCount { get; init; }
    public long PipelineCacheInitialDataRejectedCount { get; init; }
    public long PipelineCacheRecoveryCount { get; init; }
    public long PipelineCacheMergeCount { get; init; }
    public double PipelineCacheMergeMilliseconds { get; init; }
    public double PipelineCacheMergeMaxMilliseconds { get; init; }
    public long PipelineCacheCaptureCount { get; init; }
    public double PipelineCacheCaptureMilliseconds { get; init; }
    public double PipelineCacheCaptureMaxMilliseconds { get; init; }
    public long PipelineCacheCaptureBytes { get; init; }
    public long PipelineCacheWriteCount { get; init; }
    public double PipelineCacheWriteMilliseconds { get; init; }
    public double PipelineCacheWriteMaxMilliseconds { get; init; }
    public long PipelineCacheWriteBytes { get; init; }
    public string LastMutationReason { get; init; } = string.Empty;
    public string LastMutationScope { get; init; } = string.Empty;
    public int LastMutationAffectedOwnerCount { get; init; }

    public static VulkanPipelineTelemetrySnapshot operator -(
        VulkanPipelineTelemetrySnapshot current,
        VulkanPipelineTelemetrySnapshot baseline)
        => new()
        {
            GraphicsPipelineCreateCount = current.GraphicsPipelineCreateCount - baseline.GraphicsPipelineCreateCount,
            ComputePipelineCreateCount = current.ComputePipelineCreateCount - baseline.ComputePipelineCreateCount,
            WorkerPipelineCreateCount = current.WorkerPipelineCreateCount - baseline.WorkerPipelineCreateCount,
            ForegroundPipelineWaitCount = current.ForegroundPipelineWaitCount - baseline.ForegroundPipelineWaitCount,
            ForegroundPipelineWaitMilliseconds = current.ForegroundPipelineWaitMilliseconds - baseline.ForegroundPipelineWaitMilliseconds,
            AsyncQueueCount = current.AsyncQueueCount - baseline.AsyncQueueCount,
            RenderThreadShaderCompileCount = current.RenderThreadShaderCompileCount - baseline.RenderThreadShaderCompileCount,
            PendingGraphicsPipelineCount = current.PendingGraphicsPipelineCount,
            PendingComputePipelineCount = current.PendingComputePipelineCount,
            AdditiveProgramLinkCount = current.AdditiveProgramLinkCount - baseline.AdditiveProgramLinkCount,
            DependencyMutationCount = current.DependencyMutationCount - baseline.DependencyMutationCount,
            ScopedDependencyMutationCount = current.ScopedDependencyMutationCount - baseline.ScopedDependencyMutationCount,
            GlobalDependencyInvalidationCount = current.GlobalDependencyInvalidationCount - baseline.GlobalDependencyInvalidationCount,
            DrainedGraphicsPipelineJobCount = current.DrainedGraphicsPipelineJobCount - baseline.DrainedGraphicsPipelineJobCount,
            DrainedComputePipelineJobCount = current.DrainedComputePipelineJobCount - baseline.DrainedComputePipelineJobCount,
            MutationPublicationWaitCount = current.MutationPublicationWaitCount - baseline.MutationPublicationWaitCount,
            MutationPublicationWaitMilliseconds = current.MutationPublicationWaitMilliseconds - baseline.MutationPublicationWaitMilliseconds,
            StaleCompletionCount = current.StaleCompletionCount - baseline.StaleCompletionCount,
            StalePipelineDisposalCount = current.StalePipelineDisposalCount - baseline.StalePipelineDisposalCount,
            ForegroundNativePipelineCreateCount = current.ForegroundNativePipelineCreateCount - baseline.ForegroundNativePipelineCreateCount,
            ForegroundNativePipelineCreateMilliseconds = current.ForegroundNativePipelineCreateMilliseconds - baseline.ForegroundNativePipelineCreateMilliseconds,
            ForegroundNativePipelineCreateMaxMilliseconds = current.ForegroundNativePipelineCreateMaxMilliseconds,
            BackgroundNativePipelineCreateCount = current.BackgroundNativePipelineCreateCount - baseline.BackgroundNativePipelineCreateCount,
            BackgroundNativePipelineCreateMilliseconds = current.BackgroundNativePipelineCreateMilliseconds - baseline.BackgroundNativePipelineCreateMilliseconds,
            BackgroundNativePipelineCreateMaxMilliseconds = current.BackgroundNativePipelineCreateMaxMilliseconds,
            ForegroundPipelineCacheHostWaitCount = current.ForegroundPipelineCacheHostWaitCount - baseline.ForegroundPipelineCacheHostWaitCount,
            ForegroundPipelineCacheHostWaitMilliseconds = current.ForegroundPipelineCacheHostWaitMilliseconds - baseline.ForegroundPipelineCacheHostWaitMilliseconds,
            ForegroundPipelineCacheHostWaitMaxMilliseconds = current.ForegroundPipelineCacheHostWaitMaxMilliseconds,
            BackgroundPipelineCacheHostWaitCount = current.BackgroundPipelineCacheHostWaitCount - baseline.BackgroundPipelineCacheHostWaitCount,
            BackgroundPipelineCacheHostWaitMilliseconds = current.BackgroundPipelineCacheHostWaitMilliseconds - baseline.BackgroundPipelineCacheHostWaitMilliseconds,
            BackgroundPipelineCacheHostWaitMaxMilliseconds = current.BackgroundPipelineCacheHostWaitMaxMilliseconds,
            PipelineCacheProbeCount = current.PipelineCacheProbeCount - baseline.PipelineCacheProbeCount,
            PipelineCacheProbeHitCount = current.PipelineCacheProbeHitCount - baseline.PipelineCacheProbeHitCount,
            PipelineCacheProbeMissCount = current.PipelineCacheProbeMissCount - baseline.PipelineCacheProbeMissCount,
            PipelineCacheProbeFailureCount = current.PipelineCacheProbeFailureCount - baseline.PipelineCacheProbeFailureCount,
            PipelineCacheInitialDataRejectedCount = current.PipelineCacheInitialDataRejectedCount - baseline.PipelineCacheInitialDataRejectedCount,
            PipelineCacheRecoveryCount = current.PipelineCacheRecoveryCount - baseline.PipelineCacheRecoveryCount,
            PipelineCacheMergeCount = current.PipelineCacheMergeCount - baseline.PipelineCacheMergeCount,
            PipelineCacheMergeMilliseconds = current.PipelineCacheMergeMilliseconds - baseline.PipelineCacheMergeMilliseconds,
            PipelineCacheMergeMaxMilliseconds = current.PipelineCacheMergeMaxMilliseconds,
            PipelineCacheCaptureCount = current.PipelineCacheCaptureCount - baseline.PipelineCacheCaptureCount,
            PipelineCacheCaptureMilliseconds = current.PipelineCacheCaptureMilliseconds - baseline.PipelineCacheCaptureMilliseconds,
            PipelineCacheCaptureMaxMilliseconds = current.PipelineCacheCaptureMaxMilliseconds,
            PipelineCacheCaptureBytes = current.PipelineCacheCaptureBytes - baseline.PipelineCacheCaptureBytes,
            PipelineCacheWriteCount = current.PipelineCacheWriteCount - baseline.PipelineCacheWriteCount,
            PipelineCacheWriteMilliseconds = current.PipelineCacheWriteMilliseconds - baseline.PipelineCacheWriteMilliseconds,
            PipelineCacheWriteMaxMilliseconds = current.PipelineCacheWriteMaxMilliseconds,
            PipelineCacheWriteBytes = current.PipelineCacheWriteBytes - baseline.PipelineCacheWriteBytes,
            LastMutationReason = current.LastMutationReason,
            LastMutationScope = current.LastMutationScope,
            LastMutationAffectedOwnerCount = current.LastMutationAffectedOwnerCount,
        };
}
