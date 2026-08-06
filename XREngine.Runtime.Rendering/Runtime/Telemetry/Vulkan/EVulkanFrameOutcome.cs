namespace XREngine.Rendering.Vulkan;

/// <summary>Typed result recorded for a lifecycle interval or complete frame root.</summary>
public enum EVulkanFrameOutcome
{
    NotReached,
    Completed,
    Deferred,
    Skipped,
    Rejected,
    Failed,
}
