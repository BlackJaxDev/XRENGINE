using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private Dictionary<VulkanFrameBufferRenderPassKey, RenderPass> _frameBufferRenderPasses
        => ResourceRuntime.FrameBufferRenderPasses;

    internal RenderPass GetOrCreateFrameBufferRenderPass(FrameBufferAttachmentSignature[] signature)
    {
        FrameBufferAttachmentSignature[] keyData = (FrameBufferAttachmentSignature[])signature.Clone();
        VulkanFrameBufferRenderPassKey key = new(keyData);
        if (!_frameBufferRenderPasses.TryGetValue(key, out RenderPass renderPass))
        {
            renderPass = CreateFrameBufferRenderPass(signature);
            _frameBufferRenderPasses.Add(key, renderPass);
        }

        return renderPass;
    }

    private RenderPass CreateFrameBufferRenderPass(FrameBufferAttachmentSignature[] signature)
    {
        AttachmentDescription[] descriptions = new AttachmentDescription[signature.Length];
        int colorCount = 0;
        int resolveCount = 0;

        for (int i = 0; i < signature.Length; i++)
        {
            FrameBufferAttachmentSignature attachment = signature[i];
            descriptions[i] = attachment.ToAttachmentDescription();

            if (attachment.Role == AttachmentRole.Color)
                colorCount++;
            else if (attachment.Role == AttachmentRole.Resolve)
                resolveCount++;
        }

        AttachmentReference[] colorRefs = colorCount > 0
            ? new AttachmentReference[colorCount]
            : Array.Empty<AttachmentReference>();
        Format[] colorFormats = colorCount > 0
            ? new Format[colorCount]
            : Array.Empty<Format>();

        AttachmentReference[] resolveRefs = resolveCount > 0 && colorCount > 0
            ? new AttachmentReference[colorCount]
            : Array.Empty<AttachmentReference>();
        for (int i = 0; i < resolveRefs.Length; i++)
        {
            resolveRefs[i] = new AttachmentReference
            {
                Attachment = uint.MaxValue,
                Layout = ImageLayout.ColorAttachmentOptimal
            };
        }

        AttachmentReference depthRef = default;
        bool depthAssigned = false;
        int colorIndex = 0;

        for (int i = 0; i < signature.Length; i++)
        {
            FrameBufferAttachmentSignature attachment = signature[i];
            if (attachment.Role == AttachmentRole.Color)
            {
                colorFormats[colorIndex] = attachment.Format;
                colorRefs[colorIndex++] = attachment.ToAttachmentReference((uint)i);
            }
            else if ((attachment.Role is AttachmentRole.Depth or AttachmentRole.DepthStencil or AttachmentRole.Stencil) && !depthAssigned)
            {
                depthRef = attachment.ToAttachmentReference((uint)i);
                depthAssigned = true;
            }
        }

        if (resolveRefs.Length > 0)
        {
            for (int i = 0; i < signature.Length; i++)
            {
                FrameBufferAttachmentSignature attachment = signature[i];
                if (attachment.Role != AttachmentRole.Resolve)
                    continue;

                int subpassColorIndex = -1;
                for (int colorRefIndex = 0; colorRefIndex < colorRefs.Length; colorRefIndex++)
                {
                    uint attachmentIndex = colorRefs[colorRefIndex].Attachment;
                    if (attachmentIndex < signature.Length &&
                        signature[(int)attachmentIndex].ColorIndex == attachment.ColorIndex)
                    {
                        subpassColorIndex = colorRefIndex;
                        break;
                    }
                }

                if (subpassColorIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Framebuffer render pass has a resolve attachment for color {attachment.ColorIndex}, but no matching color attachment.");
                }

                resolveRefs[subpassColorIndex] = attachment.ToAttachmentReference((uint)i);
            }
        }

        fixed (AttachmentDescription* descPtr = descriptions)
        fixed (AttachmentReference* colorPtr = colorRefs)
        fixed (AttachmentReference* resolvePtr = resolveRefs)
        {
            AttachmentReference depthCopy = depthRef;
            AttachmentReference* depthPtr = depthAssigned ? &depthCopy : null;

            SubpassDescription subpass = new()
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)colorRefs.Length,
                PColorAttachments = colorRefs.Length > 0 ? colorPtr : null,
                PResolveAttachments = resolveRefs.Length > 0 ? resolvePtr : null,
                PDepthStencilAttachment = depthPtr,
            };

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

            RenderPassCreateInfo createInfo = new()
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)descriptions.Length,
                PAttachments = descPtr,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 2,
                PDependencies = dependencies,
            };

            if (Api!.CreateRenderPass(_deviceContext.Device, ref createInfo, null, out RenderPass renderPass) != Result.Success)
                throw new Exception("Failed to create framebuffer render pass.");

            RegisterRenderPassColorAttachmentFormats(
                renderPass,
                colorFormats,
                BuildFrameBufferRenderPassSignature(signature));

            return renderPass;
        }
    }

    private static string BuildFrameBufferRenderPassSignature(FrameBufferAttachmentSignature[] signature)
    {
        if (signature.Length == 0)
            return "RenderPass:FrameBuffer:<empty>";

        string[] attachments = new string[signature.Length];
        for (int i = 0; i < signature.Length; i++)
        {
            FrameBufferAttachmentSignature attachment = signature[i];
            attachments[i] = string.Join(
                ",",
                attachment.Role,
                $"fmt={attachment.Format}",
                $"samples={attachment.Samples}",
                $"aspect={attachment.AspectMask}",
                $"color={attachment.ColorIndex}",
                $"load={attachment.LoadOp}",
                $"store={attachment.StoreOp}",
                $"stencilLoad={attachment.StencilLoadOp}",
                $"stencilStore={attachment.StencilStoreOp}",
                $"initial={attachment.InitialLayout}",
                $"final={attachment.FinalLayout}",
                $"ref={attachment.ReferenceLayout}");
        }

        return $"RenderPass:FrameBuffer:{string.Join("|", attachments)}";
    }

    private void DestroyFrameBufferRenderPasses()
    {
        foreach (RenderPass renderPass in _frameBufferRenderPasses.Values)
        {
            if (renderPass.Handle != 0)
            {
                UnregisterRenderPass(renderPass);
                Api!.DestroyRenderPass(_deviceContext.Device, renderPass, null);
            }
        }

        _frameBufferRenderPasses.Clear();
    }

    private static bool IsColorLikeAttachmentRole(AttachmentRole role)
        => role is AttachmentRole.Color or AttachmentRole.Resolve;
}
