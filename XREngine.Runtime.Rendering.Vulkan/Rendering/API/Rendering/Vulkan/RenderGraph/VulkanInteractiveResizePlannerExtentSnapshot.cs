namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Captures the display and internal resource extents for one planner context at the start of an
/// interactive resize.
/// </summary>
internal readonly record struct VulkanInteractiveResizePlannerExtentSnapshot(
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight);
