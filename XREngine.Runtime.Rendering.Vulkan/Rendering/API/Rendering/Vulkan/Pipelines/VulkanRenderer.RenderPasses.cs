using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private RenderPass _renderPass;
    /// <summary>
    /// A second swapchain render pass that uses <see cref="AttachmentLoadOp.Load"/>
    /// for the color attachment. Used when re-entering the swapchain render pass
    /// after a compute dispatch or blit forced it to end, so that previous
    /// contents (e.g. the composited scene) are preserved instead of cleared.
    /// </summary>
    private RenderPass _renderPassLoad;

    private (RenderPass Clear, RenderPass Load) DetachSwapchainRenderPassesForRetirement()
    {
        (RenderPass Clear, RenderPass Load) detached = (_renderPass, _renderPassLoad);
        _renderPass = default;
        _renderPassLoad = default;
        return detached;
    }

    private void DestroyRenderPasses()
    {
        if (_renderPass.Handle != 0)
        {
            UnregisterRenderPass(_renderPass);
            Api!.DestroyRenderPass(device, _renderPass, null);
        }

        if (_renderPassLoad.Handle != 0)
        {
            UnregisterRenderPass(_renderPassLoad);
            Api!.DestroyRenderPass(device, _renderPassLoad, null);
        }

        _renderPass = default;
        _renderPassLoad = default;
    }

    private void CreateRenderPass()
    {
        if (UseDynamicRenderingRenderTargets)
        {
            _renderPass = default;
            _renderPassLoad = default;
            return;
        }

        _renderPass = CreateSwapchainRenderPass(AttachmentLoadOp.Clear);
        RegisterRenderPassColorAttachmentFormats(_renderPass, [swapChainImageFormat], BuildSwapchainRenderPassSignature(AttachmentLoadOp.Clear));

        _renderPassLoad = CreateSwapchainRenderPass(AttachmentLoadOp.Load);
        RegisterRenderPassColorAttachmentFormats(_renderPassLoad, [swapChainImageFormat], BuildSwapchainRenderPassSignature(AttachmentLoadOp.Load));
    }

    private RenderPass CreateSwapchainRenderPass(AttachmentLoadOp colorLoadOp)
    {
        AttachmentStoreOp colorStoreOp = AttachmentStoreOp.Store;

        // Swapchain images are acquired in PresentSrcKhr layout. If we intend to preserve previous contents
        // (Load), we must declare that as the initial layout. If we clear/don't-care, we can start from Undefined.
        ImageLayout colorInitialLayout = colorLoadOp == AttachmentLoadOp.Load
            ? ImageLayout.PresentSrcKhr
            : ImageLayout.Undefined;

        AttachmentDescription colorAttachment = new()
        {
            Format = swapChainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = colorLoadOp,
            StoreOp = colorStoreOp,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            InitialLayout = colorInitialLayout,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        // Phase 0 correctness: always clear swapchain depth each frame (prevents stale depth causing a black scene).
        // InitialLayout is Undefined because the depth image is created/recreated with Undefined layout.
        AttachmentDescription depthAttachment = new()
        {
            Format = _swapchainDepthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.Clear,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = colorLoadOp == AttachmentLoadOp.Load
                ? ImageLayout.DepthStencilAttachmentOptimal
                : ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        AttachmentReference colorAttachmentRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        AttachmentReference depthAttachmentRef = new()
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
            PDepthStencilAttachment = &depthAttachmentRef,
        };

        AttachmentDescription* attachmentsPtr = stackalloc AttachmentDescription[2];
        attachmentsPtr[0] = colorAttachment;
        attachmentsPtr[1] = depthAttachment;

        PipelineStageFlags attachmentStages =
            PipelineStageFlags.ColorAttachmentOutputBit |
            PipelineStageFlags.EarlyFragmentTestsBit |
            PipelineStageFlags.LateFragmentTestsBit;
        AccessFlags attachmentAccess =
            AccessFlags.ColorAttachmentReadBit |
            AccessFlags.ColorAttachmentWriteBit |
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit;
        SubpassDependency* dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.AllCommandsBit,
            DstStageMask = attachmentStages,
            SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            DstAccessMask = attachmentAccess,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = attachmentStages,
            DstStageMask = PipelineStageFlags.AllCommandsBit,
            SrcAccessMask = attachmentAccess,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachmentsPtr,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };

        if (Api!.CreateRenderPass(device, ref renderPassInfo, null, out RenderPass renderPass) != Result.Success)
            throw new Exception("Failed to create render pass.");

        return renderPass;
    }

    private (AttachmentLoadOp load, AttachmentStoreOp store) ResolveSwapchainColorAttachmentOps()
    {
        // Phase 0 correctness: always clear swapchain color each frame.
        // This prevents stale contents / partial clears when the engine only clears viewport regions.
        return (AttachmentLoadOp.Clear, AttachmentStoreOp.Store);
    }

    private string BuildSwapchainRenderPassSignature(AttachmentLoadOp colorLoadOp)
        => string.Join(
            "|",
            "RenderPass:Swapchain",
            $"color={swapChainImageFormat}",
            $"depth={_swapchainDepthFormat}",
            "samples=Count1Bit",
            $"colorLoad={colorLoadOp}",
            "colorStore=Store",
            "depthLoad=Clear",
            "depthStore=DontCare",
            "final=PresentSrcKhr");
}
