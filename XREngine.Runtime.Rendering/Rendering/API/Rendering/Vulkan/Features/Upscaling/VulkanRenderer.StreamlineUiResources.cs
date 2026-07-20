using System;
using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private Image[]? _streamlineUiImages;
    private DeviceMemory[]? _streamlineUiImageMemories;
    private ImageView[]? _streamlineUiImageViews;
    private bool[]? _streamlineUiImagesInitialized;

    /// <summary>
    /// Creates one transparent UI color/alpha target per proxy-swapchain image.
    /// DLSS-G consumes these targets at present time to composite UI over generated frames.
    /// </summary>
    private void CreateStreamlineUiResources()
    {
        if (!_streamlineFrameGenerationSwapchainActive || swapChainImages is null)
            return;

        int imageCount = swapChainImages.Length;
        _streamlineUiImages = new Image[imageCount];
        _streamlineUiImageMemories = new DeviceMemory[imageCount];
        _streamlineUiImageViews = new ImageView[imageCount];
        _streamlineUiImagesInitialized = new bool[imageCount];

        try
        {
            for (int i = 0; i < imageCount; i++)
                CreateStreamlineUiResource(i);
        }
        catch
        {
            DestroyStreamlineUiResources();
            throw;
        }
    }

    private void CreateStreamlineUiResource(int imageIndex)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(swapChainExtent.Width, swapChainExtent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = swapChainImageFormat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.ColorAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        if (CreateVulkanImageTracked(ref imageInfo, out Image image, $"Streamline.UIColorAndAlpha[{imageIndex}]") != Result.Success)
            throw new InvalidOperationException($"Failed to create Streamline UI color/alpha image {imageIndex}.");

        ClearTrackedImageLayouts(image);
        VulkanMemoryAllocation allocation = AllocateImageMemoryWithFallback(image, MemoryPropertyFlags.DeviceLocalBit);
        _imageAllocations[image.Handle] = allocation;

        if (Api!.BindImageMemory(device, image, allocation.Memory, allocation.Offset) != Result.Success)
        {
            _imageAllocations.TryRemove(image.Handle, out _);
            DestroyVulkanImageImmediateTracked(image, "Streamline.UIColorAndAlpha.BindFailure");
            FreeMemoryAllocation(allocation);
            throw new InvalidOperationException($"Failed to bind Streamline UI color/alpha image memory {imageIndex}.");
        }

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = swapChainImageFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (Api.CreateImageView(device, ref viewInfo, null, out ImageView view) != Result.Success)
        {
            _imageAllocations.TryRemove(image.Handle, out _);
            DestroyVulkanImageImmediateTracked(image, "Streamline.UIColorAndAlpha.ViewFailure");
            FreeMemoryAllocation(allocation);
            throw new InvalidOperationException($"Failed to create Streamline UI color/alpha image view {imageIndex}.");
        }

        TrackLiveImageView(view, in viewInfo, $"Streamline.UIColorAndAlpha[{imageIndex}]");
        SetDebugObjectName(ObjectType.Image, image.Handle, $"Streamline.UIColorAndAlpha[{imageIndex}]");
        SetDebugObjectName(ObjectType.ImageView, view.Handle, $"Streamline.UIColorAndAlphaView[{imageIndex}]");

        _streamlineUiImages![imageIndex] = image;
        _streamlineUiImageMemories![imageIndex] = allocation.Memory;
        _streamlineUiImageViews![imageIndex] = view;
    }

    private void DestroyStreamlineUiResources()
    {
        if (_streamlineUiImages is not null)
        {
            for (int i = 0; i < _streamlineUiImages.Length; i++)
            {
                RetireImageResources(new RetiredImageResources(
                    _streamlineUiImages[i],
                    _streamlineUiImageMemories is not null && i < _streamlineUiImageMemories.Length
                        ? _streamlineUiImageMemories[i]
                        : default,
                    _streamlineUiImageViews is not null && i < _streamlineUiImageViews.Length
                        ? _streamlineUiImageViews[i]
                        : default,
                    [],
                    default,
                    0));
            }
        }

        _streamlineUiImages = null;
        _streamlineUiImageMemories = null;
        _streamlineUiImageViews = null;
        _streamlineUiImagesInitialized = null;
    }

    private bool TryGetStreamlineUiImage(uint imageIndex, out VulkanStreamlineImage image)
    {
        image = default;
        if (_streamlineUiImages is null ||
            _streamlineUiImageMemories is null ||
            _streamlineUiImageViews is null ||
            imageIndex >= _streamlineUiImages.Length ||
            imageIndex >= _streamlineUiImageMemories.Length ||
            imageIndex >= _streamlineUiImageViews.Length)
        {
            return false;
        }

        Image nativeImage = _streamlineUiImages[imageIndex];
        ImageView view = _streamlineUiImageViews[imageIndex];
        if (nativeImage.Handle == 0 || view.Handle == 0)
            return false;

        image = new VulkanStreamlineImage(
            nativeImage,
            _streamlineUiImageMemories[imageIndex],
            view,
            ImageLayout.General,
            swapChainImageFormat,
            ImageUsageFlags.ColorAttachmentBit |
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.TransferSrcBit |
            ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit,
            swapChainExtent.Width,
            swapChainExtent.Height,
            null);
        return true;
    }

    /// <summary>
    /// Clears the DLSS-G UI surface for the acquired image before any late UI overlays.
    /// This guarantees a valid transparent resource even for frames with no UI draws.
    /// </summary>
    private bool TryPrepareStreamlineUiImage(
        CommandBuffer commandBuffer,
        uint imageIndex,
        out VulkanStreamlineImage image)
    {
        if (!TryGetStreamlineUiImage(imageIndex, out image) ||
            !TryGetStreamlineUiAttachment(imageIndex, out Image nativeImage, out _, out ImageLayout oldLayout))
        {
            return false;
        }

        TransitionStreamlineUiImage(
            commandBuffer,
            nativeImage,
            oldLayout,
            ImageLayout.TransferDstOptimal);

        ClearColorValue transparent = new(0f, 0f, 0f, 0f);
        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        CmdClearColorImageTracked(
            commandBuffer,
            nativeImage,
            ImageLayout.TransferDstOptimal,
            ref transparent,
            1,
            ref range);

        TransitionStreamlineUiImage(
            commandBuffer,
            nativeImage,
            ImageLayout.TransferDstOptimal,
            ImageLayout.General);
        MarkStreamlineUiImageInitialized(imageIndex);
        return true;
    }

    private bool TryGetStreamlineUiAttachment(uint imageIndex, out Image image, out ImageView view, out ImageLayout oldLayout)
    {
        image = default;
        view = default;
        oldLayout = ImageLayout.Undefined;
        if (_streamlineUiImages is null ||
            _streamlineUiImageViews is null ||
            _streamlineUiImagesInitialized is null ||
            imageIndex >= _streamlineUiImages.Length ||
            imageIndex >= _streamlineUiImageViews.Length ||
            imageIndex >= _streamlineUiImagesInitialized.Length)
        {
            return false;
        }

        image = _streamlineUiImages[imageIndex];
        view = _streamlineUiImageViews[imageIndex];
        oldLayout = _streamlineUiImagesInitialized[imageIndex]
            ? ImageLayout.General
            : ImageLayout.Undefined;
        return image.Handle != 0 && view.Handle != 0;
    }

    private void MarkStreamlineUiImageInitialized(uint imageIndex)
    {
        if (_streamlineUiImagesInitialized is not null && imageIndex < _streamlineUiImagesInitialized.Length)
            _streamlineUiImagesInitialized[imageIndex] = true;
    }

    private void TransitionStreamlineUiImage(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = ResolveStreamlineAccessMask(oldLayout),
            DstAccessMask = ResolveStreamlineAccessMask(newLayout),
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
                LayerCount = 1,
            },
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            ResolveStreamlinePipelineStage(oldLayout),
            ResolveStreamlinePipelineStage(newLayout),
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }
}
