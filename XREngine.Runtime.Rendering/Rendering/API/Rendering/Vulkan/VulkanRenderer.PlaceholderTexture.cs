// ──────────────────────────────────────────────────────────────────────────────
// VulkanRenderer.PlaceholderTexture.cs – partial class: Placeholder/Fallback
//                                        1×1 Texture for Missing Bindings
//
// Lazily creates a single 1×1 magenta RGBA8 image + view + sampler that is
// used as a fallback whenever a material does not provide a texture for a
// shader descriptor binding.  This keeps the descriptor set fully valid
// (Vulkan requires every binding to be written) and makes missing textures
// visually obvious in the viewport.
// ──────────────────────────────────────────────────────────────────────────────

using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const uint PlaceholderTextureLayerCount = 6;
    private const ImageCreateFlags PlaceholderCubeCompatibleFlag = (ImageCreateFlags)0x10;

    // ── Fields ───────────────────────────────────────────────────────────
    private Image _placeholderImage;
    private DeviceMemory _placeholderImageMemory;
    private ImageView _placeholderImageView;
    private ImageView _placeholderImageView2DArray;
    private ImageView _placeholderImageViewCube;
    private ImageView _placeholderImageViewCubeArray;
    private Sampler _placeholderSampler;
    private bool _placeholderTextureReady;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="DescriptorImageInfo"/> pointing to the 1×1
    /// magenta placeholder texture.  The texture is lazily created on first
    /// access.  If creation fails the returned <c>ImageView</c> handle will
    /// be zero.
    /// </summary>
    internal DescriptorImageInfo GetPlaceholderImageInfo(DescriptorType descriptorType, ImageViewType? expectedViewType = null)
    {
        EnsurePlaceholderTexture();
        ImageView imageView = GetPlaceholderImageView(expectedViewType);
        return new DescriptorImageInfo
        {
            ImageLayout = descriptorType == DescriptorType.StorageImage
                ? ImageLayout.General
                : ImageLayout.ShaderReadOnlyOptimal,
            ImageView = imageView,
            Sampler = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler
                ? _placeholderSampler
                : default,
        };
    }

    internal Sampler GetPlaceholderSampler()
    {
        EnsurePlaceholderTexture();
        return _placeholderSampler;
    }

    /// <summary>Whether the placeholder texture has been successfully created.</summary>
    internal bool PlaceholderTextureReady => _placeholderTextureReady;

    // ── Creation ─────────────────────────────────────────────────────────

    private void EnsurePlaceholderTexture()
    {
        if (_placeholderTextureReady)
            return;

        const uint width = 1;
        const uint height = 1;
        const ulong pixelSize = 4; // RGBA8
        const ulong uploadSize = pixelSize * PlaceholderTextureLayerCount;

        // ── Image ────────────────────────────────────────────────────────
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            Flags = PlaceholderCubeCompatibleFlag,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = PlaceholderTextureLayerCount,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        if (Api!.CreateImage(device, ref imageInfo, null, out _placeholderImage) != Result.Success)
        {
            Debug.VulkanWarning("[Vulkan] Failed to create placeholder image.");
            return;
        }

        VulkanMemoryAllocation allocation = AllocateImageMemoryWithFallback(_placeholderImage, MemoryPropertyFlags.DeviceLocalBit);
        _imageAllocations[_placeholderImage.Handle] = allocation;
        _placeholderImageMemory = allocation.Memory;

        if (Api.BindImageMemory(device, _placeholderImage, _placeholderImageMemory, allocation.Offset) != Result.Success)
        {
            _imageAllocations.TryRemove(_placeholderImage.Handle, out _);
            FreeMemoryAllocation(allocation);
            Api.DestroyImage(device, _placeholderImage, null);
            _placeholderImage = default;
            Debug.VulkanWarning("[Vulkan] Failed to bind placeholder image memory.");
            return;
        }

        // ── Upload magenta pixel via staging buffer ──────────────────────
        byte* pixel = stackalloc byte[(int)uploadSize];
        for (uint layer = 0; layer < PlaceholderTextureLayerCount; layer++)
        {
            int offset = (int)(layer * pixelSize);
            pixel[offset] = 255;     // R
            pixel[offset + 1] = 0;   // G
            pixel[offset + 2] = 255; // B
            pixel[offset + 3] = 255; // A
        }

        (Buffer staging, DeviceMemory stagingMem) = CreateBufferRaw(
            uploadSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            UploadBufferMemory(staging, stagingMem, uploadSize, pixel);

            using (var scope = NewCommandScope())
            {
                // Undefined → TransferDstOptimal
                TransitionPlaceholderImage(scope.CommandBuffer,
                    ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = PlaceholderTextureLayerCount,
                    },
                    ImageOffset = default,
                    ImageExtent = new Extent3D(width, height, 1),
                };

                Api!.CmdCopyBufferToImage(scope.CommandBuffer, staging, _placeholderImage,
                    ImageLayout.TransferDstOptimal, 1, &copy);

                // TransferDstOptimal → ShaderReadOnlyOptimal
                TransitionPlaceholderImage(scope.CommandBuffer,
                    ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            }
        }
        finally
        {
            DestroyBuffer(staging, stagingMem);
        }

        // ── Image View ───────────────────────────────────────────────────
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _placeholderImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        if (Api.CreateImageView(device, ref viewInfo, null, out _placeholderImageView) != Result.Success)
        {
            Debug.VulkanWarning("[Vulkan] Failed to create placeholder image view.");
            return;
        }

        // ── Sampler ──────────────────────────────────────────────────────
        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipLodBias = 0f,
            AnisotropyEnable = Vk.False,
            MaxAnisotropy = 1f,
            CompareEnable = Vk.False,
            CompareOp = CompareOp.Always,
            MinLod = 0f,
            MaxLod = 0f,
            BorderColor = BorderColor.FloatOpaqueWhite,
            UnnormalizedCoordinates = Vk.False,
        };

        if (Api.CreateSampler(device, ref samplerInfo, null, out _placeholderSampler) != Result.Success)
        {
            Debug.VulkanWarning("[Vulkan] Failed to create placeholder sampler.");
            return;
        }

        _placeholderTextureReady = true;
    }

    // ── Layout Transition ────────────────────────────────────────────────

    private ImageView GetPlaceholderImageView(ImageViewType? expectedViewType)
    {
        if (!_placeholderTextureReady)
            return default;

        return expectedViewType switch
        {
            null or ImageViewType.Type2D => _placeholderImageView,
            ImageViewType.Type2DArray => GetOrCreatePlaceholderImageView(ImageViewType.Type2DArray, ref _placeholderImageView2DArray),
            ImageViewType.TypeCube => GetOrCreatePlaceholderImageView(ImageViewType.TypeCube, ref _placeholderImageViewCube),
            ImageViewType.TypeCubeArray => GetOrCreatePlaceholderImageView(ImageViewType.TypeCubeArray, ref _placeholderImageViewCubeArray),
            _ => default,
        };
    }

    private ImageView GetOrCreatePlaceholderImageView(ImageViewType viewType, ref ImageView cachedView)
    {
        if (cachedView.Handle != 0)
            return cachedView;

        return TryCreatePlaceholderImageView(viewType, out cachedView)
            ? cachedView
            : default;
    }

    private bool TryCreatePlaceholderImageView(ImageViewType viewType, out ImageView imageView)
    {
        uint layerCount = viewType switch
        {
            ImageViewType.Type2D => 1,
            ImageViewType.Type2DArray or ImageViewType.TypeCube or ImageViewType.TypeCubeArray => PlaceholderTextureLayerCount,
            _ => 0,
        };

        if (layerCount == 0)
        {
            imageView = default;
            return false;
        }

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _placeholderImage,
            ViewType = viewType,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = layerCount,
            },
        };

        if (Api!.CreateImageView(device, ref viewInfo, null, out imageView) == Result.Success)
            return true;

        Debug.VulkanWarning($"[Vulkan] Failed to create placeholder {viewType} image view.");
        imageView = default;
        return false;
    }

    private void TransitionPlaceholderImage(CommandBuffer cmd, ImageLayout oldLayout, ImageLayout newLayout)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _placeholderImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = PlaceholderTextureLayerCount,
            },
        };

        PipelineStageFlags srcStage;
        PipelineStageFlags dstStage;

        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }
        else
        {
            // Fault-containment fallback for transitions not yet enumerated.
            // Placeholder textures only transition Undefined→TransferDst→ShaderReadOnly
            // in normal usage, so this branch should not fire in practice.
            barrier.SrcAccessMask = AccessFlags.MemoryWriteBit;
            barrier.DstAccessMask = AccessFlags.MemoryReadBit;
            srcStage = PipelineStageFlags.AllCommandsBit;
            dstStage = PipelineStageFlags.AllCommandsBit;
        }

        CmdPipelineBarrierTracked(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    // ── Destruction ──────────────────────────────────────────────────────

    private void DestroyPlaceholderTexture()
    {
        if (!_placeholderTextureReady)
            return;

        if (_placeholderSampler.Handle != 0)
        {
            Api!.DestroySampler(device, _placeholderSampler, null);
            _placeholderSampler = default;
        }

        if (_placeholderImageView.Handle != 0)
        {
            Api!.DestroyImageView(device, _placeholderImageView, null);
            _placeholderImageView = default;
        }

        if (_placeholderImageView2DArray.Handle != 0)
        {
            Api!.DestroyImageView(device, _placeholderImageView2DArray, null);
            _placeholderImageView2DArray = default;
        }

        if (_placeholderImageViewCube.Handle != 0)
        {
            Api!.DestroyImageView(device, _placeholderImageViewCube, null);
            _placeholderImageViewCube = default;
        }

        if (_placeholderImageViewCubeArray.Handle != 0)
        {
            Api!.DestroyImageView(device, _placeholderImageViewCubeArray, null);
            _placeholderImageViewCubeArray = default;
        }

        if (_placeholderImage.Handle != 0)
        {
            Api!.DestroyImage(device, _placeholderImage, null);
            if (_imageAllocations.TryRemove(_placeholderImage.Handle, out VulkanMemoryAllocation alloc))
                FreeMemoryAllocation(alloc);
            else if (_placeholderImageMemory.Handle != 0)
                Api!.FreeMemory(device, _placeholderImageMemory, null);
            _placeholderImage = default;
        }
        else if (_placeholderImageMemory.Handle != 0)
        {
            Api!.FreeMemory(device, _placeholderImageMemory, null);
        }

        _placeholderImageMemory = default;

        _placeholderTextureReady = false;
    }
}
