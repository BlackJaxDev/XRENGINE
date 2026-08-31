namespace XREngine.Rendering.Vulkan;

/// <summary>A bounded readback reference to an actually submitted planner generation.</summary>
internal readonly record struct VulkanDesktopReadbackReceipt(
    VulkanFrameOpPlannerStateKey Key,
    FrameOpContext Context,
    ResourcePlannerRuntimeState PlannerState,
    ulong SubmissionSerial);
