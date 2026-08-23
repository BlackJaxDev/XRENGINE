using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Resolves legacy renderer-facing framebuffer and texture inputs into frozen
/// native blit descriptions without retaining renderer or output authority.
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    internal bool TryResolveLegacyBlitImage(
        XRFrameBuffer? frameBuffer,
        uint swapchainImageIndex,
        EReadBufferMode readBufferMode,
        bool wantColor,
        bool wantDepth,
        bool wantStencil,
        bool isSource,
        VulkanResourceAllocator resourceAllocator,
        in SwapchainRecordingTarget recordingTarget,
        Image[]? desktopImages,
        Format desktopFormat,
        Extent2D desktopExtent,
        VulkanSwapchainDepthResources? desktopDepth,
        out BlitImageInfo info)
    {
        if (frameBuffer is null)
        {
            info = ResolveLegacySwapchainBlitImage(
                swapchainImageIndex,
                wantColor,
                wantDepth,
                wantStencil,
                in recordingTarget,
                desktopImages,
                desktopFormat,
                desktopExtent,
                desktopDepth);
            return info.IsValid;
        }

        if (frameBuffer.Targets is not { } targets)
        {
            info = default;
            return false;
        }

        int desiredColorIndex = isSource
            ? ResolveLegacyReadBufferColorAttachmentIndex(readBufferMode)
            : 0;
        EFrameBufferAttachment desiredColorAttachment =
            (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + desiredColorIndex);

        foreach ((IFrameBufferAttachement target, EFrameBufferAttachment attachment, int mipLevel, int layerIndex) in targets)
        {
            ImageAspectFlags aspect = ResolveRequestedBlitAspect(
                attachment,
                desiredColorAttachment,
                wantColor,
                wantDepth,
                wantStencil);
            if (aspect == ImageAspectFlags.None)
                continue;

            if (TryResolveLegacyAttachmentImage(
                    target,
                    mipLevel,
                    layerIndex,
                    aspect,
                    resourceAllocator,
                    out info))
                return true;
        }

        info = default;
        return false;
    }

    internal bool TryResolveLegacyAttachmentImage(
        IFrameBufferAttachement attachment,
        int mipLevel,
        int layerIndex,
        ImageAspectFlags aspectMask,
        VulkanResourceAllocator resourceAllocator,
        out BlitImageInfo info)
    {
        if (attachment is XRTexture texture)
        {
            ImageLayout layout = (aspectMask & ImageAspectFlags.ColorBit) != 0
                ? ImageLayout.ColorAttachmentOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;
            PipelineStageFlags stage = (aspectMask & ImageAspectFlags.ColorBit) != 0
                ? PipelineStageFlags.ColorAttachmentOutputBit
                : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
            AccessFlags access = (aspectMask & ImageAspectFlags.ColorBit) != 0
                ? AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
                : AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
            return TryResolveLegacyTextureBlitImage(
                texture,
                mipLevel,
                layerIndex,
                aspectMask,
                layout,
                stage,
                access,
                resourceAllocator,
                out info);
        }

        if (attachment is not XRRenderBuffer renderBuffer ||
            ResourceRuntime.WrapperLookup.GetOrCreate(renderBuffer, true) is not VkRenderBuffer vkRenderBuffer)
        {
            info = default;
            return false;
        }

        vkRenderBuffer.RefreshIfStale();
        if (IsDepthOrStencilAspect(aspectMask) &&
            (vkRenderBuffer.Aspect & aspectMask) != aspectMask)
        {
            info = default;
            return false;
        }

        ImageLayout effectiveLayout = vkRenderBuffer.PhysicalGroup?.LastKnownLayout ??
            ((aspectMask & ImageAspectFlags.ColorBit) != 0
                ? ImageLayout.ColorAttachmentOptimal
                : ImageLayout.DepthStencilAttachmentOptimal);
        PipelineStageFlags stageMask = (aspectMask & ImageAspectFlags.ColorBit) != 0
            ? PipelineStageFlags.ColorAttachmentOutputBit
            : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        AccessFlags accessMask = (aspectMask & ImageAspectFlags.ColorBit) != 0
            ? AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
            : AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
        info = new BlitImageInfo(
            vkRenderBuffer.Image,
            vkRenderBuffer.Format,
            aspectMask,
            0,
            1,
            0,
            vkRenderBuffer.ResolveAttachmentExtent(),
            effectiveLayout,
            stageMask,
            accessMask,
            renderBufferSource: vkRenderBuffer);
        return info.IsValid;
    }

    internal bool TryResolveLegacyTextureBlitImage(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
        ImageAspectFlags aspectMask,
        ImageLayout layout,
        PipelineStageFlags stage,
        AccessFlags access,
        VulkanResourceAllocator resourceAllocator,
        out BlitImageInfo info)
    {
        info = default;
        string? resourceName = texture.Name;
        if (string.IsNullOrWhiteSpace(resourceName))
            resourceName = texture.GetDescribingName();

        // Named render-graph resources are owned by the planner generation installed for this
        // operation. Prefer that generation's physical image over the process-wide logical
        // wrapper, which can currently be rebound to another viewport or OpenXR eye.
        if (TryResolvePhysicalGroupBlitImage(
                texture,
                resourceName,
                mipLevel,
                layerIndex,
                aspectMask,
                stage,
                access,
                resourceAllocator,
                out info))
        {
            return true;
        }

        AbstractRenderAPIObject apiObject = ResourceRuntime.WrapperLookup.GetOrCreate(texture, true);
        if (apiObject is VkTextureView textureView)
        {
            textureView.RefreshDescriptorFromViewedTextureIfStale();
            apiObject = textureView;
        }

        if (apiObject is not IVkImageDescriptorSource source || source.DescriptorImage.Handle == 0)
            return TryResolvePhysicalGroupBlitImage(
                texture,
                resourceName,
                mipLevel,
                layerIndex,
                aspectMask,
                stage,
                access,
                resourceAllocator,
                out info);

        Format format = source.DescriptorFormat;
        if (IsDepthOrStencilAspect(aspectMask)
                ? !IsDepthOrStencilFormat(format)
                : (aspectMask & ImageAspectFlags.ColorBit) == 0)
            return false;

        (uint baseArrayLayer, uint blitLayerCount) = ResolveDescriptorTextureBlitLayerRange(
            texture,
            layerIndex,
            source.DescriptorArrayLayers);
        uint mipLevels = Math.Max(source.DescriptorMipLevels, 1u);
        uint resolvedMipLevel = Math.Min((uint)Math.Max(mipLevel, 0), mipLevels - 1u);
        ImageLayout effectiveLayout = ResolveLegacyTextureLayout(
            source,
            resourceName,
            resourceAllocator,
            resolvedMipLevel,
            layerIndex,
            baseArrayLayer,
            layout);

        info = new BlitImageInfo(
            source.DescriptorImage,
            format,
            aspectMask,
            baseArrayLayer,
            blitLayerCount,
            resolvedMipLevel,
            ResolveTextureBlitExtent(texture, source, mipLevel, layerIndex, resolvedMipLevel),
            effectiveLayout,
            stage,
            access,
            source);
        return info.IsValid;
    }

    internal bool TryResolveLiveBlitImage(in BlitImageInfo info, out BlitImageInfo resolved)
    {
        resolved = info;
        if (info.DescriptorSource is { } source)
        {
            if (source is VkObjectBase vkObject && !vkObject.IsActive)
                vkObject.Generate();

            Image image = source.DescriptorImage;
            if (image.Handle == 0)
                return false;

            ImageLayout layout = ResolveLiveBlitLayout(info, image, source);
            Extent2D extent = info.Extent;
            if (source is IVkFrameBufferAttachmentSource attachment &&
                attachment.TryGetAttachmentExtent(
                    checked((int)info.MipLevel),
                    ResolveBlitInfoLayerIndex(info),
                    out Extent2D attachmentExtent))
            {
                extent = attachmentExtent;
            }

            resolved = info.WithResolvedState(image, layout, extent);
            return true;
        }

        if (info.RenderBufferSource is { } renderBuffer)
        {
            if (!renderBuffer.IsActive)
                renderBuffer.Generate();
            renderBuffer.RefreshIfStale();
            if (renderBuffer.Image.Handle == 0)
                return false;

            ImageLayout layout = TryGetExactTrackedBlitLayout(
                info,
                renderBuffer.Image,
                out ImageLayout tracked)
                    ? tracked
                    : renderBuffer.PhysicalGroup is not null
                        ? ImageLayout.Undefined
                        : info.PreferredLayout;
            resolved = info.WithResolvedState(
                renderBuffer.Image,
                layout,
                renderBuffer.ResolveAttachmentExtent());
            return true;
        }

        return info.Image.Handle != 0;
    }

    internal BlitImageInfo ResolveLegacySwapchainBlitImage(
        uint swapchainImageIndex,
        bool wantColor,
        bool wantDepth,
        bool wantStencil,
        in SwapchainRecordingTarget recordingTarget,
        Image[]? desktopImages,
        Format desktopFormat,
        Extent2D desktopExtent,
        VulkanSwapchainDepthResources? desktopDepth)
    {
        if (recordingTarget.IsValid)
        {
            if (wantColor)
                return CreateColorBlitInfo(
                    recordingTarget.Image,
                    recordingTarget.ImageFormat,
                    recordingTarget.Extent);

            ImageAspectFlags aspect = ResolveDepthAspect(
                recordingTarget.DepthAspect,
                wantDepth,
                wantStencil);
            if (aspect != ImageAspectFlags.None)
                return CreateDepthBlitInfo(
                    recordingTarget.DepthImage,
                    recordingTarget.DepthFormat,
                    aspect,
                    recordingTarget.Extent);
        }

        if (wantColor && desktopImages is not null && swapchainImageIndex < desktopImages.Length)
            return CreateColorBlitInfo(
                desktopImages[swapchainImageIndex],
                desktopFormat,
                desktopExtent);

        if (desktopDepth is not null)
        {
            ImageAspectFlags aspect = ResolveDepthAspect(
                desktopDepth.Aspect,
                wantDepth,
                wantStencil);
            if (aspect != ImageAspectFlags.None)
                return CreateDepthBlitInfo(
                    desktopDepth.Image,
                    desktopDepth.Format,
                    aspect,
                    desktopDepth.Extent);
        }

        return default;
    }

    internal static (uint BaseArrayLayer, uint LayerCount) ResolveDescriptorTextureBlitLayerRange(
        XRTexture texture,
        int layerIndex,
        uint descriptorArrayLayers)
    {
        uint baseArrayLayer = texture is XRTexture3D
            ? 0u
            : ResolveBlitBaseArrayLayer(texture, layerIndex);
        uint addressableLayerLimit = ResolveDescriptorAddressableLayerLimit(
            texture,
            descriptorArrayLayers);
        if (baseArrayLayer >= addressableLayerLimit)
            baseArrayLayer = addressableLayerLimit - 1u;

        uint layerCount = ResolveBlitLayerCount(
            texture,
            layerIndex,
            addressableLayerLimit,
            baseArrayLayer);
        return (baseArrayLayer, layerCount);
    }

    private bool TryResolvePhysicalGroupBlitImage(
        XRTexture texture,
        string? resourceName,
        int mipLevel,
        int layerIndex,
        ImageAspectFlags aspectMask,
        PipelineStageFlags stage,
        AccessFlags access,
        VulkanResourceAllocator resourceAllocator,
        out BlitImageInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(resourceName) ||
            !resourceAllocator.TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group) ||
            group is null ||
            !group.IsAllocated ||
            group.Image.Handle == 0)
            return false;

        if (IsDepthOrStencilAspect(aspectMask)
                ? !IsDepthOrStencilFormat(group.Format)
                : (aspectMask & ImageAspectFlags.ColorBit) == 0)
            return false;

        uint baseArrayLayer = ResolveBlitBaseArrayLayer(texture, layerIndex);
        uint availableLayers = Math.Max(group.Template.Layers, 1u);
        baseArrayLayer = Math.Min(baseArrayLayer, availableLayers - 1u);
        uint layerCount = ResolveBlitLayerCount(
            texture,
            layerIndex,
            availableLayers,
            baseArrayLayer);
        uint mipLevels = Math.Max(group.MipLevels, 1u);
        uint resolvedMipLevel = Math.Min((uint)Math.Max(mipLevel, 0), mipLevels - 1u);
        uint width = Math.Max(group.ResolvedExtent.Width >> (int)resolvedMipLevel, 1u);
        uint height = Math.Max(group.ResolvedExtent.Height >> (int)resolvedMipLevel, 1u);
        ImageLayout layout = group.GetKnownLayout(
            resolvedMipLevel,
            1,
            baseArrayLayer,
            layerCount);
        if (layout == ImageLayout.Undefined)
            layout = group.LastKnownLayout;

        info = new BlitImageInfo(
            group.Image,
            group.Format,
            aspectMask,
            baseArrayLayer,
            layerCount,
            resolvedMipLevel,
            new Extent2D(width, height),
            layout,
            stage,
            access,
            samples: group.Samples);
        return info.IsValid;
    }

    private ImageLayout ResolveLegacyTextureLayout(
        IVkImageDescriptorSource source,
        string? resourceName,
        VulkanResourceAllocator resourceAllocator,
        uint mipLevel,
        int layerIndex,
        uint baseArrayLayer,
        ImageLayout fallback)
    {
        if (source is IVkFrameBufferAttachmentSource attachmentSource)
        {
            ImageLayout attachmentLayout = attachmentSource.GetAttachmentTrackedLayout(
                checked((int)mipLevel),
                layerIndex < 0 ? -1 : checked((int)baseArrayLayer));
            if (attachmentLayout != ImageLayout.Undefined)
                return attachmentLayout;
        }

        if (!string.IsNullOrWhiteSpace(resourceName) &&
            resourceAllocator.TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group) &&
            group?.IsAllocated == true)
            return group.LastKnownLayout;

        ImageLayout trackedLayout = source.TrackedImageLayout;
        return trackedLayout != ImageLayout.Undefined || !source.UsesAllocatorImage
            ? trackedLayout
            : fallback;
    }

    private ImageLayout ResolveLiveBlitLayout(
        in BlitImageInfo info,
        Image image,
        IVkImageDescriptorSource source)
    {
        if (TryGetExactTrackedBlitLayout(info, image, out ImageLayout tracked))
            return tracked;
        if (source is IVkFrameBufferAttachmentSource attachment)
        {
            ImageLayout attachmentLayout = attachment.GetAttachmentTrackedLayout(
                checked((int)info.MipLevel),
                ResolveBlitInfoLayerIndex(info));
            if (attachmentLayout != ImageLayout.Undefined)
                return attachmentLayout;
        }

        ImageLayout layout = source.TrackedImageLayout;
        return layout == ImageLayout.Undefined && !source.UsesAllocatorImage
            ? info.PreferredLayout
            : layout;
    }

    private ImageLayout ResolveLiveBlitOldLayout(
        in BlitImageInfo info,
        ImageLayout requestedOldLayout)
    {
        if (TryGetExactTrackedBlitLayout(info, info.Image, out ImageLayout exactLayout))
            return exactLayout;
        if (info.DescriptorSource is { } source)
        {
            if (source.UsesAllocatorImage)
                return ImageLayout.Undefined;
            ImageLayout trackedLayout = ResolveLiveBlitLayout(info, info.Image, source);
            if (trackedLayout != ImageLayout.Undefined)
                return trackedLayout;
        }
        if (info.RenderBufferSource?.PhysicalGroup is not null)
            return ImageLayout.Undefined;
        return requestedOldLayout;
    }

    private bool TryGetExactTrackedBlitLayout(
        in BlitImageInfo info,
        Image image,
        out ImageLayout layout)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = NormalizeBarrierAspectMask(info.Format, info.AspectMask),
            BaseMipLevel = info.MipLevel,
            LevelCount = 1,
            BaseArrayLayer = info.BaseArrayLayer,
            LayerCount = Math.Max(info.LayerCount, 1u),
        };
        return TryGetTrackedImageLayout(image, range, out layout);
    }

    private static ImageAspectFlags ResolveRequestedBlitAspect(
        EFrameBufferAttachment attachment,
        EFrameBufferAttachment desiredColorAttachment,
        bool wantColor,
        bool wantDepth,
        bool wantStencil)
    {
        if (IsColorAttachment(attachment) && wantColor)
            return attachment == desiredColorAttachment
                ? ImageAspectFlags.ColorBit
                : ImageAspectFlags.None;
        if (attachment == EFrameBufferAttachment.DepthStencilAttachment)
            return (wantDepth, wantStencil) switch
            {
                (true, true) => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                (true, false) => ImageAspectFlags.DepthBit,
                (false, true) => ImageAspectFlags.StencilBit,
                _ => ImageAspectFlags.None,
            };
        if (attachment == EFrameBufferAttachment.DepthAttachment && wantDepth)
            return ImageAspectFlags.DepthBit;
        return attachment == EFrameBufferAttachment.StencilAttachment && wantStencil
            ? ImageAspectFlags.StencilBit
            : ImageAspectFlags.None;
    }

    private static int ResolveLegacyReadBufferColorAttachmentIndex(EReadBufferMode mode)
        => mode is >= EReadBufferMode.ColorAttachment0 and <= EReadBufferMode.ColorAttachment31
            ? (int)mode - (int)EReadBufferMode.ColorAttachment0
            : 0;

    private static ImageAspectFlags ResolveDepthAspect(
        ImageAspectFlags availableAspect,
        bool wantDepth,
        bool wantStencil)
        => (wantDepth, wantStencil) switch
        {
            (true, true) => availableAspect,
            (true, false) => ImageAspectFlags.DepthBit,
            (false, true) when (availableAspect & ImageAspectFlags.StencilBit) != 0 =>
                ImageAspectFlags.StencilBit,
            _ => ImageAspectFlags.None,
        };

    private static BlitImageInfo CreateColorBlitInfo(
        Image image,
        Format format,
        Extent2D extent)
        => new(
            image,
            format,
            ImageAspectFlags.ColorBit,
            0,
            1,
            0,
            extent,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);

    private static BlitImageInfo CreateDepthBlitInfo(
        Image image,
        Format format,
        ImageAspectFlags aspect,
        Extent2D extent)
        => new(
            image,
            format,
            aspect,
            0,
            1,
            0,
            extent,
            ImageLayout.DepthStencilAttachmentOptimal,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);

    private static uint ResolveBlitBaseArrayLayer(XRTexture texture, int layerIndex)
    {
        uint resolvedLayer = layerIndex >= 0 ? (uint)layerIndex : 0u;
        return texture switch
        {
            XRTexture1D or XRTexture2D or XRTexture3D or XRTextureRectangle => 0,
            XRTextureViewBase view => ResolveViewBlitBaseLayer(view, resolvedLayer),
            _ => resolvedLayer,
        };
    }

    private static uint ResolveDescriptorAddressableLayerLimit(
        XRTexture texture,
        uint descriptorArrayLayers)
    {
        uint localLayerCount = Math.Max(descriptorArrayLayers, 1u);
        if (texture is not XRTextureViewBase view)
            return localLayerCount;
        ulong exclusiveEnd = (ulong)view.MinLayer + localLayerCount;
        return (uint)Math.Min(Math.Max(exclusiveEnd, 1UL), uint.MaxValue);
    }

    private static uint ResolveBlitLayerCount(
        XRTexture texture,
        int layerIndex,
        uint availableLayers,
        uint baseArrayLayer)
    {
        availableLayers = Math.Max(availableLayers, 1u);
        if (layerIndex >= 0 || texture is XRTexture3D)
            return 1u;
        uint remainingLayers = Math.Max(
            availableLayers - Math.Min(baseArrayLayer, availableLayers - 1u),
            1u);
        uint requestedLayers = texture is XRTextureViewBase view
            ? Math.Max(view.NumLayers, 1u)
            : remainingLayers;
        return Math.Max(Math.Min(requestedLayers, remainingLayers), 1u);
    }

    private static uint ResolveViewBlitBaseLayer(XRTextureViewBase view, uint resolvedLayer)
        => view.TextureTarget is ETextureTarget.Texture1D or
            ETextureTarget.Texture2D or
            ETextureTarget.Texture3D or
            ETextureTarget.TextureRectangle
                ? view.MinLayer
                : view.MinLayer + resolvedLayer;

    private static Extent2D ResolveTextureBlitExtent(
        XRTexture texture,
        IVkImageDescriptorSource source,
        int mipLevel,
        int layerIndex,
        uint resolvedMipLevel)
    {
        if (source is IVkFrameBufferAttachmentSource attachmentSource &&
            attachmentSource.TryGetAttachmentExtent(
                Math.Max(mipLevel, 0),
                layerIndex,
                out Extent2D resolvedExtent) &&
            resolvedExtent.Width > 0 &&
            resolvedExtent.Height > 0)
            return resolvedExtent;

        Vector3 dimensions = texture.WidthHeightDepth;
        uint width = Math.Max((uint)Math.Max(dimensions.X, 1.0f), 1u);
        uint height = Math.Max((uint)Math.Max(dimensions.Y, 1.0f), 1u);
        if (resolvedMipLevel > 0)
        {
            width = Math.Max(width >> (int)resolvedMipLevel, 1u);
            height = Math.Max(height >> (int)resolvedMipLevel, 1u);
        }
        return new Extent2D(width, height);
    }

    private static int ResolveBlitInfoLayerIndex(in BlitImageInfo info)
        => info.LayerCount == 1u
            ? checked((int)info.BaseArrayLayer)
            : -1;
}
