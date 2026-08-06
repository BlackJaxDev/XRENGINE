using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable per-root lifecycle publication shared by runtime and diagnostic consumers.</summary>
public readonly record struct VulkanFrameTelemetryPublication(
    long AuthorityId,
    long PublicationSequence,
    VulkanFrameRootIdentity Identity,
    TimeSpan TotalElapsed,
    EVulkanFrameOutcome Outcome,
    VulkanFrameDetailTelemetry Detail,
    VulkanFrameStageTiming FramePacing,
    VulkanFrameStageTiming SnapshotHandoff,
    VulkanFrameStageTiming CompletionMaintenance,
    VulkanFrameStageTiming OutputAcquire,
    VulkanFrameStageTiming PlanBuild,
    VulkanFrameStageTiming ResourcePrepare,
    VulkanFrameStageTiming WorkSchedule,
    VulkanFrameStageTiming CommandRecord,
    VulkanFrameStageTiming SubmitPrepare,
    VulkanFrameStageTiming QueueSubmit,
    VulkanFrameStageTiming OutputComplete,
    VulkanFrameStageTiming FrameSettlement)
{
    /// <summary>Returns a stable stage by its schema identifier.</summary>
    public VulkanFrameStageTiming GetStage(EVulkanFrameStage stage)
        => stage switch
        {
            EVulkanFrameStage.FramePacing => FramePacing,
            EVulkanFrameStage.SnapshotHandoff => SnapshotHandoff,
            EVulkanFrameStage.CompletionMaintenance => CompletionMaintenance,
            EVulkanFrameStage.OutputAcquire => OutputAcquire,
            EVulkanFrameStage.PlanBuild => PlanBuild,
            EVulkanFrameStage.ResourcePrepare => ResourcePrepare,
            EVulkanFrameStage.WorkSchedule => WorkSchedule,
            EVulkanFrameStage.CommandRecord => CommandRecord,
            EVulkanFrameStage.SubmitPrepare => SubmitPrepare,
            EVulkanFrameStage.QueueSubmit => QueueSubmit,
            EVulkanFrameStage.OutputComplete => OutputComplete,
            EVulkanFrameStage.FrameSettlement => FrameSettlement,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
}
