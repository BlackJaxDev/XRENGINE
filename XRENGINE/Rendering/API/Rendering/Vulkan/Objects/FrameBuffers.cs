using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private Framebuffer[]? swapChainFramebuffers;

    private void DestroyFrameBuffers()
    {
        if (swapChainFramebuffers is null)
            return;

        foreach (var framebuffer in swapChainFramebuffers)
        {
            if (framebuffer.Handle != 0)
                Api!.DestroyFramebuffer(device, framebuffer, null);
        }

        swapChainFramebuffers = null;
    }

    private void CreateFramebuffers()
    {
        if (swapChainImageViews is null || swapChainImageViews.Length == 0)
            throw new InvalidOperationException("Swapchain image views must be created before framebuffers.");

        swapChainFramebuffers = new Framebuffer[swapChainImageViews.Length];

        for (int i = 0; i < swapChainImageViews.Length; i++)
        {
            ImageView* attachmentsPtr = stackalloc ImageView[1];
            attachmentsPtr[0] = swapChainImageViews[i];

            FramebufferCreateInfo framebufferInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = attachmentsPtr,
                Width = swapChainExtent.Width,
                Height = swapChainExtent.Height,
                Layers = 1,
            };

            if (Api!.CreateFramebuffer(device, ref framebufferInfo, null, out swapChainFramebuffers[i]) != Result.Success)
                throw new Exception("Failed to create framebuffer.");
        }

        AllocateCommandBufferDirtyFlags();
    }
}