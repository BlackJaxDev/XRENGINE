using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the output-state checks and snapshot admission for the desktop ImGui
/// overlay. It deliberately has no renderer reference: command recording is a
/// separate operation that must be supplied by a typed command adapter.
/// </summary>
internal sealed class VulkanImGuiOverlayAdmission(
    VulkanOutputRuntime outputRuntime,
    VulkanResourceRuntime resourceRuntime,
    VulkanDeviceContext deviceContext)
{
    internal bool CanRecord(uint imageIndex)
    {
        if (RenderDiagnosticsFlags.VkSkipImGui)
            return false;

        VulkanImGuiResources resources = outputRuntime._imguiResources;
        if (resources.OverlayCommandBuffers is null ||
            imageIndex >= resources.OverlayCommandBuffers.Length ||
            outputRuntime.Desktop.Images is null ||
            imageIndex >= outputRuntime.Desktop.Images.Length)
        {
            return false;
        }

        bool useDynamicRendering = deviceContext.MutableCapabilities._useDynamicRenderingRenderTargets &&
            outputRuntime.Desktop.ImageViews is not null &&
            imageIndex < outputRuntime.Desktop.ImageViews.Length;
        if (!useDynamicRendering &&
            (outputRuntime.Desktop.Framebuffers is null ||
             imageIndex >= outputRuntime.Desktop.Framebuffers.Length ||
             resourceRuntime.SwapchainLoadRenderPass.Handle == 0))
        {
            return false;
        }

        return resources.OverlayCommandBuffers[imageIndex].Handle != 0;
    }

    internal bool TryConsumeRenderableSnapshot(
        bool interactiveResizeInProgress,
        out VulkanImGuiFrameSnapshot? drawData)
    {
        drawData = null;
        if (RenderDiagnosticsFlags.VkSkipImGui ||
            !outputRuntime._imguiDrawData.TryConsume(out drawData) ||
            drawData is null)
        {
            return false;
        }

        if (!HasRenderableSnapshot(drawData))
        {
            outputRuntime._imguiDrawData.Discard(drawData);
            drawData = null;
            return false;
        }

        bool snapshotMatchesSwapchain =
            drawData.FramebufferWidth == outputRuntime.Desktop.Extent.Width &&
            drawData.FramebufferHeight == outputRuntime.Desktop.Extent.Height;
        bool canMapLiveSnapshotToScaledSwapchain =
            outputRuntime.Desktop.PresentScalingActive && interactiveResizeInProgress;
        if (snapshotMatchesSwapchain || canMapLiveSnapshotToScaledSwapchain)
            return true;

        outputRuntime.RequestImGuiFrameMarkerReset();
        outputRuntime._imguiDrawData.Discard(drawData);
        drawData = null;
        return false;
    }

    internal static bool HasRenderableSnapshot(VulkanImGuiFrameSnapshot drawData)
        => drawData.TotalVertexCount > 0 &&
           drawData.TotalIndexCount > 0 &&
           drawData.CommandListCount > 0 &&
           drawData.DisplaySize.X > 0f &&
           drawData.DisplaySize.Y > 0f &&
           drawData.FramebufferWidth > 0 &&
           drawData.FramebufferHeight > 0;
}
