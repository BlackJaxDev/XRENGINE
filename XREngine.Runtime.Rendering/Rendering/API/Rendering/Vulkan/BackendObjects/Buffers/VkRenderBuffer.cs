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

        /// <summary>
        /// Tracks the currently allocated GPU memory size for this render buffer in bytes.
        /// </summary>
        private long _allocatedVRAMBytes = 0;

        internal Image Image => _image;

        /// <summary>
        /// The physical image group backing this render buffer, or null if the buffer
        /// owns its image directly (no aliasing / resource allocator integration).
        /// </summary>
        internal VulkanPhysicalImageGroup? PhysicalGroup => _physicalGroup;

        /// <summary>
        /// If this render buffer uses a physical-group-backed image, checks whether the
        /// group has reallocated and refreshes the cached image/memory/view handles.
        /// </summary>
        internal void RefreshIfStale()
        {
            if (_physicalGroup is null)
                return;

            bool physicalGroupChanged = false;
            if (!string.IsNullOrWhiteSpace(Data.Name) &&
                Renderer.ResourceAllocator.TryGetPhysicalGroupForResource(Data.Name, out VulkanPhysicalImageGroup? activeGroup) &&
                activeGroup is not null &&
                !ReferenceEquals(activeGroup, _physicalGroup))
            {
                _physicalGroup = activeGroup;
                physicalGroupChanged = true;
            }

            if (!_physicalGroup.IsAllocated)
            {
                // The physical group was destroyed — the resource planner may have rebuilt
                // between frames and replaced it with a brand-new group object.
                // Try to re-resolve from the allocator.
                if (!string.IsNullOrWhiteSpace(Data.Name) &&
                    Renderer.ResourceAllocator.TryGetPhysicalGroupForResource(Data.Name, out VulkanPhysicalImageGroup? replacement) &&
                    replacement is not null)
                {
                    physicalGroupChanged |= !ReferenceEquals(replacement, _physicalGroup);
                    _physicalGroup = replacement;
                    // Fall through to EnsureAllocated + handle check below.
                }
                else
                {
                    // No replacement group available. Clear the stale handle so callers
                    // don't use a destroyed VkImage.
                    if (_image.Handle != 0)
                    {
                        RetireView();
                        _image = default;
                        _memory = default;
                    }
                    return;
                }
            }

            _physicalGroup.EnsureAllocated(Renderer);
            Format expectedFormat = _physicalGroup.Format;
            ImageAspectFlags expectedAspect = ResolveAspect(Data.Type);
            bool viewMetadataChanged =
                _formatOverride != expectedFormat ||
                _aspectOverride != expectedAspect ||
                physicalGroupChanged;

            if (_physicalGroup.Image.Handle == _image.Handle)
            {
                _formatOverride = expectedFormat;
                _aspectOverride = expectedAspect;
                _samplesOverride = _physicalGroup.Samples;
                if (viewMetadataChanged && _view.Handle != 0)
                    CreateImageView();
                return;
            }

            // Physical group was reallocated — refresh our cached handles.
            RetireView();

            _image = _physicalGroup.Image;
            _memory = _physicalGroup.Memory;
            _formatOverride = expectedFormat;
            _aspectOverride = expectedAspect;
            _samplesOverride = _physicalGroup.Samples;

            CreateImageView();
        }

        public override VkObjectType Type => VkObjectType.Renderbuffer;
        public override bool IsGenerated => IsActive;

        internal ImageView View => _view;
        internal Format Format => _formatOverride ?? ResolveFormat(Data.Type);
        internal ImageAspectFlags Aspect => _aspectOverride ?? ResolveAspect(Data.Type);
        internal SampleCountFlags Samples => _samplesOverride ?? ResolveSamples(Data.MultisampleCount);

        internal Extent2D ResolveAttachmentExtent()
        {
            RefreshIfStale();
            if (_physicalGroup is not null)
            {
                Extent3D extent = _physicalGroup.ResolvedExtent;
                if (extent.Width > 0 && extent.Height > 0)
                    return new Extent2D(extent.Width, extent.Height);
            }

            return new Extent2D(Math.Max(Data.Width, 1u), Math.Max(Data.Height, 1u));
        }

        protected override uint CreateObjectInternal()
        {
            AcquireImage();
            CreateImageView();
            return CacheObject(this);
        }

        protected override void DeleteObjectInternal()
        {
            ImageView retiredView = _view;
            Image retiredImage = _ownsImage ? _image : default;
            DeviceMemory retiredMemory = _ownsImage ? _memory : default;

            if (_ownsImage && _allocatedVRAMBytes > 0)
            {
                RuntimeEngine.Rendering.Stats.Vram.RemoveRenderBufferAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }

            if (retiredView.Handle != 0 || retiredImage.Handle != 0 || retiredMemory.Handle != 0)
            {
                Renderer.RetireImageResources(new RetiredImageResources(
                    retiredImage,
                    retiredMemory,
                    retiredView,
                    [],
                    default,
                    0));
            }

            _view = default;
            _image = default;
            _memory = default;
            _physicalGroup = null;
            _formatOverride = null;
            _aspectOverride = null;
            _samplesOverride = null;
        }

        private void AcquireImage()
        {
            if (!string.IsNullOrWhiteSpace(Data.Name)
                && Renderer.ResourceAllocator.TryGetPhysicalGroupForResource(Data.Name, out VulkanPhysicalImageGroup? group)
                && group is not null)
            {
                group.EnsureAllocated(Renderer);
                _physicalGroup = group;
                _image = group.Image;
                _memory = group.Memory;
                _ownsImage = false;
                _formatOverride = group.Format;
                _aspectOverride = ResolveAspect(Data.Type);
                _samplesOverride = group.Samples;
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

            Renderer.ClearTrackedImageLayouts(_image);
            VulkanMemoryAllocation allocation = Renderer.AllocateImageMemoryWithFallback(_image, MemoryPropertyFlags.DeviceLocalBit);
            Renderer._imageAllocations[_image.Handle] = allocation;
            _memory = allocation.Memory;

            if (Api!.BindImageMemory(Device, _image, _memory, allocation.Offset) != Result.Success)
            {
                Renderer._imageAllocations.TryRemove(_image.Handle, out _);
                Renderer.FreeMemoryAllocation(allocation);
                throw new Exception("Failed to bind memory for render buffer image.");
            }

            Debug.VulkanEvery(
                $"Vulkan.DedicatedRenderBuffer.{Data.Name ?? "unnamed"}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Dedicated render-buffer image created: name='{0}' handle=0x{1:X} format={2} extent={3}x{4} usage={5}",
                Data.Name ?? "<unnamed>",
                _image.Handle,
                format,
                Math.Max(Data.Width, 1u),
                Math.Max(Data.Height, 1u),
                info.Usage);

            // Track VRAM allocation
            _allocatedVRAMBytes = (long)allocation.Size;
            RuntimeEngine.Rendering.Stats.Vram.AddRenderBufferAllocation(_allocatedVRAMBytes);
        }

        private void CreateImageView()
        {
            RetireView();

            Format viewFormat = Format;
            ImageAspectFlags viewAspect = Aspect;
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = ImageViewType.Type2D,
                Format = viewFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = viewAspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (Api!.CreateImageView(Device, ref viewInfo, null, out _view) != Result.Success)
                throw new Exception("Failed to create render buffer image view.");
            Renderer.TrackLiveImageView(_view, in viewInfo, "VkRenderBuffer.View");
        }

        private void RetireView()
        {
            if (_view.Handle == 0)
                return;

            Renderer.RetireImageResources(new RetiredImageResources(
                default,
                default,
                _view,
                [],
                default,
                0));
            _view = default;
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

        private void AllocateCompat()
        {
            if (RuntimeEngine.InvokeOnMainThread(AllocateCompat, "VkRenderBuffer.AllocateCompat"))
                return;

            if (Renderer.IsDeviceLost)
                return;

            Debug.VulkanEvery(
                $"Vulkan.Compat.RenderBuffer.Allocate.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] XRRenderBuffer.Allocate requested for '{0}'. Vulkan render buffers are VkImages; this invalidates/recreates the image so GL-style Allocate callers keep working. Preferred Vulkan path: update XRFrameBuffer targets/resources and let the render graph or VkFrameBuffer allocate attachments.",
                Data.Name ?? Data.GetDescribingName());

            bool wasGenerated = IsGenerated;
            Destroy();
            if (wasGenerated)
                Generate();
        }

        private void BindCompat()
        {
            if (RuntimeEngine.InvokeOnMainThread(BindCompat, "VkRenderBuffer.BindCompat"))
                return;

            if (Renderer.IsDeviceLost)
                return;

            Debug.VulkanEvery(
                $"Vulkan.Compat.RenderBuffer.Bind.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] XRRenderBuffer.Bind requested for '{0}'. Vulkan has no GL renderbuffer bind state; this only ensures the VkImage/view exists. Preferred Vulkan path: attach through XRFrameBuffer.SetRenderTargets or render-graph attachment declarations.",
                Data.Name ?? Data.GetDescribingName());

            Generate();
            RefreshIfStale();
        }

        private void UnbindCompat()
            => Debug.VulkanEvery(
                $"Vulkan.Compat.RenderBuffer.Unbind.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] XRRenderBuffer.Unbind requested for '{0}' is a no-op. Vulkan attachment state is framebuffer/pass owned.",
                Data.Name ?? Data.GetDescribingName());

        private void AttachToFBOCompat(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel)
        {
            if (RuntimeEngine.InvokeOnMainThread(() => AttachToFBOCompat(target, attachment, mipLevel), "VkRenderBuffer.AttachToFBOCompat"))
                return;

            if (Renderer.IsDeviceLost)
                return;

            bool addedTarget = AddTargetIfMissing(target, attachment, mipLevel);
            InvalidateFrameBuffer(target);
            Generate();

            Debug.VulkanWarningEvery(
                $"Vulkan.Compat.RenderBuffer.Attach.{Data.GetHashCode()}.{target.GetHashCode()}.{attachment}.{mipLevel}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] XRRenderBuffer.AttachToFBO requested for renderbuffer '{0}' -> framebuffer '{1}' attachment={2} mip={3}. {4} Preferred Vulkan path: call XRFrameBuffer.SetRenderTargets with this renderbuffer before the pass is built.",
                Data.Name ?? Data.GetDescribingName(),
                target.Name ?? target.GetDescribingName(),
                attachment,
                mipLevel,
                addedTarget ? "The XRFrameBuffer target list was patched and the Vulkan framebuffer was invalidated." : "The renderbuffer was already present in XRFrameBuffer targets; the Vulkan framebuffer was invalidated.");
        }

        private void DetachFromFBOCompat(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel)
        {
            if (RuntimeEngine.InvokeOnMainThread(() => DetachFromFBOCompat(target, attachment, mipLevel), "VkRenderBuffer.DetachFromFBOCompat"))
                return;

            bool removedTarget = RemoveTargetIfPresent(target, attachment, mipLevel);
            InvalidateFrameBuffer(target);

            Debug.VulkanWarningEvery(
                $"Vulkan.Compat.RenderBuffer.Detach.{Data.GetHashCode()}.{target.GetHashCode()}.{attachment}.{mipLevel}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] XRRenderBuffer.DetachFromFBO requested for renderbuffer '{0}' -> framebuffer '{1}' attachment={2} mip={3}. {4} Preferred Vulkan path: remove or replace the target through XRFrameBuffer.SetRenderTargets before pass construction.",
                Data.Name ?? Data.GetDescribingName(),
                target.Name ?? target.GetDescribingName(),
                attachment,
                mipLevel,
                removedTarget ? "The XRFrameBuffer target list was patched and the Vulkan framebuffer was invalidated." : "No matching XRFrameBuffer target was present; only the Vulkan framebuffer was invalidated.");
        }

        private bool AddTargetIfMissing(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel)
        {
            var currentTargets = target.Targets;
            if (currentTargets is not null)
            {
                for (int i = 0; i < currentTargets.Length; i++)
                {
                    var current = currentTargets[i];
                    if (ReferenceEquals(current.Target, Data)
                        && current.Attachment == attachment
                        && current.MipLevel == mipLevel)
                    {
                        return false;
                    }
                }
            }

            int existingLength = currentTargets?.Length ?? 0;
            var newTargets = new (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[existingLength + 1];
            if (currentTargets is not null)
                Array.Copy(currentTargets, newTargets, currentTargets.Length);
            newTargets[existingLength] = (Data, attachment, mipLevel, -1);
            target.SetRenderTargets(newTargets);
            return true;
        }

        private bool RemoveTargetIfPresent(XRFrameBuffer target, EFrameBufferAttachment attachment, int mipLevel)
        {
            var currentTargets = target.Targets;
            if (currentTargets is null || currentTargets.Length == 0)
                return false;

            int removeCount = 0;
            for (int i = 0; i < currentTargets.Length; i++)
            {
                var current = currentTargets[i];
                if (ReferenceEquals(current.Target, Data)
                    && current.Attachment == attachment
                    && current.MipLevel == mipLevel)
                {
                    removeCount++;
                }
            }

            if (removeCount == 0)
                return false;

            if (removeCount == currentTargets.Length)
            {
                target.SetRenderTargets(((IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]?)null);
                return true;
            }

            var newTargets = new (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[currentTargets.Length - removeCount];
            int writeIndex = 0;
            for (int i = 0; i < currentTargets.Length; i++)
            {
                var current = currentTargets[i];
                if (ReferenceEquals(current.Target, Data)
                    && current.Attachment == attachment
                    && current.MipLevel == mipLevel)
                {
                    continue;
                }

                newTargets[writeIndex++] = current;
            }

            target.SetRenderTargets(newTargets);
            return true;
        }

        private void InvalidateFrameBuffer(XRFrameBuffer target)
        {
            if (Renderer.GetOrCreateAPIRenderObject(target, generateNow: false) is VkFrameBuffer vkFrameBuffer && vkFrameBuffer.IsGenerated)
                vkFrameBuffer.Destroy();
        }

        protected override void LinkData()
        {
            Data.AllocateRequested += AllocateCompat;
            Data.BindRequested += BindCompat;
            Data.UnbindRequested += UnbindCompat;
            Data.AttachToFBORequested += AttachToFBOCompat;
            Data.DetachFromFBORequested += DetachFromFBOCompat;
        }

        protected override void UnlinkData()
        {
            Data.AllocateRequested -= AllocateCompat;
            Data.BindRequested -= BindCompat;
            Data.UnbindRequested -= UnbindCompat;
            Data.AttachToFBORequested -= AttachToFBOCompat;
            Data.DetachFromFBORequested -= DetachFromFBOCompat;
        }
    }
}
