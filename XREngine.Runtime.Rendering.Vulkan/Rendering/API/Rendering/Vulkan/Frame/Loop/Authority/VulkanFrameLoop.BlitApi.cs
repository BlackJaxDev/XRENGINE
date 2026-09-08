using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Public/API producer and frame-state translation boundary for Vulkan blits.
/// Native resource resolution and transitions are owned by the command runtime.
/// </summary>
internal sealed partial class VulkanFrameLoop
{
    internal FrameBufferBlitSubmission TryBlit(
        XRFrameBuffer source,
        XRFrameBuffer destination,
        EReadBufferMode readBufferMode,
        bool copyColor,
        bool copyDepth,
        bool copyStencil,
        bool linearFilter)
    {
        if (!copyColor && !copyDepth && !copyStencil)
            return FrameBufferBlitSubmission.Rejected("A Vulkan blit must copy at least one aspect.");
        if (source.Width == 0 || source.Height == 0 || destination.Width == 0 || destination.Height == 0)
            return FrameBufferBlitSubmission.Rejected("A Vulkan blit cannot use an empty extent.");
        if (!TryValidateTransferUsage(source, destination, copyColor, copyDepth, copyStencil, out string? reason))
            return FrameBufferBlitSubmission.Rejected(reason!);

        Blit(
            source,
            destination,
            0,
            0,
            source.Width,
            source.Height,
            0,
            0,
            destination.Width,
            destination.Height,
            readBufferMode,
            copyColor,
            copyDepth,
            copyStencil,
            linearFilter,
            requireExactCompatibility: true);
        return FrameBufferBlitSubmission.Enqueued();
    }

    private bool TryValidateTransferUsage(
        XRFrameBuffer source,
        XRFrameBuffer destination,
        bool copyColor,
        bool copyDepth,
        bool copyStencil,
        out string? reason)
    {
        EFrameBufferAttachment sourceAttachment = copyColor
            ? EFrameBufferAttachment.ColorAttachment0
            : copyDepth || copyStencil
                ? EFrameBufferAttachment.DepthStencilAttachment
                : EFrameBufferAttachment.None;
        EFrameBufferAttachment destinationAttachment = sourceAttachment;
        if (!TryGetAttachment(source, sourceAttachment, out IFrameBufferAttachement sourceTarget) ||
            !TryGetAttachment(destination, destinationAttachment, out IFrameBufferAttachement destinationTarget))
        {
            reason = "The Vulkan blit source or destination does not expose the requested attachment.";
            return false;
        }

        if (!TryGetImageUsage(sourceTarget, out ImageUsageFlags sourceUsage) ||
            !TryGetImageUsage(destinationTarget, out ImageUsageFlags destinationUsage))
        {
            reason = "The Vulkan blit source or destination has no published image descriptor; transfer usage cannot be admitted.";
            return false;
        }
        if ((sourceUsage & ImageUsageFlags.TransferSrcBit) == 0 ||
            (destinationUsage & ImageUsageFlags.TransferDstBit) == 0)
        {
            reason = $"The Vulkan blit requires TransferSrc/TransferDst image usage; source={sourceUsage}, destination={destinationUsage}.";
            return false;
        }

        reason = null;
        return true;
    }

    private bool TryGetImageUsage(IFrameBufferAttachement attachment, out ImageUsageFlags usage)
    {
        if (attachment is not GenericRenderObject resource)
        {
            usage = default;
            return false;
        }

        switch (ResourceRuntime.BackendObjects.Get(resource))
        {
            case IVkImageDescriptorSource source when source.IsDescriptorReady && source.DescriptorImage.Handle != 0:
                usage = source.DescriptorUsage;
                return true;
            case VkRenderBuffer renderBuffer when renderBuffer.Image.Handle != 0:
                usage = renderBuffer.PhysicalGroup?.Usage ?? ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
                return true;
            default:
                usage = default;
                return false;
        }
    }

    private static bool TryGetAttachment(
        XRFrameBuffer frameBuffer,
        EFrameBufferAttachment requested,
        out IFrameBufferAttachement target)
    {
        if (frameBuffer.Targets is { } targets)
        {
            foreach (var (candidate, attachment, _, _) in targets)
            {
                if (attachment == requested ||
                    requested == EFrameBufferAttachment.DepthStencilAttachment && attachment == EFrameBufferAttachment.DepthAttachment)
                {
                    target = candidate;
                    return true;
                }
            }
        }

        target = null!;
        return false;
    }

    internal void Blit(
        XRFrameBuffer? inFBO,
        XRFrameBuffer? outFBO,
        int inX,
        int inY,
        uint inW,
        uint inH,
        int outX,
        int outY,
        uint outW,
        uint outH,
        EReadBufferMode readBufferMode,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter,
        bool requireExactCompatibility = false)
    {
        FrameOpContext context = CaptureFrameOpContextForCurrentPipelineScope();
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        BlitOp? operation = VulkanBlitProducer.Prepare(
            inFBO,
            outFBO,
            inX,
            inY,
            inW,
            inH,
            outX,
            outY,
            outW,
            outH,
            readBufferMode,
            colorBit,
            depthBit,
            stencilBit,
            linearFilter,
            requireExactCompatibility,
            VulkanCommandRuntime.EnsureValidPassIndex(passIndex, "Blit", context.PassMetadata),
            context);
        if (operation is not null)
            EnqueueFrameOp(operation);
    }

    internal void BlitWithDrawBuffer(
        XRFrameBuffer? inFBO,
        XRFrameBuffer? outFBO,
        uint inW,
        uint inH,
        uint outW,
        uint outH,
        EReadBufferMode readBufferMode,
        EReadBufferMode drawBufferMode,
        bool colorBit,
        bool depthBit,
        bool stencilBit,
        bool linearFilter)
        => Blit(
            inFBO,
            outFBO,
            0,
            0,
            inW,
            inH,
            0,
            0,
            outW,
            outH,
            readBufferMode,
            colorBit,
            depthBit,
            stencilBit,
            linearFilter,
            requireExactCompatibility: false);
}
