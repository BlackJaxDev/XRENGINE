using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    private void DestroyFrameBuffers()
    {
        if (OutputRuntime.Desktop.Framebuffers is null)
            return;

        foreach (var framebuffer in OutputRuntime.Desktop.Framebuffers)
        {
            if (framebuffer.Handle != 0)
                RetireFramebuffer(framebuffer);
        }

        DrainRetiredFramebuffers(CurrentDesktopFrameSlot, int.MaxValue);
        OutputRuntime.Desktop.Framebuffers = null;
    }

    private void CreateFramebuffers()
    {
        if (OutputRuntime.Desktop.ImageViews is null || OutputRuntime.Desktop.ImageViews.Length == 0)
            throw new InvalidOperationException("Swapchain image views must be created before framebuffers.");

        OutputRuntime.Desktop.Framebuffers = new Framebuffer[OutputRuntime.Desktop.ImageViews.Length];
        if (UseDynamicRenderingRenderTargets)
        {
            AllocateCommandBufferDirtyFlags();
            return;
        }

        ImageView[] attachments = new ImageView[2];

        for (int i = 0; i < OutputRuntime.Desktop.ImageViews.Length; i++)
        {
            attachments[0] = OutputRuntime.Desktop.ImageViews[i];
            attachments[1] = _swapchainDepthView;

            fixed (ImageView* attachmentsPtr = attachments)
            {
                FramebufferCreateInfo framebufferInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = ResourceRuntime.SwapchainRenderPass,
                    AttachmentCount = 2,
                    PAttachments = attachmentsPtr,
                    Width = OutputRuntime.Desktop.Extent.Width,
                    Height = OutputRuntime.Desktop.Extent.Height,
                    Layers = 1,
                };

                if (Api!.CreateFramebuffer(_deviceContext.Device, ref framebufferInfo, null, out OutputRuntime.Desktop.Framebuffers[i]) != Result.Success)
                    throw new Exception("Failed to create framebuffer.");

                RegisterVulkanFramebuffer(
                    OutputRuntime.Desktop.Framebuffers[i],
                    attachments,
                    $"Swapchain.Framebuffer[{i}]");
                SetDebugObjectName(ObjectType.Framebuffer, OutputRuntime.Desktop.Framebuffers[i].Handle, $"Swapchain.Framebuffer[{i}]");
            }
        }

        AllocateCommandBufferDirtyFlags();
    }
}
