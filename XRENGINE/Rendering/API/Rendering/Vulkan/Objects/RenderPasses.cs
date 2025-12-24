using System;
using System.Linq;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private RenderPass _renderPass;

    private void DestroyRenderPasses()
        => Api!.DestroyRenderPass(device, _renderPass, null);

    private void CreateRenderPass()
    {
        var (colorLoadOp, colorStoreOp) = ResolveSwapchainColorAttachmentOps();

        AttachmentDescription colorAttachment = new()
        {
            Format = swapChainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = colorLoadOp,
            StoreOp = colorStoreOp,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };

        AttachmentReference colorAttachmentRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };

        if (Api!.CreateRenderPass(device, ref renderPassInfo, null, out _renderPass) != Result.Success)
            throw new Exception("Failed to create render pass.");
    }

    private (AttachmentLoadOp load, AttachmentStoreOp store) ResolveSwapchainColorAttachmentOps()
    {
        RenderPassResourceUsage? usage = TryGetOutputColorUsage();
        if (usage is null)
            return (AttachmentLoadOp.Clear, AttachmentStoreOp.Store);

        return (ToVulkanLoadOp(usage.LoadOp), ToVulkanStoreOp(usage.StoreOp));
    }

    private static AttachmentLoadOp ToVulkanLoadOp(RenderPassLoadOp op)
        => op switch
        {
            RenderPassLoadOp.Clear => AttachmentLoadOp.Clear,
            RenderPassLoadOp.DontCare => AttachmentLoadOp.DontCare,
            _ => AttachmentLoadOp.Load
        };

    private static AttachmentStoreOp ToVulkanStoreOp(RenderPassStoreOp op)
        => op switch
        {
            RenderPassStoreOp.DontCare => AttachmentStoreOp.DontCare,
            _ => AttachmentStoreOp.Store
        };

    private RenderPassResourceUsage? TryGetOutputColorUsage()
    {
        var pipeline = Engine.Rendering.State.CurrentRenderingPipeline;
        var passes = pipeline?.Pipeline?.PassMetadata;
        if (passes is null)
            return null;

        return passes
            .SelectMany(p => p.ResourceUsages)
            .Where(u => u.ResourceType == RenderPassResourceType.ColorAttachment)
            .Where(u => string.Equals(u.ResourceName, RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();
    }
}