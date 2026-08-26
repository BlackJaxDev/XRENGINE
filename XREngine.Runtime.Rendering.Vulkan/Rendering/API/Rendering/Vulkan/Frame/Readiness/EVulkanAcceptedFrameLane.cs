namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Independently bounded storage lanes owned by an accepted Vulkan frame.
/// Saturating one lane must not consume the capacity reserved for another.
/// </summary>
internal enum EVulkanAcceptedFrameLane
{
    Terminal,
    Ui,
    MainScene,
    Shadow,
    Upload,
    Dependency,
    ResourceUse,
    Output,
    View,
    PlannerContext,
    FrameSlot,
}
