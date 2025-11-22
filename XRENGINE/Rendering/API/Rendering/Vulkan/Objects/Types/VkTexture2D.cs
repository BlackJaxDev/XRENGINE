using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public class VkFrameBuffer(VulkanRenderer api, XRFrameBuffer data) : VkObject<XRFrameBuffer>(api, data)
    {
        private Framebuffer _frameBuffer = default;

        public override VkObjectType Type { get; } = VkObjectType.Framebuffer;
        public override bool IsGenerated { get; }

        public Framebuffer FrameBuffer => _frameBuffer;

        public override void Destroy()
        {
            Api!.DestroyFramebuffer(Device, _frameBuffer, null);
            _frameBuffer = default;
        }

        protected override uint CreateObjectInternal()
        {
            var targets = Data.Targets;
            if (targets is null || targets.Length == 0)
                throw new InvalidOperationException("Framebuffer must have at least one attachment.");

            ImageView[] views = new ImageView[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                var (target, _, mip, layer) = targets[i];
                views[i] = ResolveTargetView(target, mip, layer);
            }

            fixed (ImageView* viewsPtr = views)
            {
                FramebufferCreateInfo framebufferInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    AttachmentCount = (uint)views.Length,
                    PAttachments = viewsPtr,
                    Width = Math.Max(Data.Width, 1u),
                    Height = Math.Max(Data.Height, 1u),
                    Layers = 1,
                };

                fixed (Framebuffer* frameBufferPtr = &_frameBuffer)
                {
                    if (Api!.CreateFramebuffer(Device, ref framebufferInfo, null, frameBufferPtr) != Result.Success)
                        throw new Exception("Failed to create framebuffer.");
                }
            }

            return CacheObject(this);
        }

        private ImageView ResolveTargetView(IFrameBufferAttachement target, int mipLevel, int layerIndex)
        {
            switch (target)
            {
                case XRTexture2D tex2D:
                    return ResolveView(tex2D, mipLevel, layerIndex);
                case XRTexture2DArray texArray:
                    return ResolveView(texArray, mipLevel, layerIndex);
                case XRTexture3D tex3D:
                    return ResolveView(tex3D, mipLevel, layerIndex);
                case XRTextureCube texCube:
                    return ResolveView(texCube, mipLevel, layerIndex);
                case XRRenderBuffer renderBuffer:
                    var vkRenderBuffer = Renderer.GetOrCreateAPIRenderObject(renderBuffer) as VkRenderBuffer
                        ?? throw new InvalidOperationException("Render buffer is not backed by a Vulkan object.");
                    vkRenderBuffer.Generate();
                    return vkRenderBuffer.View;
            }

            throw new NotSupportedException($"Framebuffer attachment type '{target?.GetType().Name ?? "<null>"}' is not supported yet.");
        }

        private ImageView ResolveView<TTexture>(TTexture texture, int mipLevel, int layerIndex) where TTexture : XRTexture
        {
            if (Renderer.GetOrCreateAPIRenderObject(texture) is not VkImageBackedTexture<TTexture> vkTexture)
                throw new InvalidOperationException($"Texture '{texture.Name ?? texture.GetDescribingName()}' is not backed by a Vulkan texture.");

            vkTexture.Generate();
            return vkTexture.GetAttachmentView(mipLevel, layerIndex);
        }

        protected override void DeleteObjectInternal() { }
        protected override void LinkData() { }
        protected override void UnlinkData() { }
    }

    internal abstract class VkImageBackedTexture<TTexture>(VulkanRenderer api, TTexture data) : VkTexture<TTexture>(api, data) where TTexture : XRTexture
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

        public override bool IsGenerated { get; }

        internal Image Image => _image;
        internal ImageView View => _view;
        internal Sampler Sampler => _sampler;
        internal bool UsesAllocatorImage => _physicalGroup is not null;

        protected Format ResolvedFormat => _formatOverride ?? Format;
        protected Extent3D ResolvedExtent => _extentOverride ?? _layout.Extent;
        protected uint ResolvedArrayLayers => _arrayLayersOverride ?? _layout.ArrayLayers;
        protected uint ResolvedMipLevels => _mipLevelsOverride ?? _layout.MipLevels;

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
                _image = group.Image;
                _memory = group.Memory;
                _extentOverride = group.ResolvedExtent;
                _formatOverride = group.Format;
                _arrayLayersOverride = Math.Max(group.Template.Layers, 1u);
                _mipLevelsOverride = 1;
                _ownsImageMemory = false;
                return;
            }

            CreateDedicatedImage();
            _physicalGroup = null;
            _extentOverride = null;
            _formatOverride = null;
            _arrayLayersOverride = null;
            _mipLevelsOverride = null;
            _ownsImageMemory = true;
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

            Bool32 anisotropyEnable = Vk.False;
            float maxAnisotropy = 1f;
            if (UseAniso)
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

        protected void CopyBufferToImage(Buffer buffer)
        {
            BufferImageCopy region = new()
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = AspectFlags,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = ResolvedArrayLayers,
                },
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = ResolvedExtent,
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

        protected virtual ImageUsageFlags DefaultUsage => ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit;
        protected virtual ImageAspectFlags DefaultAspect => ImageAspectFlags.ColorBit;
        protected virtual ImageViewType DefaultImageViewType => ImageViewType.Type2D;
        protected virtual ImageType TextureImageType => ImageType.Type2D;
        protected virtual ImageCreateFlags AdditionalImageFlags => 0;

        protected abstract TextureLayout DescribeTexture();

        protected readonly record struct TextureLayout(Extent3D Extent, uint ArrayLayers, uint MipLevels);

        protected readonly record struct AttachmentViewKey(uint BaseMipLevel, uint LevelCount, uint BaseArrayLayer, uint LayerCount, ImageViewType ViewType, ImageAspectFlags AspectMask);
    }

    public sealed class VkTexture2D(VulkanRenderer api, XRTexture2D data) : VkImageBackedTexture<XRTexture2D>(api, data)
    {
        protected override TextureLayout DescribeTexture()
        {
            uint width = Math.Max(Data.Width, 1u);
            uint height = Math.Max(Data.Height, 1u);
            uint mipLevels = (uint)Math.Max(Data.Mipmaps?.Length ?? 1, 1);
            return new TextureLayout(new Extent3D(width, height, 1), 1, mipLevels);
        }
    }

    public sealed class VkTexture2DArray(VulkanRenderer api, XRTexture2DArray data) : VkImageBackedTexture<XRTexture2DArray>(api, data)
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
    }

    public sealed class VkTexture3D(VulkanRenderer api, XRTexture3D data) : VkImageBackedTexture<XRTexture3D>(api, data)
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

    public sealed class VkTextureCube(VulkanRenderer api, XRTextureCube data) : VkImageBackedTexture<XRTextureCube>(api, data)
    {
        protected override ImageCreateFlags AdditionalImageFlags => ImageCreateFlags.CubeCompatibleBit;
        protected override ImageViewType DefaultImageViewType => ImageViewType.Cube;

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
    }
}