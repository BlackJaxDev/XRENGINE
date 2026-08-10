using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns legacy framebuffer render-pass cache entries and framebuffer lifetime
/// registration for a single Vulkan resource generation.
/// </summary>
internal unsafe sealed class VulkanFrameBufferResourceService(VulkanResourceRuntime resources)
{
    internal RenderPass GetOrCreateRenderPass(
        Vk api,
        Device device,
        FrameBufferAttachmentSignature[] signature)
    {
        FrameBufferAttachmentSignature[] keyData = (FrameBufferAttachmentSignature[])signature.Clone();
        VulkanFrameBufferRenderPassKey key = new(keyData);
        if (resources.FrameBufferRenderPasses.TryGetValue(key, out RenderPass renderPass))
            return renderPass;

        renderPass = CreateRenderPass(api, device, signature);
        resources.FrameBufferRenderPasses.Add(key, renderPass);
        return renderPass;
    }

    internal void RegisterFramebuffer(Framebuffer framebuffer, ReadOnlySpan<ImageView> attachments, string owner)
        => resources.RegisterFramebuffer(framebuffer, attachments, owner);

    internal void RetireFramebuffer(Framebuffer framebuffer, string owner)
        => resources.RetireFramebuffer(framebuffer, owner);

    /// <summary>
    /// Destroys the cache only after the owning runtime has made the device
    /// idle.  Individual framebuffers retire independently through the
    /// lifetime authority.
    /// </summary>
    internal void DestroyRenderPasses(Vk api, Device device)
    {
        foreach (RenderPass renderPass in resources.FrameBufferRenderPasses.Values)
        {
            if (renderPass.Handle == 0)
                continue;

            resources.UnregisterRenderPass(renderPass);
            api.DestroyRenderPass(device, renderPass, null);
        }

        resources.FrameBufferRenderPasses.Clear();
    }

    private RenderPass CreateRenderPass(Vk api, Device device, FrameBufferAttachmentSignature[] signature)
    {
        AttachmentDescription[] descriptions = new AttachmentDescription[signature.Length];
        int colorCount = 0;
        int resolveCount = 0;
        for (int index = 0; index < signature.Length; index++)
        {
            descriptions[index] = signature[index].ToAttachmentDescription();
            if (signature[index].Role == AttachmentRole.Color)
                colorCount++;
            else if (signature[index].Role == AttachmentRole.Resolve)
                resolveCount++;
        }

        AttachmentReference[] colorRefs = colorCount == 0 ? [] : new AttachmentReference[colorCount];
        Format[] colorFormats = colorCount == 0 ? [] : new Format[colorCount];
        AttachmentReference[] resolveRefs = resolveCount == 0 || colorCount == 0 ? [] : new AttachmentReference[colorCount];
        for (int index = 0; index < resolveRefs.Length; index++)
            resolveRefs[index] = new AttachmentReference { Attachment = uint.MaxValue, Layout = ImageLayout.ColorAttachmentOptimal };

        AttachmentReference depthRef = default;
        bool depthAssigned = false;
        int colorIndex = 0;
        for (int index = 0; index < signature.Length; index++)
        {
            FrameBufferAttachmentSignature attachment = signature[index];
            if (attachment.Role == AttachmentRole.Color)
            {
                colorFormats[colorIndex] = attachment.Format;
                colorRefs[colorIndex++] = attachment.ToAttachmentReference((uint)index);
            }
            else if (attachment.Role is AttachmentRole.Depth or AttachmentRole.DepthStencil or AttachmentRole.Stencil && !depthAssigned)
            {
                depthRef = attachment.ToAttachmentReference((uint)index);
                depthAssigned = true;
            }
        }

        for (int index = 0; index < signature.Length && resolveRefs.Length > 0; index++)
        {
            FrameBufferAttachmentSignature attachment = signature[index];
            if (attachment.Role != AttachmentRole.Resolve)
                continue;
            int colorReferenceIndex = Array.FindIndex(colorRefs, reference =>
                reference.Attachment < signature.Length && signature[(int)reference.Attachment].ColorIndex == attachment.ColorIndex);
            if (colorReferenceIndex < 0)
                throw new InvalidOperationException($"Framebuffer render pass has a resolve attachment for color {attachment.ColorIndex}, but no matching color attachment.");
            resolveRefs[colorReferenceIndex] = attachment.ToAttachmentReference((uint)index);
        }

        fixed (AttachmentDescription* descriptionsPtr = descriptions)
        fixed (AttachmentReference* colorRefsPtr = colorRefs)
        fixed (AttachmentReference* resolveRefsPtr = resolveRefs)
        {
            AttachmentReference depthCopy = depthRef;
            SubpassDescription subpass = new()
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)colorRefs.Length,
                PColorAttachments = colorRefs.Length == 0 ? null : colorRefsPtr,
                PResolveAttachments = resolveRefs.Length == 0 ? null : resolveRefsPtr,
                PDepthStencilAttachment = depthAssigned ? &depthCopy : null,
            };
            PipelineStageFlags attachmentStages = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
            AccessFlags attachmentAccess = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
            SubpassDependency* dependencies = stackalloc SubpassDependency[2];
            dependencies[0] = new SubpassDependency { SrcSubpass = Vk.SubpassExternal, DstSubpass = 0, SrcStageMask = PipelineStageFlags.AllCommandsBit, DstStageMask = attachmentStages, SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, DstAccessMask = attachmentAccess, DependencyFlags = DependencyFlags.ByRegionBit };
            dependencies[1] = new SubpassDependency { SrcSubpass = 0, DstSubpass = Vk.SubpassExternal, SrcStageMask = attachmentStages, DstStageMask = PipelineStageFlags.AllCommandsBit, SrcAccessMask = attachmentAccess, DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, DependencyFlags = DependencyFlags.ByRegionBit };
            RenderPassCreateInfo createInfo = new() { SType = StructureType.RenderPassCreateInfo, AttachmentCount = (uint)descriptions.Length, PAttachments = descriptionsPtr, SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 2, PDependencies = dependencies };
            if (api.CreateRenderPass(device, ref createInfo, null, out RenderPass renderPass) != Result.Success)
                throw new InvalidOperationException("Failed to create framebuffer render pass.");
            resources.RegisterRenderPass(renderPass, colorFormats, BuildSignature(signature));
            return renderPass;
        }
    }

    private static string BuildSignature(ReadOnlySpan<FrameBufferAttachmentSignature> signature)
    {
        if (signature.IsEmpty)
            return "RenderPass:FrameBuffer:<empty>";
        string[] attachments = new string[signature.Length];
        for (int index = 0; index < signature.Length; index++)
        {
            FrameBufferAttachmentSignature attachment = signature[index];
            attachments[index] = $"{attachment.Role},fmt={attachment.Format},samples={attachment.Samples},aspect={attachment.AspectMask},color={attachment.ColorIndex},load={attachment.LoadOp},store={attachment.StoreOp},stencilLoad={attachment.StencilLoadOp},stencilStore={attachment.StencilStoreOp},initial={attachment.InitialLayout},final={attachment.FinalLayout},ref={attachment.ReferenceLayout}";
        }
        return $"RenderPass:FrameBuffer:{string.Join("|", attachments)}";
    }
}
