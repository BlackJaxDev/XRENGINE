using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials.Textures;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal abstract class VkImageBackedTexture<TTexture> : VkTexture<TTexture> where TTexture : XRTexture
    {
        private readonly Dictionary<AttachmentViewKey, ImageView> _attachmentViews = new();
        private TextureLayout _layout;
        private bool _layoutInitialized;

        private Image _image;
        private DeviceMemory _memory;
        private ImageView _view;
        private Sampler _sampler;
        private bool _ownsImageMemory;
        private VulkanPhysicalImageGroup? _physicalGroup;
        private Extent3D? _extentOverride;
        private Format? _formatOverride;
        private uint? _arrayLayersOverride;
        private uint? _mipLevelsOverride;
        protected ImageLayout _currentImageLayout = ImageLayout.Undefined;

        /// <summary>
        /// Tracks the currently allocated GPU memory size for this texture in bytes.
        /// </summary>
        private long _allocatedVRAMBytes = 0;

        public override bool IsGenerated { get; }

        internal Image Image => _image;
        internal ImageView View => _view;
        internal Sampler Sampler => _sampler;
        internal bool UsesAllocatorImage => _physicalGroup is not null;

        protected internal Format ResolvedFormat => _formatOverride ?? Format;
        protected Extent3D ResolvedExtent => _extentOverride ?? _layout.Extent;
        protected uint ResolvedArrayLayers => _arrayLayersOverride ?? _layout.ArrayLayers;
        protected uint ResolvedMipLevels => _mipLevelsOverride ?? _layout.MipLevels;
        internal SampleCountFlags SampleCount => SampleCountFlags.Count1Bit;

        public bool CreateSampler { get; set; } = true;
        public Format Format { get; set; } = Format.R8G8B8A8Unorm;
        public MemoryPropertyFlags MemoryProperties { get; set; } = MemoryPropertyFlags.DeviceLocalBit;
        public ImageTiling Tiling { get; set; } = ImageTiling.Optimal;
        public ImageUsageFlags Usage { get; set; }
        public ImageAspectFlags AspectFlags { get; set; }
        public ImageViewType DefaultViewType { get; set; }

        public SamplerMipmapMode MipmapMode { get; set; } = SamplerMipmapMode.Linear;
        public SamplerAddressMode UWrap { get; set; } = SamplerAddressMode.Repeat;
        public SamplerAddressMode VWrap { get; set; } = SamplerAddressMode.Repeat;
        public SamplerAddressMode WWrap { get; set; } = SamplerAddressMode.Repeat;
        public Filter MinFilter { get; set; } = Filter.Linear;
        public Filter MagFilter { get; set; } = Filter.Linear;
        public bool UseAniso { get; set; } = true;

        protected VkImageBackedTexture(VulkanRenderer api, TTexture data) : base(api, data)
        {
            Usage = DefaultUsage;
            AspectFlags = DefaultAspect;
            DefaultViewType = DefaultImageViewType;
        }

        protected override void LinkData()
        {
            Data.PushDataRequested += OnPushDataRequested;
            Data.GenerateMipmapsRequested += OnGenerateMipmapsRequested;
            SubscribeResizeEvents();
        }

        protected override void UnlinkData()
        {
            Data.PushDataRequested -= OnPushDataRequested;
            Data.GenerateMipmapsRequested -= OnGenerateMipmapsRequested;
            UnsubscribeResizeEvents();
        }

        protected override uint CreateObjectInternal()
        {
            RefreshLayout();
            AcquireImageHandle();
            CreateImageView(default);
            if (CreateSampler)
                CreateSamplerInternal();
            return CacheObject(this);
        }

        protected override void DeleteObjectInternal()
        {
            DestroySampler();
            DestroyAllViews();

            if (_ownsImageMemory)
            {
                // Track VRAM deallocation
                if (_allocatedVRAMBytes > 0)
                {
                    Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                if (_image.Handle != 0)
                    Api!.DestroyImage(Device, _image, null);
                if (_memory.Handle != 0)
                    Api!.FreeMemory(Device, _memory, null);
            }

            _image = default;
            _memory = default;
            _physicalGroup = null;
            _extentOverride = null;
            _formatOverride = null;
            _arrayLayersOverride = null;
            _mipLevelsOverride = null;
            _currentImageLayout = ImageLayout.Undefined;
        }

        private void RefreshLayout()
        {
            _layout = NormalizeLayout(DescribeTexture());
            _layoutInitialized = true;
        }

        private static TextureLayout NormalizeLayout(TextureLayout layout)
        {
            Extent3D extent = new(
                Math.Max(layout.Extent.Width, 1u),
                Math.Max(layout.Extent.Height, 1u),
                Math.Max(layout.Extent.Depth, 1u));

            uint layers = Math.Max(layout.ArrayLayers, 1u);
            uint mips = Math.Max(layout.MipLevels, 1u);
            return new TextureLayout(extent, layers, mips);
        }

        private void AcquireImageHandle()
        {
            if (!_layoutInitialized)
                RefreshLayout();

            if (TryResolvePhysicalGroup(out VulkanPhysicalImageGroup? group))
            {
                _physicalGroup = group;
                _image = group!.Image;
                _memory = group.Memory;
                _extentOverride = group.ResolvedExtent;
                _formatOverride = group.Format;
                _arrayLayersOverride = Math.Max(group.Template.Layers, 1u);
                _mipLevelsOverride = 1;
                _ownsImageMemory = false;
                _currentImageLayout = ImageLayout.Undefined;
                return;
            }

            CreateDedicatedImage();
            _physicalGroup = null;
            _extentOverride = null;
            _formatOverride = null;
            _arrayLayersOverride = null;
            _mipLevelsOverride = null;
            _ownsImageMemory = true;
            _currentImageLayout = ImageLayout.Undefined;
        }

        private void CreateDedicatedImage()
        {
            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                Flags = AdditionalImageFlags,
                ImageType = TextureImageType,
                Extent = ResolvedExtent,
                MipLevels = ResolvedMipLevels,
                ArrayLayers = ResolvedArrayLayers,
                Format = ResolvedFormat,
                Tiling = Tiling,
                InitialLayout = ImageLayout.Undefined,
                Usage = Usage,
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Image* imagePtr = &_image)
            {
                Result result = Api!.CreateImage(Device, ref imageInfo, null, imagePtr);
                if (result != Result.Success)
                    throw new Exception($"Failed to create Vulkan image for texture '{ResolveLogicalResourceName() ?? Data.Name ?? "<unnamed>"}'. Result={result}.");
            }

            Api!.GetImageMemoryRequirements(Device, _image, out MemoryRequirements memRequirements);

            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memRequirements.Size,
                MemoryTypeIndex = Renderer.FindMemoryType(memRequirements.MemoryTypeBits, MemoryProperties),
            };

            fixed (DeviceMemory* memPtr = &_memory)
            {
                Renderer.AllocateMemory(allocInfo, memPtr);
            }

            if (Api!.BindImageMemory(Device, _image, _memory, 0) != Result.Success)
                throw new Exception("Failed to bind memory for texture image.");

            // Track VRAM allocation
            _allocatedVRAMBytes = (long)memRequirements.Size;
            Engine.Rendering.Stats.AddTextureAllocation(_allocatedVRAMBytes);
        }

        private void CreateImageView(AttachmentViewKey key)
        {
            DestroyView(ref _view);

            AttachmentViewKey descriptor = key == default
                ? new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, DefaultViewType, AspectFlags)
                : key;

            _view = CreateView(descriptor);
        }

        private ImageView CreateView(AttachmentViewKey descriptor)
        {
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = descriptor.ViewType,
                Format = ResolvedFormat,
                Components = new ComponentMapping(ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = descriptor.AspectMask,
                    BaseMipLevel = descriptor.BaseMipLevel,
                    LevelCount = descriptor.LevelCount,
                    BaseArrayLayer = descriptor.BaseArrayLayer,
                    LayerCount = descriptor.LayerCount,
                }
            };

            if (Api!.CreateImageView(Device, ref viewInfo, null, out ImageView created) != Result.Success)
                throw new Exception("Failed to create image view.");
            return created;
        }

        private void DestroyView(ref ImageView view)
        {
            if (view.Handle != 0)
            {
                Api!.DestroyImageView(Device, view, null);
                view = default;
            }
        }

        private void DestroySampler()
        {
            if (_sampler.Handle != 0)
            {
                Api!.DestroySampler(Device, _sampler, null);
                _sampler = default;
            }
        }

        private void DestroyAllViews()
        {
            DestroyView(ref _view);
            foreach ((_, ImageView attachmentView) in _attachmentViews)
            {
                if (attachmentView.Handle != 0)
                    Api!.DestroyImageView(Device, attachmentView, null);
            }
            _attachmentViews.Clear();
        }

        private void CreateSamplerInternal()
        {
            DestroySampler();

            var anisotropyEnable = Vk.False;
            float maxAnisotropy = 1f;
            bool allowAniso = UseAniso && Renderer.SamplerAnisotropyEnabled;
            if (allowAniso)
            {
                Api!.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties props);
                if (props.Limits.MaxSamplerAnisotropy > 1f)
                {
                    anisotropyEnable = Vk.True;
                    maxAnisotropy = MathF.Min(props.Limits.MaxSamplerAnisotropy, 16f);
                }
            }

            SamplerCreateInfo samplerInfo = new()
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = MagFilter,
                MinFilter = MinFilter,
                AddressModeU = UWrap,
                AddressModeV = VWrap,
                AddressModeW = WWrap,
                AnisotropyEnable = anisotropyEnable,
                MaxAnisotropy = maxAnisotropy,
                BorderColor = BorderColor.IntOpaqueBlack,
                UnnormalizedCoordinates = Vk.False,
                CompareEnable = Vk.False,
                CompareOp = CompareOp.Always,
                MipmapMode = MipmapMode,
                MipLodBias = 0f,
                MinLod = 0f,
                MaxLod = ResolvedMipLevels,
            };

            if (Api!.CreateSampler(Device, ref samplerInfo, null, out _sampler) != Result.Success)
                throw new Exception("Failed to create sampler.");
        }

        internal ImageView GetAttachmentView(int mipLevel, int layerIndex)
        {
            AttachmentViewKey key = BuildAttachmentViewKey(mipLevel, layerIndex);
            if (key == default)
                return _view;

            if (!_attachmentViews.TryGetValue(key, out ImageView cached))
            {
                cached = CreateView(key);
                _attachmentViews[key] = cached;
            }

            return cached;
        }

        protected virtual AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (mipLevel <= 0 && layerIndex < 0)
                return default;

            uint baseMip = (uint)Math.Max(mipLevel, 0);
            return new AttachmentViewKey(baseMip, 1, 0, 1, ImageViewType.Type2D, AspectFlags);
        }

        protected void TransitionImageLayout(ImageLayout oldLayout, ImageLayout newLayout)
        {
            AssembleTransitionImageLayout(oldLayout, newLayout, out ImageMemoryBarrier barrier, out PipelineStageFlags src, out PipelineStageFlags dst);
            using var scope = Renderer.NewCommandScope();
            Api!.CmdPipelineBarrier(scope.CommandBuffer, src, dst, 0, 0, null, 0, null, 1, ref barrier);
            _currentImageLayout = newLayout;
        }

        private void AssembleTransitionImageLayout(
            ImageLayout oldLayout,
            ImageLayout newLayout,
            out ImageMemoryBarrier barrier,
            out PipelineStageFlags sourceStage,
            out PipelineStageFlags destinationStage)
        {
            barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = AspectFlags,
                    BaseMipLevel = 0,
                    LevelCount = ResolvedMipLevels,
                    BaseArrayLayer = 0,
                    LayerCount = ResolvedArrayLayers,
                }
            };

            if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
            {
                barrier.SrcAccessMask = 0;
                barrier.DstAccessMask = AccessFlags.TransferWriteBit;
                sourceStage = PipelineStageFlags.TopOfPipeBit;
                destinationStage = PipelineStageFlags.TransferBit;
            }
            else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
            {
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                sourceStage = PipelineStageFlags.TransferBit;
                destinationStage = PipelineStageFlags.FragmentShaderBit;
            }
            else
            {
                barrier.SrcAccessMask = AccessFlags.MemoryWriteBit;
                barrier.DstAccessMask = AccessFlags.MemoryReadBit;
                sourceStage = PipelineStageFlags.AllCommandsBit;
                destinationStage = PipelineStageFlags.AllCommandsBit;
            }
        }

        protected void CopyBufferToImage(Buffer buffer, uint mipLevel, uint baseArrayLayer, uint layerCount, Extent3D extent)
        {
            BufferImageCopy region = new()
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = AspectFlags,
                    MipLevel = mipLevel,
                    BaseArrayLayer = baseArrayLayer,
                    LayerCount = layerCount,
                },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = extent,
            };

            using var scope = Renderer.NewCommandScope();
            Api!.CmdCopyBufferToImage(scope.CommandBuffer, buffer, _image, ImageLayout.TransferDstOptimal, 1, ref region);
        }

        public DescriptorImageInfo CreateImageInfo() => new()
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _view,
            Sampler = _sampler,
        };

        protected virtual ImageUsageFlags DefaultUsage => ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit;
        protected virtual ImageAspectFlags DefaultAspect => ImageAspectFlags.ColorBit;
        protected virtual ImageViewType DefaultImageViewType => ImageViewType.Type2D;
        protected virtual ImageType TextureImageType => ImageType.Type2D;
        protected virtual ImageCreateFlags AdditionalImageFlags => 0;

        protected abstract TextureLayout DescribeTexture();

        protected internal readonly record struct TextureLayout(Extent3D Extent, uint ArrayLayers, uint MipLevels);

        protected internal readonly record struct AttachmentViewKey(uint BaseMipLevel, uint LevelCount, uint BaseArrayLayer, uint LayerCount, ImageViewType ViewType, ImageAspectFlags AspectMask);

        protected virtual void PushTextureData()
        {
            Debug.LogWarning($"{GetType().Name} does not implement texture data uploads yet.");
        }

        protected virtual void GenerateMipmapsGPU()
            => GenerateMipmapsWithBlit();

        private void OnPushDataRequested()
        {
            if (Engine.InvokeOnMainThread(OnPushDataRequested, "VkTexture2D.PushData"))
                return;

            PushTextureData();
        }

        private void OnGenerateMipmapsRequested()
        {
            if (Engine.InvokeOnMainThread(OnGenerateMipmapsRequested, "VkTexture2D.GenerateMipmaps"))
                return;

            GenerateMipmapsGPU();
        }

        private void SubscribeResizeEvents()
        {
            switch (Data)
            {
                case XRTexture2D tex2D:
                    tex2D.Resized += OnTextureResized;
                    break;
                case XRTexture2DArray texArray:
                    texArray.Resized += OnTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized += OnTextureResized;
                    break;
                case XRTexture3D tex3D:
                    tex3D.Resized += OnTextureResized;
                    break;
            }
        }

        private void UnsubscribeResizeEvents()
        {
            switch (Data)
            {
                case XRTexture2D tex2D:
                    tex2D.Resized -= OnTextureResized;
                    break;
                case XRTexture2DArray texArray:
                    texArray.Resized -= OnTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized -= OnTextureResized;
                    break;
                case XRTexture3D tex3D:
                    tex3D.Resized -= OnTextureResized;
                    break;
            }
        }

        private void OnTextureResized()
        {
            Destroy();
            _layoutInitialized = false;
            _currentImageLayout = ImageLayout.Undefined;
        }

        protected bool TryCreateStagingBuffer(DataSource? data, out Buffer buffer, out DeviceMemory memory)
        {
            if (data is null || data.Length == 0)
            {
                buffer = default;
                memory = default;
                return false;
            }

            (buffer, memory) = Renderer.CreateBuffer(
                data.Length,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                data.Address);
            return true;
        }

        protected void DestroyStagingBuffer(Buffer buffer, DeviceMemory memory)
            => Renderer.DestroyBuffer(buffer, memory);

        protected void GenerateMipmapsWithBlit()
        {
            Generate();

            if (ResolvedMipLevels <= 1)
            {
                if (_currentImageLayout != ImageLayout.ShaderReadOnlyOptimal)
                    TransitionImageLayout(_currentImageLayout, ImageLayout.ShaderReadOnlyOptimal);
                return;
            }

            Api!.GetPhysicalDeviceFormatProperties(PhysicalDevice, ResolvedFormat, out FormatProperties props);
            if ((props.OptimalTilingFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) == 0)
            {
                Debug.LogWarning($"Texture format '{ResolvedFormat}' does not support linear blitting; skipping mipmap generation.");
                TransitionImageLayout(_currentImageLayout, ImageLayout.ShaderReadOnlyOptimal);
                return;
            }

            if (_currentImageLayout != ImageLayout.TransferDstOptimal)
                TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            using var scope = Renderer.NewCommandScope();
            CommandBuffer cmd = scope.CommandBuffer;

            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                Image = Image,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = AspectFlags,
                    BaseArrayLayer = 0,
                    LayerCount = ResolvedArrayLayers,
                    LevelCount = 1,
                }
            };

            int mipWidth = (int)ResolvedExtent.Width;
            int mipHeight = (int)ResolvedExtent.Height;

            for (uint level = 1; level < ResolvedMipLevels; level++)
            {
                barrier.SubresourceRange.BaseMipLevel = level - 1;
                barrier.OldLayout = ImageLayout.TransferDstOptimal;
                barrier.NewLayout = ImageLayout.TransferSrcOptimal;
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags.TransferReadBit;

                Api.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, ref barrier);

                ImageBlit blit = CreateMipBlit(level, mipWidth, mipHeight);
                Api.CmdBlitImage(cmd, Image, ImageLayout.TransferSrcOptimal, Image, ImageLayout.TransferDstOptimal, 1, ref blit, Filter.Linear);

                barrier.OldLayout = ImageLayout.TransferSrcOptimal;
                barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
                barrier.SrcAccessMask = AccessFlags.TransferReadBit;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;

                Api.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, ref barrier);

                if (mipWidth > 1)
                    mipWidth /= 2;
                if (mipHeight > 1)
                    mipHeight /= 2;
            }

            barrier.SubresourceRange.BaseMipLevel = ResolvedMipLevels - 1;
            barrier.OldLayout = ImageLayout.TransferDstOptimal;
            barrier.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;

            Api.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, ref barrier);

            _currentImageLayout = ImageLayout.ShaderReadOnlyOptimal;
        }

        private ImageBlit CreateMipBlit(uint targetLevel, int mipWidth, int mipHeight)
        {
            int dstWidth = Math.Max(mipWidth / 2, 1);
            int dstHeight = Math.Max(mipHeight / 2, 1);

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = AspectFlags,
                    MipLevel = targetLevel - 1,
                    BaseArrayLayer = 0,
                    LayerCount = ResolvedArrayLayers,
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = AspectFlags,
                    MipLevel = targetLevel,
                    BaseArrayLayer = 0,
                    LayerCount = ResolvedArrayLayers,
                }
            };

            blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.SrcOffsets.Element1 = new Offset3D(mipWidth, mipHeight, 1);
            blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.DstOffsets.Element1 = new Offset3D(dstWidth, dstHeight, 1);

            return blit;
        }
    }

    internal sealed class VkTexture2D(VulkanRenderer api, XRTexture2D data) : VkImageBackedTexture<XRTexture2D>(api, data)
    {
        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(width, height, 1), 1, mipLevels);
        }

        protected override void PushTextureData()
        {
            Generate();

            var mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.LogWarning($"Texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                Mipmap2D? mip = mipmaps[level];
                if (mip is null)
                    continue;

                if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                    continue;

                try
                {
                    Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
                    CopyBufferToImage(stagingBuffer, level, 0, 1, extent);
                }
                finally
                {
                    DestroyStagingBuffer(stagingBuffer, stagingMemory);
                }
            }

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }

    }

    internal sealed class VkTexture2DArray(VulkanRenderer api, XRTexture2DArray data) : VkImageBackedTexture<XRTexture2DArray>(api, data)
    {
        protected override TextureLayout DescribeTexture()
        {
            XRTexture2D[] textures = Data.Textures;
            uint width = textures.Length > 0 ? Math.Max(textures[0].Width, 1u) : 1u;
            uint height = textures.Length > 0 ? Math.Max(textures[0].Height, 1u) : 1u;
            uint layers = (uint)Math.Max(textures.Length, 1);
            return new TextureLayout(new Extent3D(width, height, 1), layers, 1);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (layerIndex >= 0)
            {
                uint baseLayer = (uint)Math.Max(layerIndex, 0);
                uint baseMip = (uint)Math.Max(mipLevel, 0);
                return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type2D, AspectFlags);
            }

            return default;
        }

        protected override void PushTextureData()
        {
            Generate();

            XRTexture2D[] layers = Data.Textures;
            if (layers is null || layers.Length == 0)
            {
                Debug.LogWarning($"Texture array '{Data.Name ?? GetDescribingName()}' has no layers to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint arrayLayers = Math.Min((uint)layers.Length, ResolvedArrayLayers);
            for (uint layer = 0; layer < arrayLayers; layer++)
            {
                XRTexture2D layerTexture = layers[layer];
                var mipmaps = layerTexture.Mipmaps;
                if (mipmaps is null || mipmaps.Length == 0)
                    continue;

                uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
                for (uint level = 0; level < levelCount; level++)
                {
                    Mipmap2D? mip = mipmaps[level];
                    if (mip is null)
                        continue;

                    if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                        continue;

                    try
                    {
                        Extent3D extent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1);
                        CopyBufferToImage(stagingBuffer, level, layer, 1, extent);
                    }
                    finally
                    {
                        DestroyStagingBuffer(stagingBuffer, stagingMemory);
                    }
                }
            }

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
    }

    internal sealed class VkTexture3D(VulkanRenderer api, XRTexture3D data) : VkImageBackedTexture<XRTexture3D>(api, data)
    {
        protected override ImageType TextureImageType => ImageType.Type3D;
        protected override ImageViewType DefaultImageViewType => ImageViewType.Type3D;

        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            uint depth = Math.Max(Data.Depth, 1u);
            return new TextureLayout(new Extent3D(width, height, depth), 1, 1);
        }
    }

    internal sealed class VkTextureCube(VulkanRenderer api, XRTextureCube data) : VkImageBackedTexture<XRTextureCube>(api, data)
    {
        private const ImageCreateFlags CubeCompatibleFlag = (ImageCreateFlags)0x10;

        protected override ImageCreateFlags AdditionalImageFlags => CubeCompatibleFlag;
        protected override ImageViewType DefaultImageViewType => ImageViewType.TypeCube;

        protected override TextureLayout DescribeTexture()
        {
            uint extent = Math.Max(Data.Extent, 1u);
            uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(extent, extent, 1), 6, mipLevels);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (layerIndex >= 0 && layerIndex < 6)
            {
                uint baseMip = (uint)Math.Max(mipLevel, 0);
                return new AttachmentViewKey(baseMip, 1, (uint)layerIndex, 1, ImageViewType.Type2D, AspectFlags);
            }

            return default;
        }

        protected override void PushTextureData()
        {
            Generate();

            CubeMipmap[] mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.LogWarning($"Cubemap '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                CubeMipmap? cubeMip = mipmaps[level];
                if (cubeMip is null)
                    continue;

                uint faceCount = Math.Min((uint)cubeMip.Sides.Length, ResolvedArrayLayers);
                for (uint face = 0; face < faceCount; face++)
                {
                    Mipmap2D side = cubeMip.Sides[face];
                    if (!TryCreateStagingBuffer(side.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                        continue;

                    try
                    {
                        Extent3D extent = new(Math.Max(side.Width, 1u), Math.Max(side.Height, 1u), 1);
                        CopyBufferToImage(stagingBuffer, level, face, 1, extent);
                    }
                    finally
                    {
                        DestroyStagingBuffer(stagingBuffer, stagingMemory);
                    }
                }
            }

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
    }
}