using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Initializes the owned physical output before any cropped terminal writes.
    /// This clear is not a producer receipt: missing required work must still fail.
    /// </summary>
    private unsafe void InitializeExplicitOutputColor(scoped ref PrimaryCommandBufferRecordingState state)
    {
        if (!state.Policy.InitializeOutputColor)
            return;
        if (!state.SwapchainTarget.IsValid || state.RenderScope.IsActive)
            throw new VulkanPlanPreconditionException("Exact output initialization requires an inactive scope and a valid owned target.");

        BeginRenderPassForTarget(ref state, null, VulkanBarrierPlanner.SwapchainPassIndex, state.InitialContext);
        var color = RuntimeEngine.StartupPresentationClearColor;
        ClearAttachment attachment = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            ColorAttachment = 0,
            ClearValue = new ClearValue { Color = new ClearColorValue(color.R, color.G, color.B, color.A) },
        };
        ClearRect rectangle = new()
        {
            Rect = new Rect2D { Extent = state.SwapchainTarget.Extent },
            LayerCount = 1,
        };
        Api.CmdClearAttachments(state.CommandBuffer, 1, &attachment, 1, &rectangle);
        state.SwapchainClearedThisFrame = true;
        EndActiveRenderPass(ref state);
    }
}
