using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private static Silk.NET.Vulkan.Buffer RequirePreparedBuffer(VkDataBuffer resource, string role)
    {
        if (resource.BufferHandle is { } buffer && buffer.Handle != 0)
            return buffer;

        throw new VulkanPlanPreconditionException(
            $"The prepared {role} resource has no published Vulkan buffer handle.");
    }

    private bool TryBindPreparedGlobalMaterialTextureDescriptorSet(
        CommandBuffer commandBuffer,
        VkRenderProgram program,
        string consumer)
    {
        if (program.PipelineLayout.Handle == 0)
            throw new VulkanPlanPreconditionException(
                $"Bindless material consumer '{consumer}' has no prepared pipeline layout.");

        if (program.DescriptorSetLayouts.Count <= VulkanBindlessMaterialDescriptors.TextureArraySet)
            throw new VulkanPlanPreconditionException(
                $"Bindless material consumer '{consumer}' has no prepared material texture descriptor layout.");

        VulkanBindlessMaterialTextureTableState state = ResourceRuntime.Descriptors.BindlessMaterialTextures;
        DescriptorSet descriptorSet;
        DescriptorSetLayout descriptorSetLayout;
        lock (state.Sync)
        {
            descriptorSet = state.Set;
            descriptorSetLayout = state.SetLayout;
            if (descriptorSet.Handle == 0 || descriptorSetLayout.Handle == 0 || state.PublicationStream.DirtyCount != 0)
            {
                throw new VulkanPlanPreconditionException(
                    $"Bindless material consumer '{consumer}' reached recording before its descriptor table was published.");
            }
        }

        DescriptorSetLayout programMaterialLayout =
            program.DescriptorSetLayouts[(int)VulkanBindlessMaterialDescriptors.TextureArraySet];
        if (programMaterialLayout.Handle != descriptorSetLayout.Handle)
        {
            throw new VulkanPlanPreconditionException(
                $"Bindless material consumer '{consumer}' was prepared with an incompatible material texture descriptor layout.");
        }

        Span<DescriptorSet> sets = stackalloc DescriptorSet[1];
        sets[0] = descriptorSet;
        BindDescriptorSetsTracked(
            commandBuffer,
            PipelineBindPoint.Graphics,
            program.PipelineLayout,
            VulkanBindlessMaterialDescriptors.TextureArraySet,
            sets,
            ReadOnlySpan<uint>.Empty);
        return true;
    }

    private bool TryResolvePreparedBlitImage(
        XRFrameBuffer? frameBuffer,
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
            info = ResolvePreparedSwapchainBlitImage(
                wantColor,
                wantDepth,
                wantStencil,
                in swapchainTarget);
            return true;
        }

        if (frameBuffer.Targets is not { } targets)
        {
            info = default;
            return false;
        }

        int colorIndex = isSource ? ResolvePreparedReadBufferColorAttachmentIndex(readBufferMode) : 0;
        EFrameBufferAttachment colorAttachment =
            (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + colorIndex);
        foreach ((IFrameBufferAttachement target, EFrameBufferAttachment attachment, int mipLevel, int layerIndex) in targets)
        {
            ImageAspectFlags aspect = ImageAspectFlags.None;
            if (IsColorAttachment(attachment) && wantColor)
            {
                if (attachment != colorAttachment)
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
                    _ => ImageAspectFlags.None,
                };
            }
            else if (attachment == EFrameBufferAttachment.DepthAttachment && wantDepth)
                aspect = ImageAspectFlags.DepthBit;
            else if (attachment == EFrameBufferAttachment.StencilAttachment && wantStencil)
                aspect = ImageAspectFlags.StencilBit;

            if (aspect == ImageAspectFlags.None)
                continue;

            info = ResolvePreparedAttachmentBlitImage(target, mipLevel, layerIndex, aspect);
            return true;
        }

        info = default;
        return false;
    }

    private static BlitImageInfo ResolvePreparedSwapchainBlitImage(
        bool wantColor,
        bool wantDepth,
        bool wantStencil,
        in SwapchainRecordingTarget target)
    {
        if (target.Extent.Width == 0 || target.Extent.Height == 0)
            throw new VulkanPlanPreconditionException("The prepared swapchain target has an empty extent.");

        if (wantColor)
        {
            if (target.Image.Handle == 0)
                throw new VulkanPlanPreconditionException("The prepared swapchain color target has no Vulkan image.");

            return new BlitImageInfo(
                target.Image,
                target.ImageFormat,
                ImageAspectFlags.ColorBit,
                0,
                1,
                0,
                target.Extent,
                target.InitialColorLayout,
                PipelineStageFlags.ColorAttachmentOutputBit,
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
        }

        if (!wantDepth && !wantStencil)
            throw new VulkanPlanPreconditionException("A prepared swapchain blit requested no image aspect.");
        if (target.DepthImage.Handle == 0)
            throw new VulkanPlanPreconditionException("The prepared swapchain depth/stencil target has no Vulkan image.");

        ImageAspectFlags aspect = (wantDepth, wantStencil) switch
        {
            (true, true) => target.DepthAspect,
            (true, false) => ImageAspectFlags.DepthBit,
            (false, true) => target.DepthAspect & ImageAspectFlags.StencilBit,
            _ => ImageAspectFlags.None,
        };
        if (aspect == ImageAspectFlags.None)
            throw new VulkanPlanPreconditionException("The prepared swapchain target does not expose the requested depth/stencil aspect.");

        return new BlitImageInfo(
            target.DepthImage,
            target.DepthFormat,
            aspect,
            0,
            1,
            0,
            target.Extent,
            ImageLayout.DepthStencilAttachmentOptimal,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
    }

    private BlitImageInfo ResolvePreparedAttachmentBlitImage(
        IFrameBufferAttachement attachment,
        int mipLevel,
        int layerIndex,
        ImageAspectFlags aspectMask)
    {
        VkObjectBase? wrapper = attachment is GenericRenderObject resource
            ? ResourceRuntime.BackendObjects.Get(resource) as VkObjectBase
            : null;
        if (wrapper is null)
            throw new VulkanPlanPreconditionException("A prepared blit attachment has no published Vulkan wrapper.");

        bool depthOrStencil = IsDepthOrStencilAspect(aspectMask);
        ImageLayout preferredLayout = depthOrStencil
            ? ImageLayout.DepthStencilAttachmentOptimal
            : ImageLayout.ColorAttachmentOptimal;
        PipelineStageFlags stageMask = depthOrStencil
            ? PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit
            : PipelineStageFlags.ColorAttachmentOutputBit;
        AccessFlags accessMask = depthOrStencil
            ? AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
            : AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;

        if (wrapper is VkRenderBuffer renderBuffer)
        {
            if (renderBuffer.Image.Handle == 0)
                throw new VulkanPlanPreconditionException("A prepared render-buffer blit attachment has no published Vulkan image.");
            if (depthOrStencil && (renderBuffer.Aspect & aspectMask) != aspectMask)
                throw new VulkanPlanPreconditionException("A prepared render-buffer blit attachment does not expose the requested aspect.");

            ImageSubresourceRange range = new()
            {
                AspectMask = NormalizeBarrierAspectMask(renderBuffer.Format, aspectMask),
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            };
            ImageLayout layout = TryGetTrackedImageLayout(renderBuffer.Image, range, out ImageLayout tracked)
                ? tracked
                : preferredLayout;
            return new BlitImageInfo(
                renderBuffer.Image,
                renderBuffer.Format,
                aspectMask,
                0,
                1,
                0,
                renderBuffer.ResolveAttachmentExtent(),
                layout,
                stageMask,
                accessMask,
                renderBufferSource: renderBuffer);
        }

        if (attachment is not XRTexture texture || wrapper is not IVkImageDescriptorSource source)
            throw new VulkanPlanPreconditionException("A prepared blit attachment is not backed by a Vulkan image descriptor source.");
        if (source.DescriptorImage.Handle == 0 || !source.IsDescriptorReady)
            throw new VulkanPlanPreconditionException("A prepared texture blit attachment has no published Vulkan image.");
        if (depthOrStencil ? !IsDepthOrStencilFormat(source.DescriptorFormat) : (aspectMask & ImageAspectFlags.ColorBit) == 0)
            throw new VulkanPlanPreconditionException("A prepared texture blit attachment has an incompatible format/aspect.");

        uint mipLevels = Math.Max(source.DescriptorMipLevels, 1u);
        uint resolvedMip = Math.Min((uint)Math.Max(mipLevel, 0), mipLevels - 1u);
        uint availableLayers = Math.Max(source.DescriptorArrayLayers, 1u);
        uint baseLayer = layerIndex < 0 ? 0u : Math.Min((uint)layerIndex, availableLayers - 1u);
        uint layerCount = layerIndex < 0 ? availableLayers : 1u;
        Extent2D extent = source is IVkFrameBufferAttachmentSource attachmentSource &&
                          attachmentSource.TryGetAttachmentExtent((int)resolvedMip, layerIndex, out Extent2D attachmentExtent) &&
                          attachmentExtent.Width != 0 &&
                          attachmentExtent.Height != 0
            ? attachmentExtent
            : new Extent2D(
                Math.Max(attachment.Width >> (int)resolvedMip, 1u),
                Math.Max(attachment.Height >> (int)resolvedMip, 1u));
        ImageSubresourceRange textureRange = new()
        {
            AspectMask = NormalizeBarrierAspectMask(source.DescriptorFormat, aspectMask),
            BaseMipLevel = resolvedMip,
            LevelCount = 1,
            BaseArrayLayer = baseLayer,
            LayerCount = layerCount,
        };
        ImageLayout textureLayout = TryGetTrackedImageLayout(source.DescriptorImage, textureRange, out ImageLayout exactLayout)
            ? exactLayout
            : source.TrackedImageLayout;
        if (textureLayout == ImageLayout.Undefined && !source.UsesAllocatorImage)
            textureLayout = preferredLayout;

        return new BlitImageInfo(
            source.DescriptorImage,
            source.DescriptorFormat,
            aspectMask,
            baseLayer,
            layerCount,
            resolvedMip,
            extent,
            textureLayout,
            stageMask,
            accessMask,
            source);
    }

    private static int ResolvePreparedReadBufferColorAttachmentIndex(EReadBufferMode mode)
        => mode is >= EReadBufferMode.ColorAttachment0 and <= EReadBufferMode.ColorAttachment31
            ? (int)mode - (int)EReadBufferMode.ColorAttachment0
            : 0;

    private static BlitImageInfo RequirePreparedBlitImage(in BlitImageInfo info, string role)
    {
        if (info.Image.Handle != 0 && info.Extent.Width != 0 && info.Extent.Height != 0)
            return info;

        throw new VulkanPlanPreconditionException(
            $"The prepared blit {role} has no valid Vulkan image and extent.");
    }

    private static bool TryBuildPreparedImageBlit(
        BlitImageInfo source,
        BlitImageInfo destination,
        int inX,
        int inY,
        uint inW,
        uint inH,
        int outX,
        int outY,
        uint outW,
        uint outH,
        out ImageBlit region)
    {
        region = default;
        int sourceWidth = (int)Math.Max(source.Extent.Width, 1u);
        int sourceHeight = (int)Math.Max(source.Extent.Height, 1u);
        int destinationWidth = (int)Math.Max(destination.Extent.Width, 1u);
        int destinationHeight = (int)Math.Max(destination.Extent.Height, 1u);
        int srcX0 = ClampPreparedBlitOffset(inX, sourceWidth);
        int srcY0 = ClampPreparedBlitOffset(inY, sourceHeight);
        int srcX1 = ClampPreparedBlitOffset((long)inX + inW, sourceWidth);
        int srcY1 = ClampPreparedBlitOffset((long)inY + inH, sourceHeight);
        int dstX0 = ClampPreparedBlitOffset(outX, destinationWidth);
        int dstY0 = ClampPreparedBlitOffset(outY, destinationHeight);
        int dstX1 = ClampPreparedBlitOffset((long)outX + outW, destinationWidth);
        int dstY1 = ClampPreparedBlitOffset((long)outY + outH, destinationHeight);
        uint layerCount = Math.Min(source.LayerCount, destination.LayerCount);
        if (srcX1 <= srcX0 || srcY1 <= srcY0 || dstX1 <= dstX0 || dstY1 <= dstY0 || layerCount == 0)
            return false;

        region = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(source.AspectMask, source.MipLevel, source.BaseArrayLayer, layerCount),
            DstSubresource = new ImageSubresourceLayers(destination.AspectMask, destination.MipLevel, destination.BaseArrayLayer, layerCount),
        };
        region.SrcOffsets.Element0 = new Offset3D(srcX0, srcY0, 0);
        region.SrcOffsets.Element1 = new Offset3D(srcX1, srcY1, 1);
        region.DstOffsets.Element0 = new Offset3D(dstX0, dstY0, 0);
        region.DstOffsets.Element1 = new Offset3D(dstX1, dstY1, 1);
        return true;
    }

    private static int ClampPreparedBlitOffset(long value, int extent)
        => value <= 0 ? 0 : value >= extent ? extent : (int)value;

    internal unsafe void TransitionPreparedImageForBlit(
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
                "[Vulkan] Skipping blit transition because no live image could be resolved.");
            return;
        }

        info = resolvedInfo;
        ImageSubresourceRange range = new()
        {
            AspectMask = NormalizeBarrierAspectMask(info.Format, info.AspectMask),
            BaseMipLevel = info.MipLevel,
            LevelCount = 1,
            BaseArrayLayer = info.BaseArrayLayer,
            LayerCount = Math.Max(info.LayerCount, 1u),
        };
        if (TryGetRecordedImageAccessState(commandBuffer, info.Image, range, out VulkanImageAccessState recordedState))
        {
            oldLayout = recordedState.Layout;
            srcStage = (PipelineStageFlags)(ulong)recordedState.StageMask;
            srcAccess = (AccessFlags)(ulong)recordedState.AccessMask;
        }
        else
        {
            oldLayout = ResolveLiveBlitOldLayout(info, oldLayout);
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
            Image = info.Image,
            SubresourceRange = range,
        };
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
            &barrier);
    }
}
