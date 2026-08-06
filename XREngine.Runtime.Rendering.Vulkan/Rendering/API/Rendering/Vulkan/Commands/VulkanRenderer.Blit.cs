using System;
using System.Diagnostics;
using System.Numerics;
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
            Extent2D extent,
            ImageLayout preferredLayout,
            PipelineStageFlags stageMask,
            AccessFlags accessMask,
            IVkImageDescriptorSource? descriptorSource = null,
            VkRenderBuffer? renderBufferSource = null,
            SampleCountFlags samples = default)
        {
            public Image Image { get; } = image;
            public Format Format { get; } = format;
            public ImageAspectFlags AspectMask { get; } = aspectMask;
            public uint BaseArrayLayer { get; } = baseArrayLayer;
            public uint LayerCount { get; } = layerCount;
            public uint MipLevel { get; } = mipLevel;
            public Extent2D Extent { get; } = extent;
            public ImageLayout PreferredLayout { get; } = preferredLayout;
            public PipelineStageFlags StageMask { get; } = stageMask;
            public AccessFlags AccessMask { get; } = accessMask;
            public IVkImageDescriptorSource? DescriptorSource { get; } = descriptorSource;
            public VkRenderBuffer? RenderBufferSource { get; } = renderBufferSource;
            public SampleCountFlags Samples { get; } = samples != default
                ? samples
                : descriptorSource?.DescriptorSamples
                    ?? renderBufferSource?.Samples
                    ?? SampleCountFlags.Count1Bit;
            public bool IsValid => Image.Handle != 0;

            public BlitImageInfo WithResolvedState(Image image, ImageLayout preferredLayout, Extent2D extent)
                => new(
                    image,
                    Format,
                    AspectMask,
                    BaseArrayLayer,
                    LayerCount,
                    MipLevel,
                    extent,
                    preferredLayout,
                    StageMask,
                    AccessMask,
                    DescriptorSource,
                    RenderBufferSource,
                    Samples);

            public BlitImageInfo WithLayerCount(uint layerCount)
                => new(
                    Image,
                    Format,
                    AspectMask,
                    BaseArrayLayer,
                    Math.Max(layerCount, 1u),
                    MipLevel,
                    Extent,
                    PreferredLayout,
                    StageMask,
                    AccessMask,
                    DescriptorSource,
                    RenderBufferSource,
                    Samples);
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

            FrameOpContext context = CaptureFrameOpContext();
            int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new BlitOp(
                EnsureValidPassIndex(passIndex, "Blit", context.PassMetadata),
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
                context));
        }

        public override void BlitWithDrawBuffer(
            XRFrameBuffer? inFBO,
            XRFrameBuffer? outFBO,
            uint inW, uint inH,
            uint outW, uint outH,
            EReadBufferMode readBufferMode,
            EReadBufferMode drawBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            // Vulkan does not use GL-style read/draw buffer selection;
            // delegate to the standard Blit path.
            Blit(inFBO, outFBO,
                0, 0, inW, inH,
                0, 0, outW, outH,
                readBufferMode,
                colorBit, depthBit, stencilBit, linearFilter);
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
        {
            if (frameBuffer is null)
            {
                info = ResolveSwapchainBlitImage(swapchainImageIndex, wantColor, wantDepth, wantStencil, in swapchainTarget);
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

            ImageLayout layout = (aspectMask & ImageAspectFlags.ColorBit) != 0
                ? ImageLayout.ColorAttachmentOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;

            PipelineStageFlags stage = (aspectMask & ImageAspectFlags.ColorBit) != 0
                ? PipelineStageFlags.ColorAttachmentOutputBit
                : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;

            AccessFlags access = (aspectMask & ImageAspectFlags.ColorBit) != 0
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
                        vkRenderBuffer.ResolveAttachmentExtent(),
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
            string? resourceName = texture.Name;
            if (string.IsNullOrWhiteSpace(resourceName))
                resourceName = texture.GetDescribingName();

            if (GetOrCreateAPIRenderObject(texture, true) is not { } apiObject)
                return TryResolvePhysicalGroupBlitImage(texture, resourceName, mipLevel, layerIndex, aspectMask, stage, access, out info);

            if (apiObject is VkTextureView textureView)
            {
                // Texture views can outlive backing physical image reallocations.
                // Refresh the descriptor handles in place so readback/blit resolution
                // does not re-enter view destruction/generation while resolving source state.
                textureView.RefreshDescriptorFromViewedTextureIfStale();
                apiObject = textureView;
            }

            if (apiObject is not IVkImageDescriptorSource source)
                return TryResolvePhysicalGroupBlitImage(texture, resourceName, mipLevel, layerIndex, aspectMask, stage, access, out info);

            if (source.DescriptorImage.Handle == 0)
                return TryResolvePhysicalGroupBlitImage(texture, resourceName, mipLevel, layerIndex, aspectMask, stage, access, out info);

            Format format = source.DescriptorFormat;
            if (IsDepthOrStencilAspect(aspectMask))
            {
                if (!IsDepthOrStencilFormat(format))
                    return false;
            }
            else if ((aspectMask & ImageAspectFlags.ColorBit) == 0)
            {
                return false;
            }

            (uint baseArrayLayer, uint blitLayerCount) = ResolveDescriptorTextureBlitLayerRange(
                texture,
                layerIndex,
                source.DescriptorArrayLayers);

            uint mipLevels = Math.Max(source.DescriptorMipLevels, 1u);
            uint resolvedMipLevel = Math.Min((uint)Math.Max(mipLevel, 0), mipLevels - 1u);

            ImageLayout effectiveLayout = layout;
            bool resolvedSubresourceLayout = false;
            if (source is IVkFrameBufferAttachmentSource attachmentSource)
            {
                ImageLayout attachmentLayout = attachmentSource.GetAttachmentTrackedLayout(
                    checked((int)resolvedMipLevel),
                    layerIndex < 0 ? -1 : checked((int)baseArrayLayer));
                if (attachmentLayout != ImageLayout.Undefined)
                {
                    effectiveLayout = attachmentLayout;
                    resolvedSubresourceLayout = true;
                }
            }

            if (!resolvedSubresourceLayout &&
                !string.IsNullOrWhiteSpace(resourceName) &&
                ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group) &&
                group is not null &&
                group.IsAllocated)
            {
                effectiveLayout = group.LastKnownLayout;
            }
            else if (!resolvedSubresourceLayout)
            {
                // For dedicated (non-planner) images, ALWAYS use the texture's own
                // tracked layout so blit transitions emit a correct OldLayout.
                // Newly-created images report Undefined, which is correct â€” the blit
                // pre-transition barrier must use Undefined as OldLayout, not the
                // hardcoded attachment-optimal layout.
                ImageLayout trackedLayout = source.TrackedImageLayout;
                if (trackedLayout != ImageLayout.Undefined || !source.UsesAllocatorImage)
                    effectiveLayout = trackedLayout;
            }

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

        private bool TryResolvePhysicalGroupBlitImage(
            XRTexture texture,
            string? resourceName,
            int mipLevel,
            int layerIndex,
            ImageAspectFlags aspectMask,
            PipelineStageFlags stage,
            AccessFlags access,
            out BlitImageInfo info)
        {
            info = default;
            if (string.IsNullOrWhiteSpace(resourceName) ||
                !ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group) ||
                group is null ||
                !group.IsAllocated ||
                group.Image.Handle == 0)
            {
                return false;
            }

            if (IsDepthOrStencilAspect(aspectMask))
            {
                if (!IsDepthOrStencilFormat(group.Format))
                    return false;
            }
            else if ((aspectMask & ImageAspectFlags.ColorBit) == 0)
            {
                return false;
            }

            uint baseArrayLayer = ResolveBlitBaseArrayLayer(texture, layerIndex);
            uint layerCount = Math.Max(group.Template.Layers, 1u);
            if (baseArrayLayer >= layerCount)
                baseArrayLayer = layerCount - 1u;
            uint blitLayerCount = ResolveBlitLayerCount(texture, layerIndex, layerCount, baseArrayLayer);

            uint mipLevels = Math.Max(group.MipLevels, 1u);
            uint resolvedMipLevel = Math.Min((uint)Math.Max(mipLevel, 0), mipLevels - 1u);
            uint width = Math.Max(group.ResolvedExtent.Width >> (int)resolvedMipLevel, 1u);
            uint height = Math.Max(group.ResolvedExtent.Height >> (int)resolvedMipLevel, 1u);
            ImageLayout layout = group.GetKnownLayout(resolvedMipLevel, 1, baseArrayLayer, blitLayerCount);
            if (layout == ImageLayout.Undefined)
                layout = group.LastKnownLayout;

            info = new BlitImageInfo(
                group.Image,
                group.Format,
                aspectMask,
                baseArrayLayer,
                blitLayerCount,
                resolvedMipLevel,
                new Extent2D(width, height),
                layout,
                stage,
                access,
                samples: group.Samples);
            return info.IsValid;
        }

        private bool TryResolveLiveBlitImage(in BlitImageInfo info, out BlitImageInfo resolved)
        {
            resolved = info;

            if (info.DescriptorSource is { } source)
            {
                if (source is VkObjectBase vkObject && !vkObject.IsActive)
                    vkObject.Generate();

                Image liveImage = source.DescriptorImage;
                if (liveImage.Handle == 0)
                    return false;

                ImageLayout liveLayout;
                if (!TryGetExactTrackedBlitLayout(info, liveImage, out liveLayout))
                {
                    liveLayout = source.UsesAllocatorImage
                        ? ImageLayout.Undefined
                        : ResolveTrackedBlitLayout(source, info);
                    if (liveLayout == ImageLayout.Undefined && !source.UsesAllocatorImage)
                        liveLayout = info.PreferredLayout;
                }

                Extent2D liveExtent = info.Extent;
                if (source is IVkFrameBufferAttachmentSource attachmentSource &&
                    attachmentSource.TryGetAttachmentExtent(Math.Max((int)info.MipLevel, 0), ResolveBlitInfoLayerIndex(info), out Extent2D attachmentExtent))
                {
                    liveExtent = attachmentExtent;
                }

                resolved = info.WithResolvedState(liveImage, liveLayout, liveExtent);
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

                ImageLayout liveLayout;
                if (!TryGetExactTrackedBlitLayout(info, liveImage, out liveLayout))
                    liveLayout = renderBuffer.PhysicalGroup is not null ? ImageLayout.Undefined : info.PreferredLayout;

                resolved = info.WithResolvedState(liveImage, liveLayout, renderBuffer.ResolveAttachmentExtent());
                return true;
            }

            return info.Image.Handle != 0;
        }

        private bool TryGetExactTrackedBlitLayout(in BlitImageInfo info, Image image, out ImageLayout layout)
        {
            layout = ImageLayout.Undefined;
            if (image.Handle == 0)
                return false;

            ImageAspectFlags aspectMask = NormalizeBarrierAspectMask(info.Format, info.AspectMask);
            ImageSubresourceRange range = new()
            {
                AspectMask = aspectMask,
                BaseMipLevel = info.MipLevel,
                LevelCount = 1,
                BaseArrayLayer = info.BaseArrayLayer,
                LayerCount = Math.Max(info.LayerCount, 1u)
            };

            return TryGetTrackedImageLayout(image, range, out layout);
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
                or Format.X8D24UnormPack32
                or Format.S8Uint;

        private static bool IsCombinedDepthStencilFormat(Format format)
            => format is Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint
                or Format.D16UnormS8Uint;

        private static ImageAspectFlags NormalizeBarrierAspectMask(Format format, ImageAspectFlags aspectMask)
        {
            if (!IsDepthOrStencilFormat(format))
            {
                ImageAspectFlags colorMask = aspectMask & ImageAspectFlags.ColorBit;
                return colorMask != ImageAspectFlags.None ? colorMask : ImageAspectFlags.ColorBit;
            }

            ImageAspectFlags supported = format switch
            {
                Format.S8Uint => ImageAspectFlags.StencilBit,
                Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint =>
                    ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                _ => ImageAspectFlags.DepthBit
            };

            ImageAspectFlags normalized = aspectMask & supported;
            if (normalized == ImageAspectFlags.None)
                return supported;

            return IsCombinedDepthStencilFormat(format)
                ? normalized | ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                : normalized;
        }

        // =========== Swapchain Image Resolution ===========

        private BlitImageInfo ResolveSwapchainBlitImage(
            uint swapchainImageIndex,
            bool wantColor,
            bool wantDepth,
            bool wantStencil)
            => ResolveSwapchainBlitImage(swapchainImageIndex, wantColor, wantDepth, wantStencil, default);

        private BlitImageInfo ResolveSwapchainBlitImage(
            uint swapchainImageIndex,
            bool wantColor,
            bool wantDepth,
            bool wantStencil,
            in SwapchainRecordingTarget recordingTarget)
        {
            if (recordingTarget.IsValid)
            {
                if (wantColor)
                {
                    return new BlitImageInfo(
                        recordingTarget.Image,
                        recordingTarget.ImageFormat,
                        ImageAspectFlags.ColorBit,
                        0,
                        1,
                        0,
                        recordingTarget.Extent,
                        ImageLayout.ColorAttachmentOptimal,
                        PipelineStageFlags.ColorAttachmentOutputBit,
                        AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
                }

                if (wantDepth || wantStencil)
                {
                    ImageAspectFlags depthAspect = (wantDepth, wantStencil) switch
                    {
                        (true, true) => recordingTarget.DepthAspect,
                        (true, false) => ImageAspectFlags.DepthBit,
                        (false, true) => (recordingTarget.DepthAspect & ImageAspectFlags.StencilBit) != 0
                            ? ImageAspectFlags.StencilBit
                            : ImageAspectFlags.None,
                        _ => ImageAspectFlags.None
                    };

                    if (depthAspect != ImageAspectFlags.None)
                    {
                        return new BlitImageInfo(
                            recordingTarget.DepthImage,
                            recordingTarget.DepthFormat,
                            depthAspect,
                            0,
                            1,
                            0,
                            recordingTarget.Extent,
                            ImageLayout.DepthStencilAttachmentOptimal,
                            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                            AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
                    }
                }
            }

            if (wantColor && OutputRuntime.Desktop.Images is not null && swapchainImageIndex < OutputRuntime.Desktop.Images.Length)
            {
                return new BlitImageInfo(
                    OutputRuntime.Desktop.Images[swapchainImageIndex],
                    OutputRuntime.Desktop.ImageFormat,
                    ImageAspectFlags.ColorBit,
                    0,
                    1,
                    0,
                    OutputRuntime.Desktop.Extent,
                    ImageLayout.ColorAttachmentOptimal,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
            }

            VulkanSwapchainDepthResources? depth = CurrentSwapchainDepthResources;
            if ((wantDepth || wantStencil) && depth is not null)
            {
                ImageAspectFlags depthAspect = (wantDepth, wantStencil) switch
                {
                    (true, true) => depth.Aspect,
                    (true, false) => ImageAspectFlags.DepthBit,
                    (false, true) => (depth.Aspect & ImageAspectFlags.StencilBit) != 0 ? ImageAspectFlags.StencilBit : ImageAspectFlags.None,
                    _ => ImageAspectFlags.None
                };

                if (depthAspect != ImageAspectFlags.None)
                {
                    return new BlitImageInfo(
                        depth.Image,
                        depth.Format,
                        depthAspect,
                        0,
                        1,
                        0,
                        depth.Extent,
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

        /// <summary>
        /// Resolves an image subresource range from a texture descriptor. A texture-view
        /// descriptor reports its view-local layer count, while <see cref="BlitImageInfo"/>
        /// addresses layers in the backing image. Preserve the view's absolute base layer
        /// before applying the descriptor-layer bound so a one-layer view of backing layer
        /// one cannot be silently collapsed to backing layer zero.
        /// </summary>
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

            uint remainingLayers = Math.Max(availableLayers - Math.Min(baseArrayLayer, availableLayers - 1u), 1u);
            uint requestedLayers = texture is XRTextureViewBase view
                ? Math.Max(view.NumLayers, 1u)
                : remainingLayers;
            return Math.Max(Math.Min(requestedLayers, remainingLayers), 1u);
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

        private static Extent2D ResolveTextureBlitExtent(
            XRTexture texture,
            IVkImageDescriptorSource source,
            int mipLevel,
            int layerIndex,
            uint resolvedMipLevel)
        {
            if (source is IVkFrameBufferAttachmentSource attachmentSource &&
                attachmentSource.TryGetAttachmentExtent(Math.Max(mipLevel, 0), layerIndex, out Extent2D resolvedExtent) &&
                resolvedExtent.Width > 0 &&
                resolvedExtent.Height > 0)
            {
                return resolvedExtent;
            }

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

        private static bool TryBuildImageBlit(
            BlitImageInfo source,
            BlitImageInfo destination,
            int inX, int inY, uint inW, uint inH,
            int outX, int outY, uint outW, uint outH,
            out ImageBlit region)
        {
            region = default;

            int sourceWidth = (int)Math.Max(source.Extent.Width, 1u);
            int sourceHeight = (int)Math.Max(source.Extent.Height, 1u);
            int destinationWidth = (int)Math.Max(destination.Extent.Width, 1u);
            int destinationHeight = (int)Math.Max(destination.Extent.Height, 1u);

            int srcX0 = ClampBlitOffset(inX, sourceWidth);
            int srcY0 = ClampBlitOffset(inY, sourceHeight);
            int srcX1 = ClampBlitOffset((long)inX + inW, sourceWidth);
            int srcY1 = ClampBlitOffset((long)inY + inH, sourceHeight);

            int dstX0 = ClampBlitOffset(outX, destinationWidth);
            int dstY0 = ClampBlitOffset(outY, destinationHeight);
            int dstX1 = ClampBlitOffset((long)outX + outW, destinationWidth);
            int dstY1 = ClampBlitOffset((long)outY + outH, destinationHeight);

            if (srcX1 <= srcX0 || srcY1 <= srcY0 || dstX1 <= dstX0 || dstY1 <= dstY0)
                return false;

            uint commonLayerCount = Math.Min(source.LayerCount, destination.LayerCount);
            if (commonLayerCount == 0)
                return false;

            region = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = source.AspectMask,
                    MipLevel = source.MipLevel,
                    BaseArrayLayer = source.BaseArrayLayer,
                    LayerCount = commonLayerCount
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = destination.AspectMask,
                    MipLevel = destination.MipLevel,
                    BaseArrayLayer = destination.BaseArrayLayer,
                    LayerCount = commonLayerCount
                }
            };

            region.SrcOffsets.Element0 = new Offset3D { X = srcX0, Y = srcY0, Z = 0 };
            region.SrcOffsets.Element1 = new Offset3D { X = srcX1, Y = srcY1, Z = 1 };
            region.DstOffsets.Element0 = new Offset3D { X = dstX0, Y = dstY0, Z = 0 };
            region.DstOffsets.Element1 = new Offset3D { X = dstX1, Y = dstY1, Z = 1 };

            return true;
        }

        private static int ClampBlitOffset(long value, int extent)
        {
            if (value <= 0)
                return 0;

            if (value >= extent)
                return extent;

            return (int)value;
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
                    "[Vulkan] Skipping blit transition â€” could not resolve a live image handle.");
                return;
            }

            // Guard against stale or destroyed image handles that passed the zero-check
            // but are no longer valid Vulkan objects (e.g. after physical group reallocation).
            if (resolvedInfo.Image.Handle == 0)
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.TransitionForBlit.NullImage",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping blit transition â€” image handle is null.");
                return;
            }

            ImageAspectFlags barrierAspectMask = NormalizeBarrierAspectMask(resolvedInfo.Format, resolvedInfo.AspectMask);
            ImageSubresourceRange transitionRange = new()
            {
                AspectMask = barrierAspectMask,
                BaseMipLevel = resolvedInfo.MipLevel,
                LevelCount = 1,
                BaseArrayLayer = resolvedInfo.BaseArrayLayer,
                LayerCount = resolvedInfo.LayerCount
            };
            if (TryGetRecordedImageAccessState(
                commandBuffer,
                resolvedInfo.Image,
                transitionRange,
                out VulkanImageAccessState recordedState))
            {
                oldLayout = recordedState.Layout;
                srcStage = (PipelineStageFlags)(ulong)recordedState.StageMask;
                srcAccess = (AccessFlags)(ulong)recordedState.AccessMask;
            }
            else
            {
                oldLayout = ResolveLiveBlitOldLayout(resolvedInfo, oldLayout);
            }

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
                SubresourceRange = transitionRange
            };

            ImageMemoryBarrier* barrierPtr = stackalloc ImageMemoryBarrier[1];
            barrierPtr[0] = barrier;

            CmdPipelineBarrierTracked(
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

        private ImageLayout ResolveLiveBlitOldLayout(in BlitImageInfo info, ImageLayout requestedOldLayout)
        {
            if (TryGetExactTrackedBlitLayout(info, info.Image, out ImageLayout exactLayout))
                return exactLayout;

            if (info.DescriptorSource is { } source)
            {
                if (source.UsesAllocatorImage)
                    return ImageLayout.Undefined;

                ImageLayout trackedLayout = ResolveTrackedBlitLayout(source, info);
                if (trackedLayout != ImageLayout.Undefined)
                    return trackedLayout;
            }

            if (info.RenderBufferSource?.PhysicalGroup is not null)
                return ImageLayout.Undefined;

            return requestedOldLayout;
        }

        private static ImageLayout ResolveTrackedBlitLayout(IVkImageDescriptorSource source, in BlitImageInfo info)
        {
            if (source is IVkFrameBufferAttachmentSource attachmentSource)
            {
                ImageLayout attachmentLayout = attachmentSource.GetAttachmentTrackedLayout(
                    checked((int)info.MipLevel),
                    ResolveBlitInfoLayerIndex(info));
                if (attachmentLayout != ImageLayout.Undefined)
                    return attachmentLayout;
            }

            return source.TrackedImageLayout;
        }

        private static int ResolveBlitInfoLayerIndex(in BlitImageInfo info)
            => info.LayerCount == 1u
                ? checked((int)info.BaseArrayLayer)
                : -1;

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

            // Swapchain images transition between a known set of layouts;
            // use precise stages instead of AllCommandsBit.
            PipelineStageFlags swapSrcStage = oldLayout switch
            {
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
                ImageLayout.PresentSrcKhr => PipelineStageFlags.BottomOfPipeBit,
                _ => PipelineStageFlags.ColorAttachmentOutputBit,
            };
            PipelineStageFlags swapDstStage = newLayout switch
            {
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
                ImageLayout.PresentSrcKhr => PipelineStageFlags.BottomOfPipeBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
                _ => PipelineStageFlags.BottomOfPipeBit,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                swapSrcStage,
                swapDstStage,
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                1,
                barrierPtr);
        }

    }
}
