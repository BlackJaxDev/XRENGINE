using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly Dictionary<RenderPassKey, RenderPass> _frameBufferRenderPasses = new();

    private RenderPass GetOrCreateFrameBufferRenderPass(FrameBufferAttachmentSignature[] signature)
    {
        FrameBufferAttachmentSignature[] keyData = (FrameBufferAttachmentSignature[])signature.Clone();
        RenderPassKey key = new(keyData);
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

        for (int i = 0; i < signature.Length; i++)
        {
            FrameBufferAttachmentSignature attachment = signature[i];
            descriptions[i] = attachment.ToAttachmentDescription();

            if (attachment.Role == AttachmentRole.Color)
                colorCount++;
        }

        AttachmentReference[] colorRefs = colorCount > 0
            ? new AttachmentReference[colorCount]
            : Array.Empty<AttachmentReference>();

        AttachmentReference depthRef = default;
        bool depthAssigned = false;
        int colorIndex = 0;

        for (int i = 0; i < signature.Length; i++)
        {
            FrameBufferAttachmentSignature attachment = signature[i];
            if (attachment.Role == AttachmentRole.Color)
            {
                colorRefs[colorIndex++] = attachment.ToAttachmentReference((uint)i);
            }
            else if (!depthAssigned)
            {
                depthRef = attachment.ToAttachmentReference((uint)i);
                depthAssigned = true;
            }
        }

        fixed (AttachmentDescription* descPtr = descriptions)
        fixed (AttachmentReference* colorPtr = colorRefs)
        {
            AttachmentReference depthCopy = depthRef;
            AttachmentReference* depthPtr = depthAssigned ? &depthCopy : null;

            SubpassDescription subpass = new()
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)colorRefs.Length,
                PColorAttachments = colorRefs.Length > 0 ? colorPtr : null,
                PDepthStencilAttachment = depthPtr,
            };

            RenderPassCreateInfo createInfo = new()
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)descriptions.Length,
                PAttachments = descPtr,
                SubpassCount = 1,
                PSubpasses = &subpass,
            };

            if (Api!.CreateRenderPass(device, ref createInfo, null, out RenderPass renderPass) != Result.Success)
                throw new Exception("Failed to create framebuffer render pass.");

            return renderPass;
        }
    }

    private void DestroyFrameBufferRenderPasses()
    {
        foreach (RenderPass renderPass in _frameBufferRenderPasses.Values)
        {
            if (renderPass.Handle != 0)
                Api!.DestroyRenderPass(device, renderPass, null);
        }

        _frameBufferRenderPasses.Clear();
    }

    private readonly record struct RenderPassKey(FrameBufferAttachmentSignature[] Attachments) : IEquatable<RenderPassKey>
    {
        public bool Equals(RenderPassKey other)
        {
            if (Attachments.Length != other.Attachments.Length)
                return false;

            for (int i = 0; i < Attachments.Length; i++)
            {
                if (!Attachments[i].Equals(other.Attachments[i]))
                    return false;
            }

            return true;
        }

        public override int GetHashCode()
        {
            HashCode hash = new();
            foreach (FrameBufferAttachmentSignature attachment in Attachments)
                hash.Add(attachment);
            return hash.ToHashCode();
        }
    }

    private readonly struct FrameBufferAttachmentSignature : IEquatable<FrameBufferAttachmentSignature>
    {
        public FrameBufferAttachmentSignature(
            Format format,
            SampleCountFlags samples,
            ImageAspectFlags aspectMask,
            AttachmentRole role,
            uint colorIndex,
            AttachmentLoadOp loadOp,
            AttachmentStoreOp storeOp,
            AttachmentLoadOp stencilLoadOp,
            AttachmentStoreOp stencilStoreOp,
            ImageLayout initialLayout,
            ImageLayout finalLayout)
        {
            Format = format;
            Samples = samples;
            AspectMask = aspectMask;
            Role = role;
            ColorIndex = colorIndex;
            LoadOp = loadOp;
            StoreOp = storeOp;
            StencilLoadOp = stencilLoadOp;
            StencilStoreOp = stencilStoreOp;
            InitialLayout = initialLayout;
            FinalLayout = finalLayout;
        }

        public Format Format { get; }
        public SampleCountFlags Samples { get; }
        public ImageAspectFlags AspectMask { get; }
        public AttachmentRole Role { get; }
        public uint ColorIndex { get; }
        public AttachmentLoadOp LoadOp { get; }
        public AttachmentStoreOp StoreOp { get; }
        public AttachmentLoadOp StencilLoadOp { get; }
        public AttachmentStoreOp StencilStoreOp { get; }
        public ImageLayout InitialLayout { get; }
        public ImageLayout FinalLayout { get; }

        public AttachmentDescription ToAttachmentDescription()
            => new()
            {
                Format = Format,
                Samples = Samples,
                LoadOp = LoadOp,
                StoreOp = StoreOp,
                StencilLoadOp = StencilLoadOp,
                StencilStoreOp = StencilStoreOp,
                InitialLayout = InitialLayout,
                FinalLayout = FinalLayout,
            };

        public AttachmentReference ToAttachmentReference(uint attachmentIndex)
        {
            ImageLayout layout = Role == AttachmentRole.Color
                ? ImageLayout.ColorAttachmentOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;

            return new AttachmentReference
            {
                Attachment = attachmentIndex,
                Layout = layout,
            };
        }

        public bool Equals(FrameBufferAttachmentSignature other)
        {
            return Format == other.Format &&
                   Samples == other.Samples &&
                   AspectMask == other.AspectMask &&
                   Role == other.Role &&
                   ColorIndex == other.ColorIndex &&
                   LoadOp == other.LoadOp &&
                   StoreOp == other.StoreOp &&
                   StencilLoadOp == other.StencilLoadOp &&
                   StencilStoreOp == other.StencilStoreOp &&
                   InitialLayout == other.InitialLayout &&
                   FinalLayout == other.FinalLayout;
        }

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add((int)Format);
            hash.Add((int)Samples);
            hash.Add((int)AspectMask);
            hash.Add((int)Role);
            hash.Add(ColorIndex);
            hash.Add((int)LoadOp);
            hash.Add((int)StoreOp);
            hash.Add((int)StencilLoadOp);
            hash.Add((int)StencilStoreOp);
            hash.Add((int)InitialLayout);
            hash.Add((int)FinalLayout);
            return hash.ToHashCode();
        }
    }

    private enum AttachmentRole
    {
        Color,
        Depth,
        DepthStencil,
        Stencil,
    }
}
