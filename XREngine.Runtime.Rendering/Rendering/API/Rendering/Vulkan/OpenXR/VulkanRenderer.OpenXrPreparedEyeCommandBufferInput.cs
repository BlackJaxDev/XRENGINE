namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct OpenXrPreparedEyeCommandBufferInput(
        OpenXrEyeSwapchainRenderRequest Request,
        OpenXrEyeRenderTargetContext TargetContext,
        FrameOp[] Ops,
        FrameOpContext PlannerContext,
        ulong FrameOpsSignature,
        ulong PlannerRevision,
        CommandChainSchedule? CommandChainSchedule);
}
