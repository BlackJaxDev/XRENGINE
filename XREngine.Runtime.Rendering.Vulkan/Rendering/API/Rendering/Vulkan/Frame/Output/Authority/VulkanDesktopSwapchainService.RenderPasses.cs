using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Legacy-render-pass resources owned by the desktop WSI generation.  These
/// helpers deliberately do not touch command recording; callers must publish a
/// replacement command-artifact generation before replacing the native output.
/// </summary>
internal sealed unsafe partial class VulkanDesktopSwapchainService
{
    internal (RenderPass Clear, RenderPass Load) DetachRenderPasses()
    {
        (RenderPass Clear, RenderPass Load) detached = (
            _resources.SwapchainRenderPass,
            _resources.SwapchainLoadRenderPass);
        _resources.SwapchainRenderPass = default;
        _resources.SwapchainLoadRenderPass = default;
        return detached;
    }

    internal void DestroyRenderPassesImmediately()
    {
        (RenderPass clear, RenderPass load) = DetachRenderPasses();
        DestroyRenderPassImmediate(clear);
        DestroyRenderPassImmediate(load);
    }

    internal void CreateRenderPasses()
    {
        if (_device.MutableCapabilities._useDynamicRenderingRenderTargets)
        {
            _resources.SwapchainRenderPass = default;
            _resources.SwapchainLoadRenderPass = default;
            return;
        }

        _resources.SwapchainRenderPass = CreateRenderPass(AttachmentLoadOp.Clear);
        _resources.RegisterRenderPass(
            _resources.SwapchainRenderPass,
            [_output.Desktop.ImageFormat],
            BuildRenderPassSignature(AttachmentLoadOp.Clear));
        _resources.SwapchainLoadRenderPass = CreateRenderPass(AttachmentLoadOp.Load);
        _resources.RegisterRenderPass(
            _resources.SwapchainLoadRenderPass,
            [_output.Desktop.ImageFormat],
            BuildRenderPassSignature(AttachmentLoadOp.Load));
    }

    internal void CreateFramebuffers()
    {
        ImageView[] imageViews = _output.Desktop.ImageViews
            ?? throw new InvalidOperationException("Swapchain image views must be created before framebuffers.");
        _output.Desktop.Framebuffers = new Framebuffer[imageViews.Length];
        if (_device.MutableCapabilities._useDynamicRenderingRenderTargets)
            return;

        VulkanSwapchainDepthResources depth = _output.DesktopDepthResources
            ?? throw new InvalidOperationException("Swapchain depth resources must be created before framebuffers.");
        ImageView[] attachments = new ImageView[2];
        for (int index = 0; index < imageViews.Length; index++)
        {
            attachments[0] = imageViews[index];
            attachments[1] = depth.View;
            Framebuffer framebuffer = CreateSwapchainFramebuffer(attachments);
            _output.Desktop.Framebuffers[index] = framebuffer;
            _resources.RegisterFramebuffer(framebuffer, attachments, $"Swapchain.Framebuffer[{index}]");
        }
    }

    private Framebuffer CreateSwapchainFramebuffer(ReadOnlySpan<ImageView> attachments)
    {
        fixed (ImageView* attachmentsPtr = attachments)
        {
            FramebufferCreateInfo createInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _resources.SwapchainRenderPass,
                AttachmentCount = (uint)attachments.Length,
                PAttachments = attachmentsPtr,
                Width = _output.Desktop.Extent.Width,
                Height = _output.Desktop.Extent.Height,
                Layers = 1,
            };
            if (_api.CreateFramebuffer(
                    _device.Device,
                    ref createInfo,
                    null,
                    out Framebuffer framebuffer) != Result.Success)
            {
                throw new InvalidOperationException("Failed to create a swapchain framebuffer.");
            }

            return framebuffer;
        }
    }

    internal Framebuffer[] DetachFramebuffersForRetirement()
    {
        Framebuffer[] framebuffers = _output.Desktop.Framebuffers ?? [];
        _output.Desktop.Framebuffers = null;
        return framebuffers;
    }

    /// <summary>
    /// Releases the active framebuffer array to the resource retirement queue.
    /// The native framebuffer handles remain valid until their captured ticket
    /// proves that no recorded desktop command can reference them.
    /// </summary>
    internal Framebuffer[] RetireLiveFramebuffers()
    {
        Framebuffer[] framebuffers = DetachFramebuffersForRetirement();
        RetireFramebuffers(framebuffers, "Swapchain.Framebuffers");
        return framebuffers;
    }

    internal void RetireFramebuffers(Framebuffer[] framebuffers, string owner)
    {
        for (int index = 0; index < framebuffers.Length; index++)
            _resources.RetireFramebuffer(framebuffers[index], owner);
    }

    private RenderPass CreateRenderPass(AttachmentLoadOp colorLoadOp)
    {
        VulkanSwapchainDepthResources depth = _output.DesktopDepthResources
            ?? throw new InvalidOperationException("Swapchain depth resources must exist before creating render passes.");
        AttachmentDescription colorAttachment = new()
        {
            Format = _output.Desktop.ImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = colorLoadOp,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            InitialLayout = colorLoadOp == AttachmentLoadOp.Load ? ImageLayout.PresentSrcKhr : ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };
        AttachmentDescription depthAttachment = new()
        {
            Format = depth.Format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.Clear,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = colorLoadOp == AttachmentLoadOp.Load ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        AttachmentReference colorReference = new() { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        AttachmentReference depthReference = new() { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorReference,
            PDepthStencilAttachment = &depthReference,
        };
        AttachmentDescription* attachments = stackalloc AttachmentDescription[2];
        attachments[0] = colorAttachment;
        attachments[1] = depthAttachment;
        PipelineStageFlags stages = PipelineStageFlags.ColorAttachmentOutputBit |
                                    PipelineStageFlags.EarlyFragmentTestsBit |
                                    PipelineStageFlags.LateFragmentTestsBit;
        AccessFlags access = AccessFlags.ColorAttachmentReadBit |
                             AccessFlags.ColorAttachmentWriteBit |
                             AccessFlags.DepthStencilAttachmentReadBit |
                             AccessFlags.DepthStencilAttachmentWriteBit;
        SubpassDependency* dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.AllCommandsBit, DstStageMask = stages,
            SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            DstAccessMask = access, DependencyFlags = DependencyFlags.ByRegionBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
            SrcStageMask = stages, DstStageMask = PipelineStageFlags.AllCommandsBit,
            SrcAccessMask = access,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        RenderPassCreateInfo createInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2, PAttachments = attachments,
            SubpassCount = 1, PSubpasses = &subpass,
            DependencyCount = 2, PDependencies = dependencies,
        };
        if (_api.CreateRenderPass(_device.Device, ref createInfo, null, out RenderPass renderPass) != Result.Success)
            throw new InvalidOperationException("Failed to create a swapchain render pass.");
        return renderPass;
    }

    private void DestroyRenderPassImmediate(RenderPass renderPass)
    {
        if (renderPass.Handle == 0)
            return;
        _resources.UnregisterRenderPass(renderPass);
        _api.DestroyRenderPass(_device.Device, renderPass, null);
        _resources.CompleteDetachedExternalResourceDestruction(
            ObjectType.RenderPass,
            renderPass.Handle,
            _resources.GetPublishedGeneration(ObjectType.RenderPass, renderPass.Handle),
            forced: false);
    }

    private string BuildRenderPassSignature(AttachmentLoadOp colorLoadOp)
        => string.Join(
            "|",
            "RenderPass:Swapchain",
            $"color={_output.Desktop.ImageFormat}",
            $"depth={_output.DesktopDepthFormat}",
            "samples=Count1Bit",
            $"colorLoad={colorLoadOp}",
            "colorStore=Store",
            "depthLoad=Clear",
            "depthStore=DontCare",
            "final=PresentSrcKhr");
}
