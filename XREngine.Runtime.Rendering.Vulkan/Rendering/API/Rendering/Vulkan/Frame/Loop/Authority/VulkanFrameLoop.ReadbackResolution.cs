using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Resolves readback sources from the currently published frame generation.</summary>
internal sealed partial class VulkanFrameLoop
{
    internal bool TryResolveBlitImage(
        XRFrameBuffer? frameBuffer,
        uint swapchainImageIndex,
        EReadBufferMode readBufferMode,
        bool wantColor,
        bool wantDepth,
        bool wantStencil,
        out BlitImageInfo info,
        bool isSource)
        => _commandRuntime.TryResolveLegacyBlitImage(
            frameBuffer,
            swapchainImageIndex,
            readBufferMode,
            wantColor,
            wantDepth,
            wantStencil,
            isSource,
            PublishedResourcePlannerRuntimeState.ResourceAllocator,
            default,
            OutputRuntime.Desktop.Images,
            OutputRuntime.Desktop.ImageFormat,
            OutputRuntime.Desktop.Extent,
            OutputRuntime.DesktopDepthResources,
            out info);

    internal bool TryResolveTextureBlitImage(
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
            PublishedResourcePlannerRuntimeState.ResourceAllocator,
            out info);

    internal bool TryResolveLiveBlitImage(in BlitImageInfo info, out BlitImageInfo resolved)
        => _commandRuntime.TryResolveLiveBlitImage(info, out resolved);

    internal bool TryResolveAttachmentImage(
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
            PublishedResourcePlannerRuntimeState.ResourceAllocator,
            out info);

    internal BlitImageInfo ResolveSwapchainBlitImage(
        uint swapchainImageIndex,
        bool wantColor,
        bool wantDepth,
        bool wantStencil)
        => _commandRuntime.ResolveLegacySwapchainBlitImage(
            swapchainImageIndex,
            wantColor,
            wantDepth,
            wantStencil,
            default,
            OutputRuntime.Desktop.Images,
            OutputRuntime.Desktop.ImageFormat,
            OutputRuntime.Desktop.Extent,
            OutputRuntime.DesktopDepthResources);

    private XRFrameBuffer? _lastWindowPresentFrameBuffer
        => _outputRuntime.PresentationSource.FrameBuffer;
    private XRTexture? _lastWindowPresentColorTexture
        => _outputRuntime.PresentationSource.ColorTexture;
    private string? DeviceLostReason
        => _deviceContext.DeviceFaultFacility.DeviceLostReason;
    private CommandPool GetThreadCommandPool()
        => _commandRuntime.GetThreadCommandPool();
    private void TransitionForBlit(CommandBuffer commandBuffer, BlitImageInfo info, ImageLayout oldLayout, ImageLayout newLayout, AccessFlags srcAccess, AccessFlags dstAccess, PipelineStageFlags srcStage, PipelineStageFlags dstStage)
        => _commandRuntime.TransitionPreparedImageForBlit(commandBuffer, info, oldLayout, newLayout, srcAccess, dstAccess, srcStage, dstStage);
    private static bool IsDepthOrStencilFormat(Format format)
        => VulkanCommandRuntime.IsDepthOrStencilFormat(format);

    private T? GenericToAPI<T>(GenericRenderObject? renderObject)
        where T : VkObjectBase
        => renderObject is null ? null : _resourceRuntime.BackendObjects.Get(renderObject) as T;
}
