namespace XREngine.Rendering.Vulkan;

/// <summary>Stable coarse lifecycle stages used by every Vulkan frame output.</summary>
public enum EVulkanFrameStage
{
    FramePacing,
    SnapshotHandoff,
    CompletionMaintenance,
    OutputAcquire,
    PlanBuild,
    ResourcePrepare,
    WorkSchedule,
    CommandRecord,
    SubmitPrepare,
    QueueSubmit,
    OutputComplete,
    FrameSettlement,
    Count,
}
