namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Explicit command-side data needed to install an OpenXR planner state on one
/// recording thread. This keeps the scope independent from the renderer facade.
/// </summary>
internal readonly record struct VulkanOpenXrResourcePlannerSessionToken(
    VulkanOpenXrThreadExecutionState ExecutionState,
    object StateGate,
    Dictionary<VulkanOpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> States,
    ulong PlannerGeneration);
