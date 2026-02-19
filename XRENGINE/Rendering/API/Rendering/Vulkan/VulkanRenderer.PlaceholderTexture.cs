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
    // ── Fields ───────────────────────────────────────────────────────────
    private Image _placeholderImage;
    private DeviceMemory _placeholderImageMemory;
    private ImageView _placeholderImageView;
    private Sampler _placeholderSampler;
    private bool _placeholderTextureReady;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="DescriptorImageInfo"/> pointing to the 1×1
    /// magenta placeholder texture.  The texture is lazily created on first
    /// access.  If creation fails the returned <c>ImageView</c> handle will
    /// be zero.
    /// </summary>
    internal DescriptorImageInfo GetPlaceholderImageInfo(DescriptorType descriptorType)
    {
        EnsurePlaceholderTexture();
        return new DescriptorImageInfo
        {
            ImageLayout = descriptorType == DescriptorType.StorageImage
                ? ImageLayout.General
                : ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _placeholderImageView,
            Sampler = descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler
                ? _placeholderSampler
                : default,
        };
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

        // ── Image ────────────────────────────────────────────────────────
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
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

        Api.GetImageMemoryRequirements(device, _placeholderImage, out MemoryRequirements memReqs);
        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = FindMemoryType(memReqs.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        if (Api.AllocateMemory(device, ref allocInfo, null, out _placeholderImageMemory) != Result.Success)
        {
            Api.DestroyImage(device, _placeholderImage, null);
            _placeholderImage = default;
            Debug.VulkanWarning("[Vulkan] Failed to allocate placeholder image memory.");
            return;
        }

        Api.BindImageMemory(device, _placeholderImage, _placeholderImageMemory, 0);

        // ── Upload magenta pixel via staging buffer ──────────────────────
        byte* pixel = stackalloc byte[4];
        pixel[0] = 255; // R
        pixel[1] = 0;   // G
        pixel[2] = 255; // B
        pixel[3] = 255; // A

        (Buffer staging, DeviceMemory stagingMem) = CreateBufferRaw(
            pixelSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            UploadBufferMemory(stagingMem, pixelSize, pixel);

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
                        LayerCount = 1,
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
                LayerCount = 1,
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
            barrier.SrcAccessMask = AccessFlags.MemoryWriteBit;
            barrier.DstAccessMask = AccessFlags.MemoryReadBit;
            srcStage = PipelineStageFlags.AllCommandsBit;
            dstStage = PipelineStageFlags.AllCommandsBit;
        }

        Api!.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
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

        if (_placeholderImage.Handle != 0)
        {
            Api!.DestroyImage(device, _placeholderImage, null);
            _placeholderImage = default;
        }

        if (_placeholderImageMemory.Handle != 0)
        {
            Api!.FreeMemory(device, _placeholderImageMemory, null);
            _placeholderImageMemory = default;
        }

        _placeholderTextureReady = false;
    }
}
