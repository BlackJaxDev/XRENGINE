using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials.Textures;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal abstract class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
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
        Image IVkImageDescriptorSource.DescriptorImage => _image;
        ImageView IVkImageDescriptorSource.DescriptorView => _view;
        Sampler IVkImageDescriptorSource.DescriptorSampler => _sampler;
        Format IVkImageDescriptorSource.DescriptorFormat => ResolvedFormat;
        ImageAspectFlags IVkImageDescriptorSource.DescriptorAspect => AspectFlags;
        ImageUsageFlags IVkImageDescriptorSource.DescriptorUsage => Usage;
        SampleCountFlags IVkImageDescriptorSource.DescriptorSamples => SampleCount;

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
            AspectFlags = NormalizeAspectMaskForFormat(Format, AspectFlags);
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
                Usage = group.Usage;
                _arrayLayersOverride = Math.Max(group.Template.Layers, 1u);
                _mipLevelsOverride = 1;
                _ownsImageMemory = false;
                AspectFlags = NormalizeAspectMaskForFormat(ResolvedFormat, AspectFlags);
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
            AspectFlags = NormalizeAspectMaskForFormat(ResolvedFormat, AspectFlags);
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

            ImageAspectFlags normalizedAspect = NormalizeAspectMaskForFormat(ResolvedFormat, AspectFlags);
            AspectFlags = normalizedAspect;

            AttachmentViewKey descriptor = key == default
                ? new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, DefaultViewType, normalizedAspect)
                : key;

            _view = CreateView(descriptor);
        }

        private ImageView CreateView(AttachmentViewKey descriptor)
        {
            ImageAspectFlags aspectMask = NormalizeAspectMaskForFormat(ResolvedFormat, descriptor.AspectMask);

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = descriptor.ViewType,
                Format = ResolvedFormat,
                Components = new ComponentMapping(ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspectMask,
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

        private static ImageAspectFlags NormalizeAspectMaskForFormat(Format format, ImageAspectFlags requested)
        {
            bool isDepthStencil = format is Format.D16Unorm or Format.X8D24UnormPack32 or Format.D32Sfloat or Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;
            if (!isDepthStencil)
            {
                ImageAspectFlags colorMask = requested & ImageAspectFlags.ColorBit;
                return colorMask != ImageAspectFlags.None ? colorMask : ImageAspectFlags.ColorBit;
            }

            bool hasStencil = format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;
            ImageAspectFlags supported = hasStencil
                ? (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)
                : ImageAspectFlags.DepthBit;

            ImageAspectFlags normalized = requested & supported;
            if (normalized == ImageAspectFlags.None)
                normalized = supported;

            // Without separateDepthStencilLayouts support, depth-stencil formats that include
            // stencil must transition both aspects together.
            if (hasStencil && (normalized & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0)
                normalized = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;

            return normalized;
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

        public ImageView GetAttachmentView(int mipLevel, int layerIndex)
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

        internal void TransitionImageLayout(ImageLayout oldLayout, ImageLayout newLayout)
        {
            oldLayout = CoerceLayoutForUsage(oldLayout);
            newLayout = CoerceLayoutForUsage(newLayout);
            AssembleTransitionImageLayout(oldLayout, newLayout, out ImageMemoryBarrier barrier, out PipelineStageFlags src, out PipelineStageFlags dst);
            using var scope = Renderer.NewCommandScope();
            Api!.CmdPipelineBarrier(scope.CommandBuffer, src, dst, 0, 0, null, 0, null, 1, ref barrier);
            _currentImageLayout = newLayout;
        }

        private ImageLayout CoerceLayoutForUsage(ImageLayout requested)
        {
            if (requested != ImageLayout.ShaderReadOnlyOptimal)
                return requested;

            bool canSample = (Usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0;
            if (canSample)
                return requested;

            if ((Usage & ImageUsageFlags.StorageBit) != 0)
                return ImageLayout.General;

            return ImageLayout.TransferSrcOptimal;
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

            if (Renderer.TryCopyBufferToImageViaIndirectNv(
                buffer,
                srcOffset: 0,
                _image,
                ImageLayout.TransferDstOptimal,
                region.ImageSubresource,
                region.ImageOffset,
                region.ImageExtent))
            {
                return;
            }

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
            Debug.VulkanWarning($"{GetType().Name} does not implement texture data uploads yet.");
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
                case XRTexture1D tex1D:
                    tex1D.Resized += OnTextureResized;
                    break;
                case XRTexture1DArray tex1DArray:
                    tex1DArray.Resized += OnTextureResized;
                    break;
                case XRTexture2D tex2D:
                    tex2D.Resized += OnTextureResized;
                    break;
                case XRTexture2DArray texArray:
                    texArray.Resized += OnTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized += OnTextureResized;
                    break;
                case XRTextureCubeArray texCubeArray:
                    texCubeArray.Resized += OnTextureResized;
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
                case XRTexture1D tex1D:
                    tex1D.Resized -= OnTextureResized;
                    break;
                case XRTexture1DArray tex1DArray:
                    tex1DArray.Resized -= OnTextureResized;
                    break;
                case XRTexture2D tex2D:
                    tex2D.Resized -= OnTextureResized;
                    break;
                case XRTexture2DArray texArray:
                    texArray.Resized -= OnTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized -= OnTextureResized;
                    break;
                case XRTextureCubeArray texCubeArray:
                    texCubeArray.Resized -= OnTextureResized;
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

            bool preferIndirectCopy = Renderer.SupportsNvCopyMemoryIndirect && Renderer.SupportsBufferDeviceAddress;
            BufferUsageFlags usage = BufferUsageFlags.TransferSrcBit;
            if (preferIndirectCopy)
                usage |= BufferUsageFlags.ShaderDeviceAddressBit;

            (buffer, memory) = Renderer.CreateBuffer(
                data.Length,
                usage,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                data.Address,
                preferIndirectCopy);
            return true;
        }

        /// <summary>
        /// Creates a Vulkan staging buffer and fills it directly from a file via DirectStorage.
        /// Reads file data straight into the mapped Vulkan host-visible memory, eliminating the
        /// intermediate managed byte[] allocation.
        /// <para>
        /// This is the Vulkan equivalent of DirectStorage's D3D12 <c>DestinationBuffer</c>:
        /// since DirectStorage GPU destinations require <c>ID3D12Resource*</c>, Vulkan engines
        /// achieve the same effect by reading into a mapped staging buffer, then issuing
        /// <c>CmdCopyBufferToImage</c> to transfer to device-local memory.
        /// </para>
        /// Use this for pre-cooked binary texture data (DDS, KTX, raw pixel blobs) that
        /// does not require CPU-side decoding.
        /// </summary>
        /// <param name="filePath">Path to the source file.</param>
        /// <param name="offset">Byte offset within the file.</param>
        /// <param name="length">Number of bytes to read.</param>
        /// <param name="buffer">The created staging buffer.</param>
        /// <param name="memory">The staging buffer's device memory.</param>
        /// <returns><c>true</c> if successful; <c>false</c> if the file could not be read.</returns>
        protected bool TryCreateStagingBufferFromFile(
            string filePath, long offset, int length,
            out Buffer buffer, out DeviceMemory memory)
        {
            buffer = default;
            memory = default;

            if (string.IsNullOrWhiteSpace(filePath) || length <= 0)
                return false;

            bool preferIndirectCopy = Renderer.SupportsNvCopyMemoryIndirect && Renderer.SupportsBufferDeviceAddress;
            BufferUsageFlags usage = BufferUsageFlags.TransferSrcBit;
            if (preferIndirectCopy)
                usage |= BufferUsageFlags.ShaderDeviceAddressBit;

            // Allocate a host-visible staging buffer WITHOUT copying any data yet.
            (buffer, memory) = Renderer.CreateBufferRaw(
                (ulong)length,
                usage,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                preferIndirectCopy);

            // Map the staging buffer memory.
            void* mappedPtr = null;
            if (Api!.MapMemory(Device, memory, 0, (ulong)length, 0, &mappedPtr) != Result.Success)
            {
                Renderer.DestroyBuffer(buffer, memory);
                buffer = default;
                memory = default;
                return false;
            }

            try
            {
                // Read file data directly into the mapped staging buffer via DirectStorage.
                // Falls back to RandomAccess I/O if DirectStorage is unavailable.
                DirectStorageIO.TryReadInto(filePath, offset, length, mappedPtr);
            }
            catch
            {
                Api.UnmapMemory(Device, memory);
                Renderer.DestroyBuffer(buffer, memory);
                buffer = default;
                memory = default;
                return false;
            }

            Api.UnmapMemory(Device, memory);
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
                Debug.VulkanWarning($"Texture format '{ResolvedFormat}' does not support linear blitting; skipping mipmap generation.");
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

    internal sealed class VkTexture1D(VulkanRenderer api, XRTexture1D data) : VkImageBackedTexture<XRTexture1D>(api, data)
    {
        protected override ImageType TextureImageType => ImageType.Type1D;
        protected override ImageViewType DefaultImageViewType => ImageViewType.Type1D;

        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(width, 1, 1), 1, mipLevels);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (mipLevel <= 0)
                return default;

            uint baseMip = (uint)Math.Max(mipLevel, 0);
            return new AttachmentViewKey(baseMip, 1, 0, 1, ImageViewType.Type1D, AspectFlags);
        }

        protected override void PushTextureData()
        {
            Generate();

            Mipmap1D[] mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.VulkanWarning($"1D texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                Mipmap1D? mip = mipmaps[level];
                if (mip is null)
                    continue;

                if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                    continue;

                try
                {
                    Extent3D extent = new(Math.Max(mip.Width, 1u), 1, 1);
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

    internal sealed class VkTexture1DArray(VulkanRenderer api, XRTexture1DArray data) : VkImageBackedTexture<XRTexture1DArray>(api, data)
    {
        protected override ImageType TextureImageType => ImageType.Type1D;
        protected override ImageViewType DefaultImageViewType => ImageViewType.Type1DArray;

        protected override TextureLayout DescribeTexture()
        {
            XRTexture1D[] textures = Data.Textures;
            uint width = textures.Length > 0 ? Math.Max(textures[0].Width, 1u) : 1u;
            uint layers = (uint)Math.Max(textures.Length, 1);
            uint mipLevels = (uint)Math.Max(
                textures.Length > 0 ? textures.Max(t => t?.Mipmaps?.Length ?? 1) : 1,
                1);
            return new TextureLayout(new Extent3D(width, 1, 1), layers, mipLevels);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (layerIndex < 0 && mipLevel <= 0)
                return default;

            uint baseLayer = (uint)Math.Max(layerIndex, 0);
            uint baseMip = (uint)Math.Max(mipLevel, 0);
            return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type1D, AspectFlags);
        }

        protected override void PushTextureData()
        {
            Generate();

            XRTexture1D[] layers = Data.Textures;
            if (layers is null || layers.Length == 0)
            {
                Debug.VulkanWarning($"1D texture array '{Data.Name ?? GetDescribingName()}' has no layers to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint arrayLayers = Math.Min((uint)layers.Length, ResolvedArrayLayers);
            for (uint layer = 0; layer < arrayLayers; layer++)
            {
                XRTexture1D layerTexture = layers[layer];
                Mipmap1D[] mipmaps = layerTexture.Mipmaps;
                if (mipmaps is null || mipmaps.Length == 0)
                    continue;

                uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
                for (uint level = 0; level < levelCount; level++)
                {
                    Mipmap1D? mip = mipmaps[level];
                    if (mip is null)
                        continue;

                    if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                        continue;

                    try
                    {
                        Extent3D extent = new(Math.Max(mip.Width, 1u), 1, 1);
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
                Debug.VulkanWarning($"Texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
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
                Debug.VulkanWarning($"Texture array '{Data.Name ?? GetDescribingName()}' has no layers to upload.");
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
            uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(width, height, depth), 1, mipLevels);
        }

        protected override void PushTextureData()
        {
            Generate();

            Mipmap3D[] mipmaps = Data.Mipmaps;
            if (mipmaps is null || mipmaps.Length == 0)
            {
                Debug.VulkanWarning($"3D texture '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
            for (uint level = 0; level < levelCount; level++)
            {
                Mipmap3D? mip = mipmaps[level];
                if (mip is null)
                    continue;

                if (!TryCreateStagingBuffer(mip.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                    continue;

                try
                {
                    Extent3D extent = new(
                        Math.Max(mip.Width, 1u),
                        Math.Max(mip.Height, 1u),
                        Math.Max(mip.Depth, 1u));
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

    internal sealed class VkTextureRectangle(VulkanRenderer api, XRTextureRectangle data) : VkImageBackedTexture<XRTextureRectangle>(api, data)
    {
        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            return new TextureLayout(new Extent3D(width, height, 1), 1, 1);
        }

        protected override void PushTextureData()
        {
            Generate();
            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            if (TryCreateStagingBuffer(Data.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
            {
                try
                {
                    Extent3D extent = new(Math.Max(Data.Width, 1u), Math.Max(Data.Height, 1u), 1);
                    CopyBufferToImage(stagingBuffer, 0, 0, 1, extent);
                }
                finally
                {
                    DestroyStagingBuffer(stagingBuffer, stagingMemory);
                }
            }

            TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
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
                Debug.VulkanWarning($"Cubemap '{Data.Name ?? GetDescribingName()}' has no mipmaps to upload.");
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

    internal sealed class VkTextureCubeArray(VulkanRenderer api, XRTextureCubeArray data) : VkImageBackedTexture<XRTextureCubeArray>(api, data)
    {
        private const ImageCreateFlags CubeCompatibleFlag = (ImageCreateFlags)0x10;

        protected override ImageCreateFlags AdditionalImageFlags => CubeCompatibleFlag;
        protected override ImageViewType DefaultImageViewType => ImageViewType.TypeCubeArray;

        protected override TextureLayout DescribeTexture()
        {
            XRTextureCube[] cubes = Data.Cubes;
            uint extent = cubes.Length > 0 ? Math.Max(cubes[0].Extent, 1u) : 1u;
            uint layers = (uint)Math.Max(cubes.Length, 1) * 6u;
            uint mipLevels = (uint)Math.Max(
                cubes.Length > 0 ? cubes.Max(c => c?.Mipmaps?.Length ?? 1) : 1,
                1);
            return new TextureLayout(new Extent3D(extent, extent, 1), layers, mipLevels);
        }

        protected override AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            if (layerIndex < 0 && mipLevel <= 0)
                return default;

            uint baseLayer = (uint)Math.Max(layerIndex, 0);
            uint baseMip = (uint)Math.Max(mipLevel, 0);
            return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type2D, AspectFlags);
        }

        protected override void PushTextureData()
        {
            Generate();

            XRTextureCube[] cubes = Data.Cubes;
            if (cubes is null || cubes.Length == 0)
            {
                Debug.VulkanWarning($"Cube array '{Data.Name ?? GetDescribingName()}' has no cube layers to upload.");
                return;
            }

            TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

            uint cubeCount = Math.Min((uint)cubes.Length, Math.Max(1u, ResolvedArrayLayers / 6u));
            for (uint cubeIndex = 0; cubeIndex < cubeCount; cubeIndex++)
            {
                XRTextureCube cube = cubes[cubeIndex];
                CubeMipmap[] mipmaps = cube.Mipmaps;
                if (mipmaps is null || mipmaps.Length == 0)
                    continue;

                uint levelCount = Math.Min((uint)mipmaps.Length, ResolvedMipLevels);
                for (uint level = 0; level < levelCount; level++)
                {
                    CubeMipmap? cubeMip = mipmaps[level];
                    if (cubeMip is null)
                        continue;

                    uint faceCount = Math.Min((uint)cubeMip.Sides.Length, 6u);
                    for (uint face = 0; face < faceCount; face++)
                    {
                        uint baseLayer = cubeIndex * 6u + face;
                        if (baseLayer >= ResolvedArrayLayers)
                            break;

                        Mipmap2D side = cubeMip.Sides[face];
                        if (!TryCreateStagingBuffer(side.Data, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                            continue;

                        try
                        {
                            Extent3D extent = new(Math.Max(side.Width, 1u), Math.Max(side.Height, 1u), 1);
                            CopyBufferToImage(stagingBuffer, level, baseLayer, 1, extent);
                        }
                        finally
                        {
                            DestroyStagingBuffer(stagingBuffer, stagingMemory);
                        }
                    }
                }
            }

            if (Data.AutoGenerateMipmaps && ResolvedMipLevels > 1)
                GenerateMipmapsGPU();
            else
                TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        }
    }

    internal sealed class VkTextureBuffer(VulkanRenderer api, XRTextureBuffer data) : VkTexture<XRTextureBuffer>(api, data), IVkTexelBufferDescriptorSource
    {
        private BufferView _view;
        private Format _format = Format.R8G8B8A8Unorm;

        internal BufferView View => _view;
        internal Format BufferFormat => _format;
        BufferView IVkTexelBufferDescriptorSource.DescriptorBufferView => _view;
        Format IVkTexelBufferDescriptorSource.DescriptorBufferFormat => _format;

        public override VkObjectType Type => VkObjectType.BufferView;
        public override bool IsGenerated => _view.Handle != 0;

        protected override uint CreateObjectInternal()
        {
            CreateBufferView();
            return CacheObject(this);
        }

        protected override void DeleteObjectInternal()
        {
            if (_view.Handle != 0)
            {
                Api!.DestroyBufferView(Device, _view, null);
                _view = default;
            }
        }

        protected override void LinkData()
            => Data.PropertyChanged += OnTextureBufferPropertyChanged;

        protected override void UnlinkData()
            => Data.PropertyChanged -= OnTextureBufferPropertyChanged;

        private void OnTextureBufferPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XRTextureBuffer.DataBuffer):
                case nameof(XRTextureBuffer.SizedInternalFormat):
                case nameof(XRTextureBuffer.TexelCount):
                    if (IsActive)
                    {
                        Destroy();
                        Generate();
                    }
                    break;
            }
        }

        private void CreateBufferView()
        {
            XRDataBuffer? sourceBuffer = Data.DataBuffer;
            if (sourceBuffer is null)
            {
                Debug.VulkanWarning($"Texture buffer '{Data.Name ?? "<unnamed>"}' has no source data buffer.");
                return;
            }

            if (Renderer.GetOrCreateAPIRenderObject(sourceBuffer, generateNow: true) is not VkDataBuffer vkDataBuffer)
                throw new InvalidOperationException("Texture buffer source is not backed by a Vulkan data buffer.");

            vkDataBuffer.PushData();
            Buffer? handle = vkDataBuffer.BufferHandle;
            if (handle is null || handle.Value.Handle == 0)
            {
                Debug.VulkanWarning($"Texture buffer '{Data.Name ?? "<unnamed>"}' could not resolve a Vulkan buffer handle.");
                return;
            }

            _format = ResolveBufferFormat(Data.SizedInternalFormat);
            ulong bytesPerTexel = ResolveTexelSize(Data.SizedInternalFormat);
            ulong requestedRange = Data.TexelCount > 0
                ? bytesPerTexel * Data.TexelCount
                : Math.Max(1u, sourceBuffer.Length);
            ulong range = Math.Max(1ul, Math.Min(requestedRange, Math.Max(1u, sourceBuffer.Length)));

            BufferViewCreateInfo createInfo = new()
            {
                SType = StructureType.BufferViewCreateInfo,
                Buffer = handle.Value,
                Format = _format,
                Offset = 0,
                Range = range
            };

            if (Api!.CreateBufferView(Device, ref createInfo, null, out _view) != Result.Success)
                throw new Exception($"Failed to create Vulkan buffer view for texture buffer '{Data.Name ?? "<unnamed>"}'.");
        }

        private static Format ResolveBufferFormat(ESizedInternalFormat sizedFormat)
            => sizedFormat switch
            {
                ESizedInternalFormat.R8 => Format.R8Unorm,
                ESizedInternalFormat.R8i => Format.R8Sint,
                ESizedInternalFormat.R8ui => Format.R8Uint,
                ESizedInternalFormat.R16 => Format.R16Unorm,
                ESizedInternalFormat.R16i => Format.R16Sint,
                ESizedInternalFormat.R16ui => Format.R16Uint,
                ESizedInternalFormat.R16f => Format.R16Sfloat,
                ESizedInternalFormat.R32i => Format.R32Sint,
                ESizedInternalFormat.R32ui => Format.R32Uint,
                ESizedInternalFormat.R32f => Format.R32Sfloat,
                ESizedInternalFormat.Rg8 => Format.R8G8Unorm,
                ESizedInternalFormat.Rg8i => Format.R8G8Sint,
                ESizedInternalFormat.Rg8ui => Format.R8G8Uint,
                ESizedInternalFormat.Rg16 => Format.R16G16Unorm,
                ESizedInternalFormat.Rg16i => Format.R16G16Sint,
                ESizedInternalFormat.Rg16ui => Format.R16G16Uint,
                ESizedInternalFormat.Rg16f => Format.R16G16Sfloat,
                ESizedInternalFormat.Rg32i => Format.R32G32Sint,
                ESizedInternalFormat.Rg32ui => Format.R32G32Uint,
                ESizedInternalFormat.Rg32f => Format.R32G32Sfloat,
                ESizedInternalFormat.Rgb8 => Format.R8G8B8Unorm,
                ESizedInternalFormat.Rgb8i => Format.R8G8B8Sint,
                ESizedInternalFormat.Rgb8ui => Format.R8G8B8Uint,
                ESizedInternalFormat.Rgb16i => Format.R16G16B16Sint,
                ESizedInternalFormat.Rgb16ui => Format.R16G16B16Uint,
                ESizedInternalFormat.Rgb16f => Format.R16G16B16Sfloat,
                ESizedInternalFormat.Rgb32i => Format.R32G32B32Sint,
                ESizedInternalFormat.Rgb32ui => Format.R32G32B32Uint,
                ESizedInternalFormat.Rgb32f => Format.R32G32B32Sfloat,
                ESizedInternalFormat.Rgba8 => Format.R8G8B8A8Unorm,
                ESizedInternalFormat.Rgba8i => Format.R8G8B8A8Sint,
                ESizedInternalFormat.Rgba8ui => Format.R8G8B8A8Uint,
                ESizedInternalFormat.Rgba16 => Format.R16G16B16A16Unorm,
                ESizedInternalFormat.Rgba16i => Format.R16G16B16A16Sint,
                ESizedInternalFormat.Rgba16ui => Format.R16G16B16A16Uint,
                ESizedInternalFormat.Rgba16f => Format.R16G16B16A16Sfloat,
                ESizedInternalFormat.Rgba32i => Format.R32G32B32A32Sint,
                ESizedInternalFormat.Rgba32ui => Format.R32G32B32A32Uint,
                ESizedInternalFormat.Rgba32f => Format.R32G32B32A32Sfloat,
                _ => Format.R8G8B8A8Unorm
            };

        private static ulong ResolveTexelSize(ESizedInternalFormat sizedFormat)
            => sizedFormat switch
            {
                ESizedInternalFormat.R8 or
                ESizedInternalFormat.R8Snorm or
                ESizedInternalFormat.R8i or
                ESizedInternalFormat.R8ui => 1,

                ESizedInternalFormat.R16 or
                ESizedInternalFormat.R16Snorm or
                ESizedInternalFormat.R16f or
                ESizedInternalFormat.R16i or
                ESizedInternalFormat.R16ui or
                ESizedInternalFormat.Rg8 or
                ESizedInternalFormat.Rg8Snorm or
                ESizedInternalFormat.Rg8i or
                ESizedInternalFormat.Rg8ui => 2,

                ESizedInternalFormat.R32f or
                ESizedInternalFormat.R32i or
                ESizedInternalFormat.R32ui or
                ESizedInternalFormat.Rg16 or
                ESizedInternalFormat.Rg16Snorm or
                ESizedInternalFormat.Rg16f or
                ESizedInternalFormat.Rg16i or
                ESizedInternalFormat.Rg16ui or
                ESizedInternalFormat.Rgba8 or
                ESizedInternalFormat.Rgba8Snorm or
                ESizedInternalFormat.Rgba8i or
                ESizedInternalFormat.Rgba8ui => 4,

                ESizedInternalFormat.Rgb32f or
                ESizedInternalFormat.Rgb32i or
                ESizedInternalFormat.Rgb32ui => 12,

                ESizedInternalFormat.Rgba32f or
                ESizedInternalFormat.Rgba32i or
                ESizedInternalFormat.Rgba32ui => 16,

                _ => 4
            };
    }
}
