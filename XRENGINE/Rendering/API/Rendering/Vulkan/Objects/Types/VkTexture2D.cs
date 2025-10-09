using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public class VkFrameBuffer(VulkanRenderer api, XRFrameBuffer data) : VkObject<XRFrameBuffer>(api, data)
    {
        private Framebuffer _frameBuffer = default;
        public Framebuffer FrameBuffer
        {
            get => _frameBuffer;
            set => _frameBuffer = value;
        }

        public override VkObjectType Type { get; } = VkObjectType.Framebuffer;
        public override bool IsGenerated { get; }

        public override void Destroy()
        {
            Api!.DestroyFramebuffer(Device, _frameBuffer, null);
            _frameBuffer = default;
        }

        protected override uint CreateObjectInternal()
        {
            var targets = Data.Targets;
            if (targets is null || targets.Length == 0)
                throw new Exception("Framebuffer must have at least one target.");

            ImageView[] views = new ImageView[targets.Length];
            //for (int i = 0; i < targets.Length; i++)
            //    views[i] = targets[i].Target.View;

            fixed (ImageView* viewsPtr = views)
            {
                FramebufferCreateInfo framebufferInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    //RenderPass = Renderer.RenderPass,
                    AttachmentCount = (uint)targets.Length,
                    PAttachments = viewsPtr,
                    Width = Data.Width,
                    Height = Data.Height,
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

        protected override void DeleteObjectInternal()
        {
            throw new NotImplementedException();
        }

        protected override void LinkData()
        {
            throw new NotImplementedException();
        }

        protected override void UnlinkData()
        {
            throw new NotImplementedException();
        }
    }
    public class VkTexture2D(VulkanRenderer api, XRTexture2D data) : VkTexture<XRTexture2D>(api, data)
    {
        protected override uint CreateObjectInternal()
        {
            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = GetImageType(),
                Extent =
                {
                    Width = Width,
                    Height = Height,
                    Depth = Depth,
                },
                MipLevels = MipLevels,
                ArrayLayers = TexArrayCount,
                Format = Format,
                Tiling = Tiling,
                InitialLayout = ImageLayout.Undefined,
                Usage = Usage,
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = ExclusiveSharing ? SharingMode.Exclusive : SharingMode.Concurrent,
            };

            fixed (Image* imagePtr = &image)
            {
                if (Api.CreateImage(Device, ref imageInfo, null, imagePtr) != Result.Success)
                    throw new Exception("Failed to create image.");
            }

            Api.GetImageMemoryRequirements(Device, image, out MemoryRequirements memRequirements);

            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memRequirements.Size,
                MemoryTypeIndex = Renderer.FindMemoryType(memRequirements.MemoryTypeBits, MemoryProperties),
            };

            fixed (DeviceMemory* memPtr = &memory)
            {
                Renderer.AllocateMemory(allocInfo, memPtr);
            }

            Api!.BindImageMemory(Device, image, memory, 0);

            return CacheObject(this);
        }
        protected override void DeleteObjectInternal()
        {
            Api.DestroyImage(Device, image, null);
        }

        private Image image;
        private DeviceMemory memory;
        private ImageView view;
        private Sampler sampler;

        internal ImageView View => view;
        internal Sampler Sampler => sampler;

        public bool CreateSampler { get; set; } = true;

        public uint Width => Data.Width;
        public uint Height => Data.Height;
        public uint Depth { get; } = 1u;
        public uint MipLevels => (uint)Data.Mipmaps.Length;
        public uint TexArrayCount { get; set; } = 1;
        public bool ExclusiveSharing => Data.ExclusiveSharing;
        public Format Format { get; set; } = Format.R8G8B8A8Srgb;
        public MemoryPropertyFlags MemoryProperties { get; set; } = MemoryPropertyFlags.DeviceLocalBit;
        public ImageTiling Tiling { get; set; } = ImageTiling.Optimal;
        public ImageUsageFlags Usage { get; set; } = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit;
        public ImageType GetImageType()
            => ViewType switch
            {
                ImageViewType.Type1D or ImageViewType.Type1DArray => ImageType.Type1D,
                ImageViewType.Type3D or ImageViewType.TypeCube or ImageViewType.TypeCubeArray => ImageType.Type3D,
                _ => ImageType.Type2D,
            };

        //View
        public ImageAspectFlags AspectFlags { get; set; } = ImageAspectFlags.ColorBit;
        public ImageViewType ViewType { get; set; } = ImageViewType.Type2D;
        
        //Sampler
        public SamplerMipmapMode MipmapMode { get; set; } = SamplerMipmapMode.Linear;
        public SamplerAddressMode UWrap { get; set; } = SamplerAddressMode.Repeat;
        public SamplerAddressMode VWrap { get; set; } = SamplerAddressMode.Repeat;
        public SamplerAddressMode WWrap { get; set; } = SamplerAddressMode.Repeat;
        public Filter MinFilter { get; set; } = Filter.Linear;
        public Filter MagFilter { get; set; } = Filter.Linear;
        public bool UseAniso { get; set; } = true;
        public override bool IsGenerated { get; }

        public override void Destroy()
        {
            Api!.DestroySampler(Device, sampler, null);
            Api!.DestroyImageView(Device, view, null);
            Api!.DestroyImage(Device, image, null);
            Api!.FreeMemory(Device, memory, null);
        }

        //public void LoadFromImage(string path)
        //{
        //    using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
        //    Width = (uint)img.Width;
        //    Height = (uint)img.Height;

        //    using VkBuffer staging = new(Renderer, new XRDataBuffer(EBufferTarget.ArrayBuffer, false));
        //    MapImage(staging, img);
        //    LoadFromBuffer(staging);
        //}

        //public static void MapImage(VkBuffer buf, Image<Rgba32> image)
        //{
        //    buf.Count = (ulong)(image.Width * image.Height * image.PixelType.BitsPerPixel / 8);

        //    var span = buf.BeginUpdate();
        //    image.CopyPixelDataTo(span);
        //    buf.EndUpdate();
        //}

        //private void LoadFromBuffer(VkBuffer buffer)
        //{
        //    CreateImage();
        //    TransitionImageLayout(ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        //    CopyBufferToImage(buffer.Buffer);
        //    TransitionImageLayout(ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        //}

        public override void Generate()
        {
            CreateView();
            if (CreateSampler)
                GenSampler();
        }

        public void GenSampler()
        {
            Api!.GetPhysicalDeviceProperties(Renderer._physicalDevice, out PhysicalDeviceProperties properties);

            SamplerCreateInfo samplerInfo = new()
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = MagFilter,
                MinFilter = MinFilter,
                AddressModeU = UWrap,
                AddressModeV = VWrap,
                AddressModeW = WWrap,
                AnisotropyEnable = UseAniso,
                MaxAnisotropy = properties.Limits.MaxSamplerAnisotropy,
                BorderColor = BorderColor.IntOpaqueBlack,
                UnnormalizedCoordinates = false,
                CompareEnable = false,
                CompareOp = CompareOp.Always,
                MipmapMode = MipmapMode,
            };

            fixed (Sampler* textureSamplerPtr = &sampler)
            {
                if (Api!.CreateSampler(Renderer.device, ref samplerInfo, null, textureSamplerPtr) != Result.Success)
                    throw new Exception("Failed to create texture sampler.");
            }
        }

        private void CreateView()
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ViewType,
                Format = Format,
                Components =
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange =
                {
                    AspectMask = AspectFlags,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (Api!.CreateImageView(Device, ref createInfo, null, out view) != Result.Success)
                throw new Exception("Failed to create image views.");
        }

        private void TransitionImageLayout(ImageLayout oldLayout, ImageLayout newLayout)
        {
            AssembleTransitionImageLayout(
                oldLayout,
                newLayout,
                out ImageMemoryBarrier barrier,
                out PipelineStageFlags sourceStage,
                out PipelineStageFlags destinationStage);

            using var scope = Renderer.NewCommandScope();
            Api!.CmdPipelineBarrier(scope.CommandBuffer, sourceStage, destinationStage, 0, 0, null, 0, null, 1, ref barrier);
        }

        private void AssembleTransitionImageLayout(
            ImageLayout oldLayout,
            ImageLayout newLayout,
            out ImageMemoryBarrier barrier,
            out PipelineStageFlags sourceStage,
            out PipelineStageFlags destinationStage)
        {
            barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange =
                {
                    AspectMask = AspectFlags,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
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
                throw new Exception("Unsupported layout transition.");
        }

        private void CopyBufferToImage(Buffer buffer)
        {
            BufferImageCopy region = MakeCopyBufferToImageRegion();

            using var scope = Renderer.NewCommandScope();
            Api!.CmdCopyBufferToImage(scope.CommandBuffer, buffer, image, ImageLayout.TransferDstOptimal, 1, ref region);
        }

        private BufferImageCopy MakeCopyBufferToImageRegion() => new()
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource =
            {
                AspectMask = AspectFlags,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(Width, Height, Depth),
        };

        public DescriptorImageInfo CreateImageInfo() => new()
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = view,
            Sampler = sampler,
        };

        protected override void UnlinkData()
        {
            throw new NotImplementedException();
        }

        protected override void LinkData()
        {
            throw new NotImplementedException();
        }
    }
}