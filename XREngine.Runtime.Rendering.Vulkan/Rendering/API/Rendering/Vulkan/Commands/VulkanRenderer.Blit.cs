using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Public/API producer and frame-state translation boundary for Vulkan blits.
/// Native resource resolution and transitions are owned by the command runtime.
/// </summary>
public unsafe partial class VulkanRenderer
{
    public override void Blit(
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
        bool linearFilter)
    {
        FrameOpContext context = CaptureFrameOpContext();
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
            EnsureValidPassIndex(passIndex, "Blit", context.PassMetadata),
            context);
        if (operation is not null)
            EnqueueFrameOp(operation);
    }

    public override void BlitWithDrawBuffer(
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
            linearFilter);

    private bool TryResolveBlitImage(
        XRFrameBuffer? frameBuffer,
        uint swapchainImageIndex,
        EReadBufferMode readBufferMode,
        bool wantColor,
        bool wantDepth,
        bool wantStencil,
        out BlitImageInfo info,
        bool isSource)
        => TryResolveBlitImage(
            frameBuffer,
            swapchainImageIndex,
            readBufferMode,
            wantColor,
            wantDepth,
            wantStencil,
            out info,
            isSource,
            default);

    private bool TryResolveBlitImage(
        XRFrameBuffer? frameBuffer,
        uint swapchainImageIndex,
        EReadBufferMode readBufferMode,
        bool wantColor,
        bool wantDepth,
        bool wantStencil,
        out BlitImageInfo info,
        bool isSource,
        in SwapchainRecordingTarget swapchainTarget)
        => _commandRuntime.TryResolveLegacyBlitImage(
            frameBuffer,
            swapchainImageIndex,
            readBufferMode,
            wantColor,
            wantDepth,
            wantStencil,
            isSource,
            ResourceAllocator,
            in swapchainTarget,
            OutputRuntime.Desktop.Images,
            OutputRuntime.Desktop.ImageFormat,
            OutputRuntime.Desktop.Extent,
            OutputRuntime.DesktopDepthResources,
            out info);

    private bool TryResolveAttachmentImage(
        IFrameBufferAttachement attachment,
        int mipLevel,
        int layerIndex,
        ImageAspectFlags aspectMask,
        out BlitImageInfo info)
        => _commandRuntime.TryResolveLegacyAttachmentImage(
            attachment,
            mipLevel,
            layerIndex,
            aspectMask,
            ResourceAllocator,
            out info);

    private bool TryResolveTextureBlitImage(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
        ImageAspectFlags aspectMask,
        ImageLayout layout,
        PipelineStageFlags stage,
        AccessFlags access,
        out BlitImageInfo info)
        => _commandRuntime.TryResolveLegacyTextureBlitImage(
            texture,
            mipLevel,
            layerIndex,
            aspectMask,
            layout,
            stage,
            access,
            ResourceAllocator,
            out info);

    private bool TryResolveLiveBlitImage(
        in BlitImageInfo info,
        out BlitImageInfo resolved)
        => _commandRuntime.TryResolveLiveBlitImage(info, out resolved);

    private BlitImageInfo ResolveSwapchainBlitImage(
        uint swapchainImageIndex,
        bool wantColor,
        bool wantDepth,
        bool wantStencil)
        => ResolveSwapchainBlitImage(
            swapchainImageIndex,
            wantColor,
            wantDepth,
            wantStencil,
            default);

    private BlitImageInfo ResolveSwapchainBlitImage(
        uint swapchainImageIndex,
        bool wantColor,
        bool wantDepth,
        bool wantStencil,
        in SwapchainRecordingTarget recordingTarget)
        => _commandRuntime.ResolveLegacySwapchainBlitImage(
            swapchainImageIndex,
            wantColor,
            wantDepth,
            wantStencil,
            in recordingTarget,
            OutputRuntime.Desktop.Images,
            OutputRuntime.Desktop.ImageFormat,
            OutputRuntime.Desktop.Extent,
            OutputRuntime.DesktopDepthResources);

    internal static (uint BaseArrayLayer, uint LayerCount) ResolveDescriptorTextureBlitLayerRange(
        XRTexture texture,
        int layerIndex,
        uint descriptorArrayLayers)
        => VulkanCommandRuntime.ResolveDescriptorTextureBlitLayerRange(
            texture,
            layerIndex,
            descriptorArrayLayers);

    private static bool IsDepthOrStencilFormat(Format format)
        => VulkanCommandRuntime.IsDepthOrStencilFormat(format);

    private static ImageAspectFlags NormalizeBarrierAspectMask(
        Format format,
        ImageAspectFlags aspectMask)
        => VulkanCommandRuntime.NormalizeBarrierAspectMask(format, aspectMask);

    private void TransitionForBlit(
        CommandBuffer commandBuffer,
        BlitImageInfo info,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage)
        => _commandRuntime.TransitionPreparedImageForBlit(
            commandBuffer,
            info,
            oldLayout,
            newLayout,
            srcAccess,
            dstAccess,
            srcStage,
            dstStage);
}
