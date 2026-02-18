using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== BlitImageInfo ===========

        private readonly struct BlitImageInfo(
            Image image,
            Format format,
            ImageAspectFlags aspectMask,
            uint baseArrayLayer,
            uint layerCount,
            uint mipLevel,
            ImageLayout preferredLayout,
            PipelineStageFlags stageMask,
            AccessFlags accessMask,
            VulkanRenderer.IVkImageDescriptorSource? descriptorSource = null,
            VulkanRenderer.VkRenderBuffer? renderBufferSource = null)
        {
            public Image Image { get; } = image;
            public Format Format { get; } = format;
            public ImageAspectFlags AspectMask { get; } = aspectMask;
            public uint BaseArrayLayer { get; } = baseArrayLayer;
            public uint LayerCount { get; } = layerCount;
            public uint MipLevel { get; } = mipLevel;
            public ImageLayout PreferredLayout { get; } = preferredLayout;
            public PipelineStageFlags StageMask { get; } = stageMask;
            public AccessFlags AccessMask { get; } = accessMask;
            public IVkImageDescriptorSource? DescriptorSource { get; } = descriptorSource;
            public VkRenderBuffer? RenderBufferSource { get; } = renderBufferSource;
            public bool IsValid => Image.Handle != 0;

            public BlitImageInfo WithResolvedState(Image image, ImageLayout preferredLayout)
                => new(
                    image,
                    Format,
                    AspectMask,
                    BaseArrayLayer,
                    LayerCount,
                    MipLevel,
                    preferredLayout,
                    StageMask,
                    AccessMask,
                    DescriptorSource,
                    RenderBufferSource);
        }

        // =========== Blit / Copy Operations ===========

        public override void Blit(
            XRFrameBuffer? inFBO,
            XRFrameBuffer? outFBO,
            int inX, int inY, uint inW, uint inH,
            int outX, int outY, uint outW, uint outH,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            if (!colorBit && !depthBit && !stencilBit)
                return;

            if (inFBO is null && outFBO is null)
                return;

            if (inW == 0 || inH == 0 || outW == 0 || outH == 0)
                return;

            if (inFBO is not null)
            {
                EnsureFrameBufferRegistered(inFBO);
                EnsureFrameBufferAttachmentsRegistered(inFBO);
            }

            if (outFBO is not null)
            {
                EnsureFrameBufferRegistered(outFBO);
                EnsureFrameBufferAttachmentsRegistered(outFBO);
            }

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new BlitOp(
                EnsureValidPassIndex(passIndex, "Blit"),
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
                CaptureFrameOpContext()));
        }

        // =========== Image Resolution Helpers ===========

        private bool TryResolveBlitImage(
            XRFrameBuffer? frameBuffer,
            uint swapchainImageIndex,
            EReadBufferMode readBufferMode,
            bool wantColor,
            bool wantDepth,
            bool wantStencil,
            out BlitImageInfo info,
            bool isSource)
        {
            if (frameBuffer is null)
            {
                info = ResolveSwapchainBlitImage(swapchainImageIndex, wantColor, wantDepth, wantStencil);
                return info.IsValid;
            }

            var targets = frameBuffer.Targets;
            if (targets is null)
            {
                info = default;
                return false;
            }

            int desiredColorIndex = isSource ? ResolveReadBufferColorAttachmentIndex(readBufferMode) : 0;
            EFrameBufferAttachment desiredColorAttachment = (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + desiredColorIndex);

            foreach (var (target, attachment, mipLevel, layerIndex) in targets)
            {
                ImageAspectFlags aspect = ImageAspectFlags.None;

                if (IsColorAttachment(attachment) && wantColor)
                {
                    if (attachment != desiredColorAttachment)
                        continue;
                    aspect = ImageAspectFlags.ColorBit;
                }
                else if (attachment == EFrameBufferAttachment.DepthStencilAttachment && (wantDepth || wantStencil))
                {
                    aspect = (wantDepth, wantStencil) switch
                    {
                        (true, true) => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                        (true, false) => ImageAspectFlags.DepthBit,
                        (false, true) => ImageAspectFlags.StencilBit,
                        _ => ImageAspectFlags.None
                    };
                }
                else if (attachment == EFrameBufferAttachment.DepthAttachment && wantDepth)
                    aspect = ImageAspectFlags.DepthBit;
                else if (attachment == EFrameBufferAttachment.StencilAttachment && wantStencil)
                    aspect = ImageAspectFlags.StencilBit;

                if (aspect == ImageAspectFlags.None)
                    continue;

                if (TryResolveAttachmentImage(target, mipLevel, layerIndex, aspect, out info))
                    return true;
            }

            info = default;
            return false;
        }

        private bool TryResolveAttachmentImage(IFrameBufferAttachement attachment, int mipLevel, int layerIndex, ImageAspectFlags aspectMask, out BlitImageInfo info)
        {
            info = default;

            ImageLayout layout = aspectMask.HasFlag(ImageAspectFlags.ColorBit)
                ? ImageLayout.ColorAttachmentOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;

            PipelineStageFlags stage = aspectMask.HasFlag(ImageAspectFlags.ColorBit)
                ? PipelineStageFlags.ColorAttachmentOutputBit
                : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;

            AccessFlags access = aspectMask.HasFlag(ImageAspectFlags.ColorBit)
                ? AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
                : AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;

            switch (attachment)
            {
                case XRTexture texture:
                    return TryResolveTextureBlitImage(texture, mipLevel, layerIndex, aspectMask, layout, stage, access, out info);
                case XRRenderBuffer renderBuffer when GetOrCreateAPIRenderObject(renderBuffer, true) is VkRenderBuffer vkRenderBuffer:
                    // Refresh the cached image handle in case the physical group was reallocated.
                    vkRenderBuffer.RefreshIfStale();
                    // Allow depth/stencil or color depending on the requested aspect and buffer format.
                    if (IsDepthOrStencilAspect(aspectMask) && (vkRenderBuffer.Aspect & aspectMask) != aspectMask)
                        return false;

                    // Use the physical group's tracked layout when available so the
                    // blit transition barrier uses the correct OldLayout.
                    ImageLayout effectiveLayout = layout;
                    if (vkRenderBuffer.PhysicalGroup is { } group)
                        effectiveLayout = group.LastKnownLayout;

                    info = new BlitImageInfo(
                        vkRenderBuffer.Image,
                        vkRenderBuffer.Format,
                        aspectMask,
                        0,
                        1,
                        0,
                        effectiveLayout,
                        stage,
                        access,
                        renderBufferSource: vkRenderBuffer);
                    return info.IsValid;
                default:
                    return false;
            }
        }

        private bool TryResolveTextureBlitImage(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            ImageAspectFlags aspectMask,
            ImageLayout layout,
            PipelineStageFlags stage,
            AccessFlags access,
            out BlitImageInfo info)
        {
            info = default;
            if (GetOrCreateAPIRenderObject(texture, true) is not { } apiObject)
                return false;

            if (apiObject is VkTextureView textureView)
            {
                // Texture views can outlive backing physical image reallocations.
                // Rebuild the view so DescriptorImage points at a currently valid VkImage.
                textureView.Destroy();
                textureView.Generate();
                apiObject = textureView;
            }

            if (apiObject is not IVkImageDescriptorSource source)
                return false;

            if (source.DescriptorImage.Handle == 0)
                return false;

            Format format = source.DescriptorFormat;
            if (IsDepthOrStencilAspect(aspectMask))
            {
                if (!IsDepthOrStencilFormat(format))
                    return false;
            }
            else if (!aspectMask.HasFlag(ImageAspectFlags.ColorBit))
            {
                return false;
            }

            uint baseArrayLayer = ResolveBlitBaseArrayLayer(texture, layerIndex);
            if (texture is XRTexture3D)
                baseArrayLayer = 0;

            ImageLayout effectiveLayout = layout;
            string? resourceName = texture.Name;
            if (string.IsNullOrWhiteSpace(resourceName))
                resourceName = texture.GetDescribingName();

            if (!string.IsNullOrWhiteSpace(resourceName) &&
                ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group) &&
                group is not null &&
                group.IsAllocated)
            {
                effectiveLayout = group.LastKnownLayout;
            }
            else if (!source.UsesAllocatorImage)
            {
                // For dedicated (non-planner) images, ALWAYS use the texture's own
                // tracked layout so blit transitions emit a correct OldLayout.
                // Newly-created images report Undefined, which is correct — the blit
                // pre-transition barrier must use Undefined as OldLayout, not the
                // hardcoded attachment-optimal layout.
                effectiveLayout = source.TrackedImageLayout;
            }

            info = new BlitImageInfo(
                source.DescriptorImage,
                format,
                aspectMask,
                baseArrayLayer,
                1,
                (uint)Math.Max(mipLevel, 0),
                effectiveLayout,
                stage,
                access,
                source);

            return info.IsValid;
        }

        private static bool TryResolveLiveBlitImage(in BlitImageInfo info, out BlitImageInfo resolved)
        {
            resolved = info;

            if (info.DescriptorSource is { } source)
            {
                if (source is VkObjectBase vkObject && !vkObject.IsActive)
                    vkObject.Generate();

                Image liveImage = source.DescriptorImage;
                if (liveImage.Handle == 0)
                    return false;

                resolved = info.WithResolvedState(liveImage, info.PreferredLayout);
                return true;
            }

            if (info.RenderBufferSource is { } renderBuffer)
            {
                if (!renderBuffer.IsActive)
                    renderBuffer.Generate();

                renderBuffer.RefreshIfStale();
                Image liveImage = renderBuffer.Image;
                if (liveImage.Handle == 0)
                    return false;

                ImageLayout liveLayout = info.PreferredLayout;
                if (renderBuffer.PhysicalGroup is { } group)
                    liveLayout = group.LastKnownLayout;

                resolved = info.WithResolvedState(liveImage, liveLayout);
                return true;
            }

            return info.Image.Handle != 0;
        }

        // =========== Format / Attachment Helpers ===========

        private static bool IsColorAttachment(EFrameBufferAttachment attachment)
            => attachment >= EFrameBufferAttachment.ColorAttachment0 && attachment <= EFrameBufferAttachment.ColorAttachment31;

        private static bool IsDepthOrStencilAspect(ImageAspectFlags aspectMask)
            => (aspectMask & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0;

        private static int ResolveReadBufferColorAttachmentIndex(EReadBufferMode mode)
        {
            if (mode >= EReadBufferMode.ColorAttachment0 && mode <= EReadBufferMode.ColorAttachment31)
                return (int)mode - (int)EReadBufferMode.ColorAttachment0;

            return 0;
        }

        private static bool IsDepthOrStencilFormat(Format format)
            => format is Format.D16Unorm
                or Format.D32Sfloat
                or Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint
                or Format.D16UnormS8Uint
                or Format.X8D24UnormPack32;

        private static bool IsCombinedDepthStencilFormat(Format format)
            => format is Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint
                or Format.D16UnormS8Uint;

        private static ImageAspectFlags NormalizeBarrierAspectMask(Format format, ImageAspectFlags aspectMask)
        {
            if (!IsCombinedDepthStencilFormat(format))
                return aspectMask;

            if ((aspectMask & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == 0)
                return aspectMask;

            return aspectMask | ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        }

        // =========== Swapchain Image Resolution ===========

        private BlitImageInfo ResolveSwapchainBlitImage(uint swapchainImageIndex, bool wantColor, bool wantDepth, bool wantStencil)
        {
            if (wantColor && swapChainImages is not null && swapchainImageIndex < swapChainImages.Length)
            {
                return new BlitImageInfo(
                    swapChainImages[swapchainImageIndex],
                    swapChainImageFormat,
                    ImageAspectFlags.ColorBit,
                    0,
                    1,
                    0,
                    ImageLayout.ColorAttachmentOptimal,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
            }

            if ((wantDepth || wantStencil) && _swapchainDepthImage.Handle != 0)
            {
                ImageAspectFlags depthAspect = (wantDepth, wantStencil) switch
                {
                    (true, true) => _swapchainDepthAspect,
                    (true, false) => ImageAspectFlags.DepthBit,
                    (false, true) => _swapchainDepthAspect.HasFlag(ImageAspectFlags.StencilBit) ? ImageAspectFlags.StencilBit : ImageAspectFlags.None,
                    _ => ImageAspectFlags.None
                };

                if (depthAspect != ImageAspectFlags.None)
                {
                    return new BlitImageInfo(
                        _swapchainDepthImage,
                        _swapchainDepthFormat,
                        depthAspect,
                        0,
                        1,
                        0,
                        ImageLayout.DepthStencilAttachmentOptimal,
                        PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                        AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
                }
            }

            return default;
        }

        // =========== Layer / Array Resolution ===========

        private static uint ResolveLayerIndex(int layerIndex)
            => layerIndex >= 0 ? (uint)layerIndex : 0u;

        private static uint ResolveBlitBaseArrayLayer(XRTexture texture, int layerIndex)
        {
            uint resolvedLayer = ResolveLayerIndex(layerIndex);
            return texture switch
            {
                XRTexture1D => 0,
                XRTexture2D => 0,
                XRTexture3D => 0,
                XRTextureRectangle => 0,
                XRTextureViewBase view => ResolveViewBlitBaseLayer(view, resolvedLayer),
                _ => resolvedLayer
            };
        }

        private static uint ResolveViewBlitBaseLayer(XRTextureViewBase view, uint resolvedLayer)
            => view.TextureTarget switch
            {
                ETextureTarget.Texture1D => view.MinLayer,
                ETextureTarget.Texture2D => view.MinLayer,
                ETextureTarget.Texture3D => view.MinLayer,
                ETextureTarget.TextureRectangle => view.MinLayer,
                _ => view.MinLayer + resolvedLayer
            };

        private static ImageBlit BuildImageBlit(
            BlitImageInfo source,
            BlitImageInfo destination,
            int inX, int inY, uint inW, uint inH,
            int outX, int outY, uint outW, uint outH)
        {
            ImageBlit region = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = source.AspectMask,
                    MipLevel = source.MipLevel,
                    BaseArrayLayer = source.BaseArrayLayer,
                    LayerCount = source.LayerCount
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = destination.AspectMask,
                    MipLevel = destination.MipLevel,
                    BaseArrayLayer = destination.BaseArrayLayer,
                    LayerCount = destination.LayerCount
                }
            };

            region.SrcOffsets.Element0 = new Offset3D { X = inX, Y = inY, Z = 0 };
            region.SrcOffsets.Element1 = new Offset3D { X = inX + (int)inW, Y = inY + (int)inH, Z = 1 };
            region.DstOffsets.Element0 = new Offset3D { X = outX, Y = outY, Z = 0 };
            region.DstOffsets.Element1 = new Offset3D { X = outX + (int)outW, Y = outY + (int)outH, Z = 1 };

            return region;
        }

        // =========== Image Transitions ===========

        private void TransitionForBlit(
            CommandBuffer commandBuffer,
            BlitImageInfo info,
            ImageLayout oldLayout,
            ImageLayout newLayout,
            AccessFlags srcAccess,
            AccessFlags dstAccess,
            PipelineStageFlags srcStage,
            PipelineStageFlags dstStage)
        {
            if (!TryResolveLiveBlitImage(info, out BlitImageInfo resolvedInfo))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.TransitionForBlit.UnresolvedImage",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping blit transition — could not resolve a live image handle.");
                return;
            }

            // Guard against stale or destroyed image handles that passed the zero-check
            // but are no longer valid Vulkan objects (e.g. after physical group reallocation).
            if (resolvedInfo.Image.Handle == 0)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.TransitionForBlit.NullImage",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping blit transition — image handle is null.");
                return;
            }

            ImageAspectFlags barrierAspectMask = NormalizeBarrierAspectMask(resolvedInfo.Format, resolvedInfo.AspectMask);

            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = resolvedInfo.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = barrierAspectMask,
                    BaseMipLevel = resolvedInfo.MipLevel,
                    LevelCount = 1,
                    BaseArrayLayer = resolvedInfo.BaseArrayLayer,
                    LayerCount = resolvedInfo.LayerCount
                }
            };

            ImageMemoryBarrier* barrierPtr = stackalloc ImageMemoryBarrier[1];
            barrierPtr[0] = barrier;

            Api!.CmdPipelineBarrier(
                commandBuffer,
                srcStage,
                dstStage,
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                1,
                barrierPtr);
        }

        private void TransitionSwapchainImage(CommandBuffer commandBuffer, Image image, ImageLayout oldLayout, ImageLayout newLayout)
        {
            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryReadBit,
                DstAccessMask = newLayout == ImageLayout.TransferSrcOptimal ? AccessFlags.TransferReadBit : AccessFlags.MemoryReadBit,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            ImageMemoryBarrier* barrierPtr = stackalloc ImageMemoryBarrier[1];
            barrierPtr[0] = barrier;

            Api!.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                newLayout == ImageLayout.TransferSrcOptimal ? PipelineStageFlags.TransferBit : PipelineStageFlags.AllCommandsBit,
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                1,
                barrierPtr);
        }

        // =========== Readback Region Helpers ===========

        private static void ClampReadbackRegion(BoundingRectangle region, uint sourceWidth, uint sourceHeight, out int x, out int y, out int width, out int height)
        {
            int maxX = Math.Max((int)sourceWidth - 1, 0);
            int maxY = Math.Max((int)sourceHeight - 1, 0);
            x = Math.Clamp(region.X, 0, maxX);
            y = Math.Clamp(region.Y, 0, maxY);

            int requestedWidth = region.Width > 0 ? region.Width : (int)sourceWidth;
            int requestedHeight = region.Height > 0 ? region.Height : (int)sourceHeight;
            int availableWidth = Math.Max((int)sourceWidth - x, 1);
            int availableHeight = Math.Max((int)sourceHeight - y, 1);

            width = Math.Clamp(requestedWidth, 1, availableWidth);
            height = Math.Clamp(requestedHeight, 1, availableHeight);
        }

        // =========== Color Pixel Reading ===========

        private bool TryReadColorPixel(in BlitImageInfo source, int x, int y, out ColorF4 color)
        {
            color = ColorF4.Transparent;

            if (!TryReadColorRegionRgba8(source, x, y, 1, 1, out byte[] rgba) || rgba.Length < 4)
                return false;

            color = new ColorF4(
                rgba[0] / 255f,
                rgba[1] / 255f,
                rgba[2] / 255f,
                rgba[3] / 255f);
            return true;
        }

        private bool TryReadColorRegionRgba8(in BlitImageInfo source, int x, int y, int width, int height, out byte[] rgbaPixels)
        {
            rgbaPixels = [];

            if (!source.IsValid || !source.AspectMask.HasFlag(ImageAspectFlags.ColorBit))
                return false;

            uint sourcePixelSize = GetColorFormatPixelSize(source.Format);
            if (sourcePixelSize == 0)
                return false;

            ulong rawByteCount = (ulong)(width * height) * sourcePixelSize;
            var (stagingBuffer, stagingMemory) = CreateBuffer(
                rawByteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            try
            {
                using var scope = NewCommandScope();

                ImageLayout preTransferLayout = source.PreferredLayout;
                ImageLayout postTransferLayout = preTransferLayout != ImageLayout.Undefined
                    ? preTransferLayout
                    : ImageLayout.ColorAttachmentOptimal;

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    source.AccessMask,
                    AccessFlags.TransferReadBit,
                    source.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = source.MipLevel,
                        BaseArrayLayer = source.BaseArrayLayer,
                        LayerCount = source.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 }
                };

                Api!.CmdCopyImageToBuffer(
                    scope.CommandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    source.AccessMask,
                    PipelineStageFlags.TransferBit,
                    source.StageMask);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, rawByteCount, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            try
            {
                rgbaPixels = new byte[width * height * 4];
                return TryConvertColorPixelsToRgba8(mappedPtr, source.Format, width * height, rgbaPixels);
            }
            finally
            {
                Api!.UnmapMemory(device, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }

        private bool TryReadColorRegionRgbaFloat(in BlitImageInfo source, int x, int y, int width, int height, out float[] rgbaFloats)
        {
            rgbaFloats = [];

            if (!source.IsValid || !source.AspectMask.HasFlag(ImageAspectFlags.ColorBit))
                return false;

            uint sourcePixelSize = GetColorFormatPixelSize(source.Format);
            if (sourcePixelSize == 0)
                return false;

            ulong rawByteCount = (ulong)(width * height) * sourcePixelSize;
            var (stagingBuffer, stagingMemory) = CreateBuffer(
                rawByteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            try
            {
                using var scope = NewCommandScope();

                ImageLayout preTransferLayout = source.PreferredLayout;
                ImageLayout postTransferLayout = preTransferLayout != ImageLayout.Undefined
                    ? preTransferLayout
                    : ImageLayout.ColorAttachmentOptimal;

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    source.AccessMask,
                    AccessFlags.TransferReadBit,
                    source.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = source.MipLevel,
                        BaseArrayLayer = source.BaseArrayLayer,
                        LayerCount = source.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 }
                };

                Api!.CmdCopyImageToBuffer(
                    scope.CommandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    source.AccessMask,
                    PipelineStageFlags.TransferBit,
                    source.StageMask);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, rawByteCount, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            try
            {
                int pixelCount = width * height;
                rgbaFloats = new float[pixelCount * 4];
                return TryConvertColorPixelsToRgbaFloat(mappedPtr, source.Format, pixelCount, rgbaFloats);
            }
            finally
            {
                Api!.UnmapMemory(device, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }

        // =========== Pixel Format Conversion ===========

        private static bool TryConvertColorPixelsToRgba8(void* srcPtr, Format format, int pixelCount, byte[] dstRgba)
        {
            if (pixelCount <= 0 || dstRgba.Length < pixelCount * 4)
                return false;

            static byte FloatToByte(float v)
            {
                float clamped = Math.Clamp(v, 0.0f, 1.0f);
                return (byte)Math.Clamp((int)MathF.Round(clamped * 255.0f), 0, 255);
            }

            byte* src = (byte*)srcPtr;

            switch (format)
            {
                case Format.R8G8B8A8Unorm:
                case Format.R8G8B8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 0];
                        dstRgba[dstIndex + 1] = src[srcIndex + 1];
                        dstRgba[dstIndex + 2] = src[srcIndex + 2];
                        dstRgba[dstIndex + 3] = src[srcIndex + 3];
                    }
                    return true;

                case Format.B8G8R8A8Unorm:
                case Format.B8G8R8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 2];
                        dstRgba[dstIndex + 1] = src[srcIndex + 1];
                        dstRgba[dstIndex + 2] = src[srcIndex + 0];
                        dstRgba[dstIndex + 3] = src[srcIndex + 3];
                    }
                    return true;

                case Format.R16G16B16A16Unorm:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte(p[0] / 65535.0f);
                        dstRgba[dstIndex + 1] = FloatToByte(p[1] / 65535.0f);
                        dstRgba[dstIndex + 2] = FloatToByte(p[2] / 65535.0f);
                        dstRgba[dstIndex + 3] = FloatToByte(p[3] / 65535.0f);
                    }
                    return true;

                case Format.R16G16B16A16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[0]));
                        dstRgba[dstIndex + 1] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[1]));
                        dstRgba[dstIndex + 2] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[2]));
                        dstRgba[dstIndex + 3] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[3]));
                    }
                    return true;

                case Format.R32G32B32A32Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 16;
                        int dstIndex = i * 4;
                        float* p = (float*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte(p[0]);
                        dstRgba[dstIndex + 1] = FloatToByte(p[1]);
                        dstRgba[dstIndex + 2] = FloatToByte(p[2]);
                        dstRgba[dstIndex + 3] = FloatToByte(p[3]);
                    }
                    return true;
            }

            return false;
        }

        private static bool TryConvertColorPixelsToRgbaFloat(void* srcPtr, Format format, int pixelCount, float[] dstRgba)
        {
            if (pixelCount <= 0 || dstRgba.Length < pixelCount * 4)
                return false;

            byte* src = (byte*)srcPtr;

            switch (format)
            {
                case Format.R8G8B8A8Unorm:
                case Format.R8G8B8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 0] / 255.0f;
                        dstRgba[dstIndex + 1] = src[srcIndex + 1] / 255.0f;
                        dstRgba[dstIndex + 2] = src[srcIndex + 2] / 255.0f;
                        dstRgba[dstIndex + 3] = src[srcIndex + 3] / 255.0f;
                    }
                    return true;

                case Format.B8G8R8A8Unorm:
                case Format.B8G8R8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 2] / 255.0f;
                        dstRgba[dstIndex + 1] = src[srcIndex + 1] / 255.0f;
                        dstRgba[dstIndex + 2] = src[srcIndex + 0] / 255.0f;
                        dstRgba[dstIndex + 3] = src[srcIndex + 3] / 255.0f;
                    }
                    return true;

                case Format.R16G16B16A16Unorm:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = p[0] / 65535.0f;
                        dstRgba[dstIndex + 1] = p[1] / 65535.0f;
                        dstRgba[dstIndex + 2] = p[2] / 65535.0f;
                        dstRgba[dstIndex + 3] = p[3] / 65535.0f;
                    }
                    return true;

                case Format.R16G16B16A16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = (float)BitConverter.UInt16BitsToHalf(p[0]);
                        dstRgba[dstIndex + 1] = (float)BitConverter.UInt16BitsToHalf(p[1]);
                        dstRgba[dstIndex + 2] = (float)BitConverter.UInt16BitsToHalf(p[2]);
                        dstRgba[dstIndex + 3] = (float)BitConverter.UInt16BitsToHalf(p[3]);
                    }
                    return true;

                case Format.R32G32B32A32Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 16;
                        int dstIndex = i * 4;
                        float* p = (float*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = p[0];
                        dstRgba[dstIndex + 1] = p[1];
                        dstRgba[dstIndex + 2] = p[2];
                        dstRgba[dstIndex + 3] = p[3];
                    }
                    return true;
            }

            return false;
        }

        private static uint GetColorFormatPixelSize(Format format)
            => format switch
            {
                Format.R8G8B8A8Unorm => 4,
                Format.R8G8B8A8Srgb => 4,
                Format.B8G8R8A8Unorm => 4,
                Format.B8G8R8A8Srgb => 4,
                Format.R16G16B16A16Unorm => 8,
                Format.R16G16B16A16Sfloat => 8,
                Format.R32G32B32A32Sfloat => 16,
                _ => 0,
            };

        // =========== Depth Pixel Reading ===========

        private bool TryReadDepthPixel(in BlitImageInfo source, int x, int y, out float depth)
        {
            depth = 1.0f;

            if (!source.IsValid || !IsDepthOrStencilAspect(source.AspectMask))
                return false;

            uint pixelSize = GetDepthFormatPixelSize(source.Format);
            if (pixelSize == 0)
                return false;

            ulong bufferSize = pixelSize;
            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            try
            {
                using var scope = NewCommandScope();

                ImageLayout preTransferLayout = source.PreferredLayout;
                ImageLayout postTransferLayout = preTransferLayout != ImageLayout.Undefined
                    ? preTransferLayout
                    : (IsDepthOrStencilAspect(source.AspectMask)
                        ? ImageLayout.DepthStencilAttachmentOptimal
                        : ImageLayout.ColorAttachmentOptimal);

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    source.AccessMask,
                    AccessFlags.TransferReadBit,
                    source.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = source.AspectMask.HasFlag(ImageAspectFlags.DepthBit)
                            ? ImageAspectFlags.DepthBit
                            : source.AspectMask,
                        MipLevel = source.MipLevel,
                        BaseArrayLayer = source.BaseArrayLayer,
                        LayerCount = source.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                Api!.CmdCopyImageToBuffer(
                    scope.CommandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    source.AccessMask,
                    PipelineStageFlags.TransferBit,
                    source.StageMask);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            depth = ReadDepthValue(mappedPtr, source.Format);
            Api!.UnmapMemory(device, stagingMemory);
            DestroyBuffer(stagingBuffer, stagingMemory);
            return true;
        }

        // =========== Depth Format Helpers ===========

        /// <summary>
        /// Gets the byte size of a single pixel for a given depth format.
        /// </summary>
        private static uint GetDepthFormatPixelSize(Format format) => format switch
        {
            Format.D16Unorm => 2,
            Format.D32Sfloat => 4,
            Format.D24UnormS8Uint => 4, // 3 bytes depth + 1 byte stencil
            Format.D32SfloatS8Uint => 5, // 4 bytes depth + 1 byte stencil (may be 8 with padding)
            _ => 0, // Unknown format
        };

        /// <summary>
        /// Reads a depth value from a mapped buffer based on the depth format.
        /// </summary>
        private static float ReadDepthValue(void* ptr, Format format)
        {
            return format switch
            {
                Format.D16Unorm => *(ushort*)ptr / 65535f,
                Format.D32Sfloat => *(float*)ptr,
                Format.D24UnormS8Uint => (*(uint*)ptr & 0x00FFFFFF) / 16777215f,
                Format.D32SfloatS8Uint => *(float*)ptr,
                _ => 1.0f,
            };
        }
    }
}
