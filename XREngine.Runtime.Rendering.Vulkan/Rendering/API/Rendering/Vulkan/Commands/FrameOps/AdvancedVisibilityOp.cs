namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Authoring operation for the first advanced visibility lane. It retains no
/// native framebuffer, buffer, or CPU readback result: those are admitted
/// only after the frame plan and its render-graph generation are frozen.
/// </summary>
internal sealed record AdvancedVisibilityOp(
    int PassIndex,
    VulkanAdvancedVisibilityStageRequest Request,
    FrameOpContext Context)
    : FrameOp(PassIndex, Request.Target, Context)
{
    public VulkanAdvancedVisibilityStageRequest Request { get; private set; } = Request;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.AdvancedVisibility;
}
