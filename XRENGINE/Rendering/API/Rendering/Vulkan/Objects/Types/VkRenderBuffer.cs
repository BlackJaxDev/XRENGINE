using System;
using Silk.NET.Vulkan;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public sealed class VkRenderBuffer(VulkanRenderer api, XRRenderBuffer data) : VkObject<XRRenderBuffer>(api, data)
    {
        private Image _image;
        private DeviceMemory _memory;
        private ImageView _view;
        private bool _ownsImage;
        private VulkanPhysicalImageGroup? _physicalGroup;
        private Format? _formatOverride;
        private ImageAspectFlags? _aspectOverride;
        private SampleCountFlags? _samplesOverride;

        internal Image Image => _image;

        public override VkObjectType Type => VkObjectType.Renderbuffer;
        public override bool IsGenerated => true;

        internal ImageView View => _view;
        internal Format Format => _formatOverride ?? ResolveFormat(Data.Type);
        internal ImageAspectFlags Aspect => _aspectOverride ?? ResolveAspect(Data.Type);
        internal SampleCountFlags Samples => _samplesOverride ?? ResolveSamples(Data.MultisampleCount);

        protected override uint CreateObjectInternal()
        {
            AcquireImage();
            CreateImageView();
            return CacheObject(this);
        }

        protected override void DeleteObjectInternal()
        {
            if (_view.Handle != 0)
            {
                Api!.DestroyImageView(Device, _view, null);
                _view = default;
            }

            if (_ownsImage)
            {
                if (_image.Handle != 0)
                    Api!.DestroyImage(Device, _image, null);
                if (_memory.Handle != 0)
                    Api!.FreeMemory(Device, _memory, null);
            }

            _image = default;
            _memory = default;
            _physicalGroup = null;
            _formatOverride = null;
            _aspectOverride = null;
            _samplesOverride = null;
        }

        private void AcquireImage()
        {
            if (!string.IsNullOrWhiteSpace(Data.Name) && Renderer.ResourceAllocator.TryGetPhysicalGroupForResource(Data.Name, out VulkanPhysicalImageGroup group))
            {
                group.EnsureAllocated(Renderer);
                _physicalGroup = group;
                _image = group.Image;
                _memory = group.Memory;
                _ownsImage = false;
                _formatOverride = group.Format;
                _aspectOverride = ResolveAspect(Data.Type);
                _samplesOverride = SampleCountFlags.Count1Bit;
                return;
            }

            CreateDedicatedImage();
            _ownsImage = true;
            _formatOverride = null;
            _aspectOverride = null;
            _samplesOverride = null;
        }

        private void CreateDedicatedImage()
        {
            Format format = ResolveFormat(Data.Type);
            SampleCountFlags samples = ResolveSamples(Data.MultisampleCount);
            ImageCreateInfo info = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent = new Extent3D(Math.Max(Data.Width, 1u), Math.Max(Data.Height, 1u), 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Format = format,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined,
                Usage = ResolveUsage(Data.Type),
                Samples = samples,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Image* imagePtr = &_image)
            {
                if (Api!.CreateImage(Device, ref info, null, imagePtr) != Result.Success)
                    throw new Exception("Failed to create Vulkan render buffer image.");
            }

            Api!.GetImageMemoryRequirements(Device, _image, out MemoryRequirements requirements);

            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = Renderer.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };

            fixed (DeviceMemory* memoryPtr = &_memory)
            {
                Renderer.AllocateMemory(allocInfo, memoryPtr);
            }

            if (Api!.BindImageMemory(Device, _image, _memory, 0) != Result.Success)
                throw new Exception("Failed to bind memory for render buffer image.");
        }

        private void CreateImageView()
        {
            if (_view.Handle != 0)
            {
                Api!.DestroyImageView(Device, _view, null);
                _view = default;
            }

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = ImageViewType.Type2D,
                Format = ResolveFormat(Data.Type),
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ResolveAspect(Data.Type),
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (Api!.CreateImageView(Device, ref viewInfo, null, out _view) != Result.Success)
                throw new Exception("Failed to create render buffer image view.");
        }

        private static Format ResolveFormat(ERenderBufferStorage storage) => storage switch
        {
            ERenderBufferStorage.Depth24Stencil8 => Format.D24UnormS8Uint,
            ERenderBufferStorage.DepthComponent24 => Format.D24UnormS8Uint,
            ERenderBufferStorage.DepthComponent32 => Format.D32Sfloat,
            ERenderBufferStorage.DepthComponent32f => Format.D32Sfloat,
            ERenderBufferStorage.DepthComponent16 => Format.D16Unorm,
            ERenderBufferStorage.Depth32fStencil8 => Format.D32SfloatS8Uint,
            ERenderBufferStorage.Rgba16f => Format.R16G16B16A16Sfloat,
            ERenderBufferStorage.Rgba32f => Format.R32G32B32A32Sfloat,
            ERenderBufferStorage.Rgba8 => Format.R8G8B8A8Unorm,
            ERenderBufferStorage.Rgb8 => Format.R8G8B8Unorm,
            ERenderBufferStorage.R16f => Format.R16Sfloat,
            ERenderBufferStorage.R32f => Format.R32Sfloat,
            _ => Format.R8G8B8A8Unorm,
        };

        private static ImageUsageFlags ResolveUsage(ERenderBufferStorage storage)
        {
            ImageUsageFlags usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
            return IsDepthFormat(storage)
                ? usage | ImageUsageFlags.DepthStencilAttachmentBit
                : usage | ImageUsageFlags.ColorAttachmentBit;
        }

        private static ImageAspectFlags ResolveAspect(ERenderBufferStorage storage)
            => IsDepthFormat(storage)
                ? (storage == ERenderBufferStorage.Depth24Stencil8 || storage == ERenderBufferStorage.Depth32fStencil8
                    ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                    : ImageAspectFlags.DepthBit)
                : ImageAspectFlags.ColorBit;

        private static bool IsDepthFormat(ERenderBufferStorage storage) => storage switch
        {
            ERenderBufferStorage.DepthComponent => true,
            ERenderBufferStorage.DepthComponent16 => true,
            ERenderBufferStorage.DepthComponent24 => true,
            ERenderBufferStorage.DepthComponent32 => true,
            ERenderBufferStorage.DepthComponent32f => true,
            ERenderBufferStorage.Depth24Stencil8 => true,
            ERenderBufferStorage.Depth32fStencil8 => true,
            _ => false,
        };

        private static SampleCountFlags ResolveSamples(uint samples) => samples switch
        {
            >= 64 => SampleCountFlags.Count64Bit,
            >= 32 => SampleCountFlags.Count32Bit,
            >= 16 => SampleCountFlags.Count16Bit,
            >= 8 => SampleCountFlags.Count8Bit,
            >= 4 => SampleCountFlags.Count4Bit,
            >= 2 => SampleCountFlags.Count2Bit,
            _ => SampleCountFlags.Count1Bit,
        };

        protected override void LinkData() { }
        protected override void UnlinkData() { }
    }
}
