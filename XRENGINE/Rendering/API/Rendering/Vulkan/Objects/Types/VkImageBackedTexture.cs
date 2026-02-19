using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Abstract base class for Vulkan texture wrappers backed by a <see cref="Image"/>.
    /// Manages the full Vulkan resource lifecycle: image creation (either dedicated or via a
    /// resource-planner physical group), image-view and sampler creation, layout transitions,
    /// staging-buffer uploads, mipmap generation, and per-attachment view caching.
    /// <para>
    /// Concrete subclasses only need to implement <see cref="DescribeTexture"/> (to declare
    /// extents/layers/mips) and optionally override <see cref="PushTextureData"/> for
    /// type-specific upload logic.
    /// </para>
    /// </summary>
    /// <typeparam name="TTexture">The engine-side texture type (e.g. <see cref="XRTexture2D"/>).</typeparam>
    internal abstract class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
    {
        #region Fields

        /// <summary>Cache of per-attachment image views keyed by mip/layer/viewType/aspect.</summary>
        private readonly Dictionary<AttachmentViewKey, ImageView> _attachmentViews = new();

        /// <summary>Normalised texture dimensions, layers, and mip levels derived from <see cref="DescribeTexture"/>.</summary>
        private TextureLayout _layout;

        /// <summary>Whether <see cref="_layout"/> has been computed at least once.</summary>
        private bool _layoutInitialized;

        /// <summary>The Vulkan image handle (owned or borrowed from a physical group).</summary>
        private Image _image;

        /// <summary>Device memory backing <see cref="_image"/> when the image is dedicated (owned).</summary>
        private DeviceMemory _memory;

        /// <summary>Primary image view used for shader sampling.</summary>
        private ImageView _view;

        /// <summary>Sampler object (created when <see cref="CreateSampler"/> is <c>true</c>).</summary>
        private Sampler _sampler;

        /// <summary>
        /// <c>true</c> when this wrapper allocated the image and memory itself
        /// (as opposed to borrowing from a <see cref="VulkanPhysicalImageGroup"/>).
        /// </summary>
        private bool _ownsImageMemory;

        /// <summary>Non-null when the image comes from the resource planner's physical group allocator.</summary>
        private VulkanPhysicalImageGroup? _physicalGroup;

        // Per-field overrides applied when using a physical-group image whose dimensions/format
        // may differ from the logical texture description.
        private Extent3D? _extentOverride;
        private Format? _formatOverride;
        private uint? _arrayLayersOverride;
        private uint? _mipLevelsOverride;

        /// <summary>Tracks the most recent image layout so transitions use the correct source layout.</summary>
        protected ImageLayout _currentImageLayout = ImageLayout.Undefined;

        /// <summary>Tracks the currently allocated GPU memory size for this texture in bytes.</summary>
        private long _allocatedVRAMBytes = 0;

        #endregion

        #region Properties

        /// <inheritdoc />
        public override bool IsGenerated { get; }

        /// <summary>The raw Vulkan image handle.</summary>
        internal Image Image => _image;

        /// <summary>The primary image view for shader reads.</summary>
        internal ImageView View => _view;

        /// <summary>The texture sampler.</summary>
        internal Sampler Sampler => _sampler;

        /// <summary>The most recently tracked image layout for this texture.</summary>
        internal ImageLayout CurrentImageLayout
        {
            get
            {
                RefreshPhysicalGroupImageIfStale();
                if (_physicalGroup is not null)
                    _currentImageLayout = _physicalGroup.LastKnownLayout;
                return _currentImageLayout;
            }
        }

        /// <summary><c>true</c> when the image is borrowed from a resource-planner physical group.</summary>
        internal bool UsesAllocatorImage => _physicalGroup is not null;

        #endregion

        #region IVkImageDescriptorSource Implementation

        Image IVkImageDescriptorSource.DescriptorImage
        {
            get
            {
                // Ensure the cached handle is still valid if the planner rebuilt the image.
                RefreshPhysicalGroupImageIfStale();
                return _image;
            }
        }

        ImageView IVkImageDescriptorSource.DescriptorView
        {
            get
            {
                RefreshPhysicalGroupImageIfStale();
                return _view;
            }
        }

        Sampler IVkImageDescriptorSource.DescriptorSampler
        {
            get
            {
                RefreshPhysicalGroupImageIfStale();
                return _sampler;
            }
        }

        Format IVkImageDescriptorSource.DescriptorFormat
        {
            get
            {
                RefreshPhysicalGroupImageIfStale();
                return ResolvedFormat;
            }
        }

        ImageAspectFlags IVkImageDescriptorSource.DescriptorAspect
        {
            get
            {
                RefreshPhysicalGroupImageIfStale();
                return AspectFlags;
            }
        }

        ImageUsageFlags IVkImageDescriptorSource.DescriptorUsage
        {
            get
            {
                RefreshPhysicalGroupImageIfStale();
                return Usage;
            }
        }

        SampleCountFlags IVkImageDescriptorSource.DescriptorSamples
        {
            get
            {
                RefreshPhysicalGroupImageIfStale();
                return SampleCount;
            }
        }

        /// <inheritdoc />
        ImageView IVkImageDescriptorSource.GetDepthOnlyDescriptorView()
        {
            RefreshPhysicalGroupImageIfStale();

            // Build a view key for the full mip/layer range with only the DepthBit aspect.
            var key = new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, DefaultViewType, ImageAspectFlags.DepthBit);

            if (!_attachmentViews.TryGetValue(key, out ImageView cached))
            {
                cached = CreateView(key);
                _attachmentViews[key] = cached;
            }

            return cached;
        }

        /// <inheritdoc />
        ImageLayout IVkImageDescriptorSource.TrackedImageLayout
        {
            get
            {
                RefreshPhysicalGroupImageIfStale();
                if (_physicalGroup is not null)
                    return _physicalGroup.LastKnownLayout;
                return _currentImageLayout;
            }
        }

        /// <inheritdoc />
        bool IVkImageDescriptorSource.UsesAllocatorImage => _physicalGroup is not null;

        #endregion

        #region Resolved Properties

        /// <summary>Effective format, respecting any override from the physical group.</summary>
        protected internal Format ResolvedFormat => _formatOverride ?? Format;

        /// <summary>Effective extent, respecting any override from the physical group.</summary>
        protected Extent3D ResolvedExtent => _extentOverride ?? _layout.Extent;

        /// <summary>Effective array layer count.</summary>
        protected uint ResolvedArrayLayers => _arrayLayersOverride ?? _layout.ArrayLayers;

        /// <summary>Effective mip level count.</summary>
        protected uint ResolvedMipLevels => _mipLevelsOverride ?? _layout.MipLevels;

        /// <summary>Always single-sample for now.</summary>
        internal SampleCountFlags SampleCount => SampleCountFlags.Count1Bit;

        #endregion

        #region Configuration Properties

        /// <summary>Whether a <see cref="Silk.NET.Vulkan.Sampler"/> should be created alongside the image.</summary>
        public bool CreateSampler { get; set; } = true;

        /// <summary>Requested Vulkan format for the image.</summary>
        public Format Format { get; set; } = Format.R8G8B8A8Unorm;

        /// <summary>Memory property flags used when allocating dedicated image memory.</summary>
        public MemoryPropertyFlags MemoryProperties { get; set; } = MemoryPropertyFlags.DeviceLocalBit;

        /// <summary>Image tiling mode (optimal vs. linear).</summary>
        public ImageTiling Tiling { get; set; } = ImageTiling.Optimal;

        /// <summary>Combined usage flags applied to the Vulkan image.</summary>
        public ImageUsageFlags Usage { get; set; }

        /// <summary>Aspect flags (color, depth, stencil) for subresource selection.</summary>
        public ImageAspectFlags AspectFlags { get; set; }

        /// <summary>Default view type used for the primary image view (e.g. 2D, Cube, Array).</summary>
        public ImageViewType DefaultViewType { get; set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initialises the texture wrapper with default usage, aspect, and view-type from the
        /// concrete subclass's overrides.
        /// </summary>
        protected VkImageBackedTexture(VulkanRenderer api, TTexture data) : base(api, data)
        {
            Usage = DefaultUsage;
            AspectFlags = DefaultAspect;
            DefaultViewType = DefaultImageViewType;
        }

        #endregion

        #region VkObject Lifecycle

        /// <inheritdoc />
        /// <remarks>
        /// Subscribes to push-data, mipmap-generation, and resize events on the engine texture.
        /// </remarks>
        protected override void LinkData()
        {
            Data.PushDataRequested += OnPushDataRequested;
            Data.GenerateMipmapsRequested += OnGenerateMipmapsRequested;
            SubscribeResizeEvents();
        }

        /// <inheritdoc />
        /// <remarks>
        /// Unsubscribes all engine-texture events wired up in <see cref="LinkData"/>.
        /// </remarks>
        protected override void UnlinkData()
        {
            Data.PushDataRequested -= OnPushDataRequested;
            Data.GenerateMipmapsRequested -= OnGenerateMipmapsRequested;
            UnsubscribeResizeEvents();
        }

        /// <inheritdoc />
        /// <remarks>
        /// Computes the texture layout, acquires a Vulkan image (dedicated or from a physical group),
        /// creates the primary image view, and optionally a sampler.
        /// </remarks>
        protected override uint CreateObjectInternal()
        {
            RefreshLayout();
            AcquireImageHandle();
            CreateImageView(default);
            if (CreateSampler)
                CreateSamplerInternal();
            return CacheObject(this);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Destroys all owned Vulkan resources: sampler, image views, and (if dedicated) the
        /// image and its backing device memory. VRAM tracking stats are updated accordingly.
        /// </remarks>
        protected override void DeleteObjectInternal()
        {
            // Collect all Vulkan handles for deferred destruction.  In-flight
            // command buffers from other frame slots may still reference these
            // resources.  By retiring them to the current frame slot's queue, they
            // will be destroyed after the timeline fence for this slot signals.
            ImageView[] retiredAttachmentViews;
            if (_attachmentViews.Count > 0)
            {
                retiredAttachmentViews = new ImageView[_attachmentViews.Count];
                int idx = 0;
                foreach ((_, ImageView av) in _attachmentViews)
                    retiredAttachmentViews[idx++] = av;
            }
            else
            {
                retiredAttachmentViews = [];
            }

            Renderer.RetireImageResources(new RetiredImageResources(
                _ownsImageMemory ? _image : default,
                _ownsImageMemory ? _memory : default,
                _view,
                retiredAttachmentViews,
                _sampler,
                _ownsImageMemory ? _allocatedVRAMBytes : 0));

            // Report the VRAM deallocation to the stats tracker immediately
            // (the logical allocation is gone even if the GPU handle lingers).
            if (_ownsImageMemory && _allocatedVRAMBytes > 0)
            {
                Engine.Rendering.Stats.RemoveTextureAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }

            // Reset all cached handles and overrides.
            _view = default;
            _attachmentViews.Clear();
            _sampler = default;
            _image = default;
            _memory = default;
            _physicalGroup = null;
            _extentOverride = null;
            _formatOverride = null;
            _arrayLayersOverride = null;
            _mipLevelsOverride = null;
            _currentImageLayout = ImageLayout.Undefined;
        }

        #endregion

        #region Layout & Image Acquisition

        /// <summary>
        /// Computes the normalised texture layout from the subclass's <see cref="DescribeTexture"/>,
        /// resolves the Vulkan <see cref="Format"/> from the engine-side <c>SizedInternalFormat</c>,
        /// and ensures the aspect mask is valid for the resolved format.
        /// </summary>
        private void RefreshLayout()
        {
            _layout = NormalizeLayout(DescribeTexture());
            Format = ReadFormatFromData();
            AspectFlags = NormalizeAspectMaskForFormat(Format, AspectFlags);
            _layoutInitialized = true;
        }

        /// <summary>
        /// Clamps extent, layers, and mip levels to be at least 1.
        /// </summary>
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

        /// <summary>
        /// Acquires a Vulkan <see cref="Image"/> handle, either from the resource planner's
        /// physical group (shared allocation) or by creating a dedicated image with its own
        /// memory allocation.
        /// </summary>
        private void AcquireImageHandle()
        {
            if (!_layoutInitialized)
                RefreshLayout();

            if (TryResolvePhysicalGroup(out VulkanPhysicalImageGroup? group))
            {
                // Borrow the image from the resource-planner physical group.
                _physicalGroup = group;
                _image = group!.Image;
                _memory = group.Memory;
                _extentOverride = group.ResolvedExtent;
                _formatOverride = group.Format;
                Usage = group.Usage;
                // Preserve storage usage if the abstract texture requires it —
                // the resource planner may not know about out-of-graph compute dispatches.
                if (Data.RequiresStorageUsage)
                    Usage |= ImageUsageFlags.StorageBit;
                _arrayLayersOverride = Math.Max(group.Template.Layers, 1u);
                _mipLevelsOverride = 1;
                _ownsImageMemory = false;
                AspectFlags = NormalizeAspectMaskForFormat(ResolvedFormat, AspectFlags);
                _currentImageLayout = group.LastKnownLayout;
                return;
            }

            // No physical group available — create a dedicated image.
            // Adjust usage before creating: add storage bit if the engine texture requests it,
            // and swap color-attachment for depth-stencil-attachment when the format is depth/stencil.
            if (Data.RequiresStorageUsage)
                Usage |= ImageUsageFlags.StorageBit;
            bool isAttachmentTexture = Data.FrameBufferAttachment.HasValue;
            if (isAttachmentTexture)
            {
                if (VkFormatConversions.IsDepthStencilFormat(ResolvedFormat))
                {
                    Usage &= ~ImageUsageFlags.ColorAttachmentBit;
                    Usage |= ImageUsageFlags.DepthStencilAttachmentBit;
                }
                else
                {
                    Usage &= ~ImageUsageFlags.DepthStencilAttachmentBit;
                    Usage |= ImageUsageFlags.ColorAttachmentBit;
                }
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

        /// <summary>
        /// When backed by a resource-planner physical group, checks whether the group's
        /// VkImage handle has changed (e.g. because the planner rebuilt between frames)
        /// and updates the cached <see cref="_image"/> / <see cref="_memory"/> fields.
        /// Also recreates the primary ImageView for the new image.
        /// This prevents stale-handle segfaults in CmdBlitImage and other Vulkan commands.
        /// </summary>
        private void RefreshPhysicalGroupImageIfStale()
        {
            if (_physicalGroup is null)
                return;

            if (!_physicalGroup.IsAllocated)
            {
                // The physical group was destroyed — the resource planner may have rebuilt
                // between frames and replaced it with a brand-new group object.
                // Try to re-resolve from the allocator.
                VulkanPhysicalImageGroup? replacement = TryResolvePhysicalGroup(ensureAllocated: true);
                if (replacement is not null && replacement.IsAllocated)
                {
                    _physicalGroup = replacement;
                    // Fall through to the handle-update check below.
                }
                else
                {
                    // No replacement group available. Clear the stale handle so callers
                    // don't use a destroyed VkImage.
                    if (_image.Handle != 0)
                    {
                        _image = default;
                        _memory = default;
                    }
                    return;
                }
            }

            Image current = _physicalGroup.Image;
            if (current.Handle == _image.Handle)
            {
                _currentImageLayout = _physicalGroup.LastKnownLayout;
                return;
            }

            Debug.VulkanWarningEvery(
                $"Vulkan.StaleImageHandle.{ResolveLogicalResourceName() ?? "?"}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Physical group image handle changed for '{0}': 0x{1:X} → 0x{2:X}. Refreshing cached handle + view.",
                ResolveLogicalResourceName() ?? Data.Name ?? "<unnamed>",
                _image.Handle,
                current.Handle);

            _image = current;
            _memory = _physicalGroup.Memory;
            _extentOverride = _physicalGroup.ResolvedExtent;
            _formatOverride = _physicalGroup.Format;
            Usage = _physicalGroup.Usage;
            if (Data.RequiresStorageUsage)
                Usage |= ImageUsageFlags.StorageBit;

            // Recreate the primary view against the new image.
            DestroyView(ref _view);
            foreach ((_, ImageView attachmentView) in _attachmentViews)
            {
                if (attachmentView.Handle != 0)
                    Api!.DestroyImageView(Device, attachmentView, null);
            }
            _attachmentViews.Clear();
            CreateImageView(default);
            _currentImageLayout = _physicalGroup.LastKnownLayout;

            // The physical group may have been transitioned to an initial layout during
            // allocation (see TransitionNewPhysicalImagesToInitialLayout). Adopt that
            // layout so that subsequent barrier calculations use the correct old layout.
            // If it is still UNDEFINED the barrier planner will transition on first use.
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
                {
                    // The driver may have written a garbage handle to *imagePtr on failure
                    // (the spec says the output is undefined). Clear it so we don't
                    // accidentally use an invalid handle if the exception is caught.
                    _image = default;
                    throw new Exception($"Failed to create Vulkan image for texture '{ResolveLogicalResourceName() ?? Data.Name ?? "<unnamed>"}'. Result={result}.");
                }
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

            Debug.VulkanEvery(
                $"Vulkan.DedicatedTexture.{ResolveLogicalResourceName() ?? Data.Name ?? "unnamed"}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Dedicated texture image created: name='{0}' handle=0x{1:X} format={2} extent={3}x{4}x{5} usage={6}",
                ResolveLogicalResourceName() ?? Data.Name ?? "<unnamed>",
                _image.Handle,
                ResolvedFormat,
                ResolvedExtent.Width,
                ResolvedExtent.Height,
                ResolvedExtent.Depth,
                Usage);

            // Record the allocation for VRAM usage statistics.
            _allocatedVRAMBytes = (long)memRequirements.Size;
            Engine.Rendering.Stats.AddTextureAllocation(_allocatedVRAMBytes);
        }

        #endregion

        #region Image View Management

        /// <summary>
        /// Destroys the current primary view and creates a new one. When <paramref name="key"/>
        /// is <c>default</c>, builds a view covering all mip levels, all array layers, and using
        /// the <see cref="DefaultViewType"/>.
        /// </summary>
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

        /// <summary>
        /// Creates a Vulkan <see cref="ImageView"/> for the given subresource descriptor.
        /// The aspect mask is normalised to ensure depth/stencil formats don't include the
        /// color bit.
        /// </summary>
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

        /// <summary>
        /// Ensures the aspect mask is valid for the given format. Color formats get
        /// <see cref="ImageAspectFlags.ColorBit"/>; depth/stencil formats are restricted
        /// to their supported depth and/or stencil bits.
        /// </summary>
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

            if ((normalized & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == ImageAspectFlags.None)
                normalized = hasStencil ? ImageAspectFlags.DepthBit : supported;

            return normalized;
        }

        /// <summary>Destroys a single image view and resets the handle to <c>default</c>.</summary>
        private void DestroyView(ref ImageView view)
        {
            if (view.Handle != 0)
            {
                Api!.DestroyImageView(Device, view, null);
                view = default;
            }
        }

        /// <summary>Destroys the primary view and all cached attachment views.</summary>
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

        /// <summary>
        /// Returns a cached (or newly created) image view for a specific mip level and array
        /// layer, suitable for use as a framebuffer attachment. The default key falls back to
        /// the primary view.
        /// </summary>
        /// <param name="mipLevel">Mip level to target, or &lt;=0 for the base level.</param>
        /// <param name="layerIndex">Array layer index, or &lt;0 for the default layer range.</param>
        public ImageView GetAttachmentView(int mipLevel, int layerIndex)
        {
            RefreshPhysicalGroupImageIfStale();

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

        public void EnsureAttachmentLayout(bool depthStencil)
        {
            // Intentionally a no-op.  The render pass handles the initial layout
            // transition from Undefined → attachment-optimal via its initialLayout
            // field.  Performing a separate one-shot transition here would put the
            // image in attachment-optimal BEFORE the render pass begins, creating a
            // mismatch between the actual GPU layout and the declared initialLayout
            // (Undefined).  On NVIDIA GPUs this can corrupt Delta Color Compression
            // (DCC) metadata, leading to delayed TDR / VK_ERROR_DEVICE_LOST.
        }

        /// <summary>
        /// Builds the <see cref="AttachmentViewKey"/> for a given mip/layer combination.
        /// Subclasses override this to select the correct <see cref="ImageViewType"/> for their
        /// dimensionality (e.g. 2D for cube faces, 1D for 1D arrays).
        /// </summary>
        protected virtual AttachmentViewKey BuildAttachmentViewKey(int mipLevel, int layerIndex)
        {
            uint baseMip = (uint)Math.Max(mipLevel, 0);

            // Framebuffer attachments require single-mip-level views (levelCount=1).
            // Only reuse the default full-mip view when it already has exactly 1 level
            // and 1 layer — otherwise we must create a single-mip view.
            if (baseMip == 0 && layerIndex < 0 && ResolvedMipLevels <= 1 && ResolvedArrayLayers <= 1)
                return default;

            return new AttachmentViewKey(baseMip, 1, 0, 1, ImageViewType.Type2D, AspectFlags);
        }

        #endregion

        #region Sampler Management

        /// <summary>Destroys the sampler and resets the handle.</summary>
        private void DestroySampler()
        {
            if (_sampler.Handle != 0)
            {
                Api!.DestroySampler(Device, _sampler, null);
                _sampler = default;
            }
        }

        /// <summary>
        /// Resolves the Vulkan image format from engine texture data.
        /// For resizable textures with mip data, the first mip's <c>InternalFormat</c>
        /// is treated as authoritative; otherwise <c>SizedInternalFormat</c> is used.
        /// </summary>
        private Format ReadFormatFromData()
        {
            ESizedInternalFormat sizedFormat = ReadSizedFormatFromData();

            if (IsResizableTexture(Data)
                && TryReadFirstMipmapInternalFormat(Data, out EPixelInternalFormat mipInternalFormat))
            {
                Format mipDerivedFormat = VkFormatConversions.FromPixelInternalFormat(mipInternalFormat);
                if (mipDerivedFormat != Format.Undefined)
                    return mipDerivedFormat;
            }

            return VkFormatConversions.FromSizedFormat(sizedFormat);
        }

        /// <summary>
        /// Reads <c>SizedInternalFormat</c> from the concrete engine texture.
        /// </summary>
        private ESizedInternalFormat ReadSizedFormatFromData()
            => Data switch
            {
                XRTexture1D t => t.SizedInternalFormat,
                XRTexture1DArray t => t.SizedInternalFormat,
                XRTexture2D t => t.SizedInternalFormat,
                XRTexture2DArray t => t.SizedInternalFormat,
                XRTexture3D t => t.SizedInternalFormat,
                XRTextureCube t => t.SizedInternalFormat,
                XRTextureCubeArray t => t.SizedInternalFormat,
                XRTextureRectangle t => t.SizedInternalFormat,
                _ => ESizedInternalFormat.Rgba8,
            };

        /// <summary>
        /// Determines whether the concrete texture should be treated as resizable.
        /// </summary>
        private static bool IsResizableTexture(XRTexture texture)
            => texture switch
            {
                XRTexture1D t => t.Resizable,
                XRTexture1DArray t => t.Resizable,
                XRTexture2D t => t.Resizable,
                XRTexture2DArray t => t.Resizable,
                XRTexture3D t => t.Resizable,
                XRTextureCube t => t.Resizable,
                XRTextureCubeArray t => t.Resizable,
                XRTextureRectangle t => t.Resizable,
                _ => texture.IsResizeable,
            };

        /// <summary>
        /// Attempts to read the first mipmap's <see cref="EPixelInternalFormat"/> from the concrete texture.
        /// </summary>
        private static bool TryReadFirstMipmapInternalFormat(XRTexture texture, out EPixelInternalFormat internalFormat)
        {
            switch (texture)
            {
                case XRTexture1D t when t.Mipmaps is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].InternalFormat;
                    return true;

                case XRTexture1DArray t
                    when t.Textures is { Length: > 0 }
                         && t.Textures[0].Mipmaps is { Length: > 0 }:
                    internalFormat = t.Textures[0].Mipmaps[0].InternalFormat;
                    return true;

                case XRTexture2D t when t.Mipmaps is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].InternalFormat;
                    return true;

                case XRTexture2DArray t when t.Mipmaps is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].InternalFormat;
                    return true;

                case XRTexture3D t when t.Mipmaps is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].InternalFormat;
                    return true;

                case XRTextureCube t
                    when t.Mipmaps is { Length: > 0 }
                         && t.Mipmaps[0].Sides is { Length: > 0 }:
                    internalFormat = t.Mipmaps[0].Sides[0].InternalFormat;
                    return true;

                case XRTextureCubeArray t
                    when t.Cubes is { Length: > 0 }
                         && t.Cubes[0].Mipmaps is { Length: > 0 }
                         && t.Cubes[0].Mipmaps[0].Sides is { Length: > 0 }:
                    internalFormat = t.Cubes[0].Mipmaps[0].Sides[0].InternalFormat;
                    return true;
            }

            internalFormat = default;
            return false;
        }

        /// <summary>
        /// Reads sampler-related properties (filter, wrap, LOD bias) from the engine-side
        /// <see cref="Data"/> texture using pattern matching, since the XRTexture hierarchy
        /// does not expose these through a common interface. Values are converted to Vulkan
        /// types via <see cref="SamplerConversions"/>.
        /// </summary>
        private (Filter minFilter, Filter magFilter, SamplerMipmapMode mipmapMode,
                 SamplerAddressMode uWrap, SamplerAddressMode vWrap, SamplerAddressMode wWrap,
                 float lodBias) ReadSamplerSettingsFromData()
        {
            // Defaults when the concrete type doesn't expose a particular property.
            ETexMinFilter engineMin = ETexMinFilter.Linear;
            ETexMagFilter engineMag = ETexMagFilter.Linear;
            ETexWrapMode  engineU   = ETexWrapMode.Repeat;
            ETexWrapMode  engineV   = ETexWrapMode.Repeat;
            ETexWrapMode  engineW   = ETexWrapMode.Repeat;
            float         lodBias   = 0f;

            switch (Data)
            {
                case XRTexture1D t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; lodBias = t.LodBias;
                    break;
                case XRTexture1DArray t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; lodBias = t.LodBias;
                    break;
                case XRTexture2D t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; lodBias = t.LodBias;
                    break;
                case XRTexture2DArray t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap;
                    break;
                case XRTexture3D t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; engineW = t.WWrap; lodBias = t.LodBias;
                    break;
                case XRTextureCube t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; engineW = t.WWrap; lodBias = t.LodBias;
                    break;
                case XRTextureCubeArray t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; engineW = t.WWrap; lodBias = t.LodBias;
                    break;
                case XRTextureRectangle t:
                    engineMin = t.MinFilter; engineMag = t.MagFilter;
                    engineU = t.UWrap; engineV = t.VWrap; lodBias = t.LodBias;
                    break;
            }

            var (minFilter, mipmapMode) = SamplerConversions.FromMinFilter(engineMin);
            Filter magFilter = SamplerConversions.FromMagFilter(engineMag);

            return (minFilter, magFilter, mipmapMode,
                    SamplerConversions.FromWrap(engineU),
                    SamplerConversions.FromWrap(engineV),
                    SamplerConversions.FromWrap(engineW),
                    lodBias);
        }

        /// <summary>
        /// Creates a Vulkan <see cref="Silk.NET.Vulkan.Sampler"/> by reading filter, wrap, and
        /// mipmap settings from the engine-side <see cref="Data"/> texture. Anisotropic filtering
        /// is enabled when the device supports it.
        /// </summary>
        private void CreateSamplerInternal()
        {
            DestroySampler();

            // Read sampler settings from the engine-side XRTexture data source.
            var (minFilter, magFilter, mipmapMode, uWrap, vWrap, wWrap, lodBias) = ReadSamplerSettingsFromData();

            // Determine whether anisotropic filtering is available.
            var anisotropyEnable = Vk.False;
            float maxAnisotropy = 1f;
            if (Renderer.SamplerAnisotropyEnabled)
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
                MagFilter = magFilter,
                MinFilter = minFilter,
                AddressModeU = uWrap,
                AddressModeV = vWrap,
                AddressModeW = wWrap,
                AnisotropyEnable = anisotropyEnable,
                MaxAnisotropy = maxAnisotropy,
                BorderColor = BorderColor.IntOpaqueBlack,
                UnnormalizedCoordinates = Vk.False,
                CompareEnable = Vk.False,
                CompareOp = CompareOp.Always,
                MipmapMode = mipmapMode,
                MipLodBias = lodBias,
                MinLod = 0f,
                MaxLod = ResolvedMipLevels,
            };

            if (Api!.CreateSampler(Device, ref samplerInfo, null, out _sampler) != Result.Success)
                throw new Exception("Failed to create sampler.");
        }

        #endregion

        #region Image Layout Transitions

        /// <summary>
        /// Performs a full pipeline barrier to transition the image from <paramref name="oldLayout"/>
        /// to <paramref name="newLayout"/>. Layouts are first coerced to be valid for the
        /// image's actual usage flags.
        /// </summary>
        internal void TransitionImageLayout(ImageLayout oldLayout, ImageLayout newLayout)
        {
            RefreshPhysicalGroupImageIfStale();

            oldLayout = CoerceLayoutForUsage(oldLayout);
            newLayout = CoerceLayoutForUsage(newLayout);
            AssembleTransitionImageLayout(oldLayout, newLayout, out ImageMemoryBarrier barrier, out PipelineStageFlags src, out PipelineStageFlags dst);
            using var scope = Renderer.NewCommandScope();
            Api!.CmdPipelineBarrier(scope.CommandBuffer, src, dst, 0, 0, null, 0, null, 1, ref barrier);
            _currentImageLayout = newLayout;
            if (_physicalGroup is not null)
                _physicalGroup.LastKnownLayout = newLayout;
        }

        /// <summary>
        /// Coerces <see cref="ImageLayout.ShaderReadOnlyOptimal"/> to a valid layout when the
        /// image lacks <see cref="ImageUsageFlags.SampledBit"/>. Falls back to
        /// <see cref="ImageLayout.General"/> (if storage) or <see cref="ImageLayout.TransferSrcOptimal"/>.
        /// </summary>
        private ImageLayout CoerceLayoutForUsage(ImageLayout requested)
        {
            if (requested != ImageLayout.ShaderReadOnlyOptimal)
                return requested;

            if ((Usage & ImageUsageFlags.StorageBit) != 0)
                return ImageLayout.General;

            bool canSample = (Usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0;
            if (canSample)
                return requested;

            return ImageLayout.TransferSrcOptimal;
        }

        /// <summary>
        /// Builds the <see cref="ImageMemoryBarrier"/> and selects appropriate pipeline stages
        /// for transitioning from <paramref name="oldLayout"/> to <paramref name="newLayout"/>.
        /// Common transitions (undefined→transfer-dst, transfer-dst→shader-read) use precise
        /// stages; all others fall back to <c>AllCommands</c>.
        /// </summary>
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

        #endregion

        #region Buffer-to-Image Transfer

        /// <summary>
        /// Copies pixel data from <paramref name="buffer"/> into a specific mip level and array
        /// layer range of the image. Prefers NV indirect copy when available; otherwise falls
        /// back to <c>vkCmdCopyBufferToImage</c>, using a dedicated transfer queue with
        /// queue-family ownership barriers when the device exposes one.
        /// </summary>
        /// <param name="buffer">Staging buffer containing the pixel data.</param>
        /// <param name="mipLevel">Target mip level.</param>
        /// <param name="baseArrayLayer">First array layer to write.</param>
        /// <param name="layerCount">Number of array layers to write.</param>
        /// <param name="extent">Pixel extent of the target mip level.</param>
        /// <param name="stagingBufferSize">Size in bytes of the staging buffer. When non-zero,
        /// the method validates that the buffer is large enough for the target image format
        /// and logs an error (skipping the copy) if there is a mismatch.</param>
        protected void CopyBufferToImage(Buffer buffer, uint mipLevel, uint baseArrayLayer, uint layerCount, Extent3D extent, ulong stagingBufferSize = 0)
        {
            // Validate staging buffer size against what the GPU will actually read.
            if (stagingBufferSize > 0)
            {
                uint bpt = VkFormatConversions.GetBytesPerTexel(ResolvedFormat);
                if (bpt > 0)
                {
                    ulong requiredBytes = (ulong)extent.Width * extent.Height * extent.Depth * layerCount * bpt;
                    if (stagingBufferSize < requiredBytes)
                    {
                        Debug.LogError(
                            $"[Vulkan] Staging buffer size mismatch for '{Data.Name ?? GetDescribingName()}': " +
                            $"buffer={stagingBufferSize} bytes but image format {ResolvedFormat} requires " +
                            $"{requiredBytes} bytes ({extent.Width}x{extent.Height}x{extent.Depth} * {layerCount} layers * {bpt} bpp). " +
                            $"Skipping CopyBufferToImage to avoid GPU out-of-bounds read. " +
                            $"Check that the texture's SizedInternalFormat matches its pixel data.");
                        return;
                    }
                }
            }

            if (_currentImageLayout != ImageLayout.TransferDstOptimal)
                TransitionImageLayout(_currentImageLayout, ImageLayout.TransferDstOptimal);

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

            // First, try the fast NV indirect copy path.
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

            // Determine whether a dedicated transfer queue family is available.
            QueueFamilyIndices queueFamilies = Renderer.FamilyQueueIndices;
            uint graphicsFamily = queueFamilies.GraphicsFamilyIndex ?? 0u;
            uint transferFamily = queueFamilies.TransferFamilyIndex ?? graphicsFamily;
            bool dedicatedTransferFamily = transferFamily != graphicsFamily;

            if (dedicatedTransferFamily)
                Api!.QueueWaitIdle(Renderer.GraphicsQueue);

            using (var transferScope = Renderer.NewTransferCommandScope())
            {
                if (dedicatedTransferFamily)
                {
                    ImageMemoryBarrier acquireBarrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                        DstAccessMask = AccessFlags.TransferWriteBit,
                        OldLayout = ImageLayout.TransferDstOptimal,
                        NewLayout = ImageLayout.TransferDstOptimal,
                        SrcQueueFamilyIndex = graphicsFamily,
                        DstQueueFamilyIndex = transferFamily,
                        Image = _image,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = AspectFlags,
                            BaseMipLevel = mipLevel,
                            LevelCount = 1,
                            BaseArrayLayer = baseArrayLayer,
                            LayerCount = layerCount,
                        }
                    };

                    Api!.CmdPipelineBarrier(
                        transferScope.CommandBuffer,
                        PipelineStageFlags.AllCommandsBit,
                        PipelineStageFlags.TransferBit,
                        DependencyFlags.None,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &acquireBarrier);
                }

                Api!.CmdCopyBufferToImage(transferScope.CommandBuffer, buffer, _image, ImageLayout.TransferDstOptimal, 1, ref region);

                if (dedicatedTransferFamily)
                {
                    ImageMemoryBarrier releaseBarrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.TransferWriteBit,
                        DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                        OldLayout = ImageLayout.TransferDstOptimal,
                        NewLayout = ImageLayout.TransferDstOptimal,
                        SrcQueueFamilyIndex = transferFamily,
                        DstQueueFamilyIndex = graphicsFamily,
                        Image = _image,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = AspectFlags,
                            BaseMipLevel = mipLevel,
                            LevelCount = 1,
                            BaseArrayLayer = baseArrayLayer,
                            LayerCount = layerCount,
                        }
                    };

                    Api!.CmdPipelineBarrier(
                        transferScope.CommandBuffer,
                        PipelineStageFlags.TransferBit,
                        PipelineStageFlags.AllCommandsBit,
                        DependencyFlags.None,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &releaseBarrier);
                }
            }

            if (dedicatedTransferFamily)
            {
                using var graphicsScope = Renderer.NewCommandScope();
                ImageMemoryBarrier acquireOnGraphics = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = transferFamily,
                    DstQueueFamilyIndex = graphicsFamily,
                    Image = _image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = AspectFlags,
                        BaseMipLevel = mipLevel,
                        LevelCount = 1,
                        BaseArrayLayer = baseArrayLayer,
                        LayerCount = layerCount,
                    }
                };

                Api!.CmdPipelineBarrier(
                    graphicsScope.CommandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.AllCommandsBit,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &acquireOnGraphics);
            }
        }

        #endregion

        #region Descriptor Helpers

        /// <summary>
        /// Convenience method to build a <see cref="DescriptorImageInfo"/> for this texture
        /// using the primary view, sampler, and <see cref="ImageLayout.ShaderReadOnlyOptimal"/>.
        /// </summary>
        public DescriptorImageInfo CreateImageInfo()
        {
            RefreshPhysicalGroupImageIfStale();
            return new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = _view,
                Sampler = _sampler,
            };
        }

        #endregion

        #region Abstract & Virtual Members

        /// <summary>Default usage flags for new images. Subclasses may override.</summary>
        protected virtual ImageUsageFlags DefaultUsage => ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit;

        /// <summary>Default aspect flags (color). Depth textures override this.</summary>
        protected virtual ImageAspectFlags DefaultAspect => ImageAspectFlags.ColorBit;

        /// <summary>Default image-view type for the primary view.</summary>
        protected virtual ImageViewType DefaultImageViewType => ImageViewType.Type2D;

        /// <summary>Vulkan image type (1D, 2D, 3D). Overridden by 1D and 3D subclasses.</summary>
        protected virtual ImageType TextureImageType => ImageType.Type2D;

        /// <summary>Additional <see cref="ImageCreateFlags"/> (e.g. <c>CubeCompatible</c>). Default is none.</summary>
        protected virtual ImageCreateFlags AdditionalImageFlags => 0;

        /// <summary>
        /// Returns the texture's logical dimensions, array layer count, and mip level count.
        /// Implemented by each concrete texture type.
        /// </summary>
        protected abstract TextureLayout DescribeTexture();

        /// <summary>Describes a texture's extent, array layers, and mip levels.</summary>
        protected internal readonly record struct TextureLayout(Extent3D Extent, uint ArrayLayers, uint MipLevels);

        /// <summary>Key identifying a unique image-view configuration for attachment use.</summary>
        protected internal readonly record struct AttachmentViewKey(uint BaseMipLevel, uint LevelCount, uint BaseArrayLayer, uint LayerCount, ImageViewType ViewType, ImageAspectFlags AspectMask);

        /// <summary>
        /// Uploads texture pixel data to the GPU via staging buffers.
        /// The default implementation logs a warning; concrete types override this.
        /// </summary>
        protected virtual void PushTextureData()
        {
            Debug.VulkanWarning($"{GetType().Name} does not implement texture data uploads yet.");
        }

        /// <summary>
        /// Generates mipmaps on the GPU. Defaults to <see cref="GenerateMipmapsWithBlit"/>.
        /// </summary>
        protected virtual void GenerateMipmapsGPU()
            => GenerateMipmapsWithBlit();

        #endregion

        #region Event Handlers

        /// <summary>
        /// Deferred handler for push-data requests from the engine texture.
        /// Ensures execution on the main thread before uploading.
        /// </summary>
        private void OnPushDataRequested()
        {
            if (Engine.InvokeOnMainThread(OnPushDataRequested, "VkTexture2D.PushData"))
                return;

            PushTextureData();
        }

        /// <summary>
        /// Deferred handler for mipmap-generation requests from the engine texture.
        /// Ensures execution on the main thread.
        /// </summary>
        private void OnGenerateMipmapsRequested()
        {
            if (Engine.InvokeOnMainThread(OnGenerateMipmapsRequested, "VkTexture2D.GenerateMipmaps"))
                return;

            GenerateMipmapsGPU();
        }

        /// <summary>
        /// Subscribes to the <c>Resized</c> event on the specific engine-texture subtype so
        /// that Vulkan resources are recreated when the texture dimensions change.
        /// </summary>
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

        /// <summary>
        /// Unsubscribes from the <c>Resized</c> event on the specific engine-texture subtype.
        /// </summary>
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

        /// <summary>
        /// Called when the engine texture is resized. Destroys all Vulkan resources so they
        /// will be recreated with the new dimensions on the next <see cref="Generate"/> call.
        /// </summary>
        private void OnTextureResized()
        {
            Destroy();
            _layoutInitialized = false;
            _currentImageLayout = ImageLayout.Undefined;
        }

        #endregion

        #region Staging Buffers

        /// <summary>
        /// Creates a host-visible staging buffer and copies <paramref name="data"/> into it.
        /// When the NV indirect-copy extension is available, the buffer is also given
        /// <see cref="BufferUsageFlags.ShaderDeviceAddressBit"/> for indirect transfer support.
        /// </summary>
        /// <param name="data">Source pixel data to upload.</param>
        /// <param name="buffer">The created staging buffer handle.</param>
        /// <param name="memory">The staging buffer's device memory.</param>
        /// <returns><c>true</c> if the buffer was created; <c>false</c> if <paramref name="data"/> is null or empty.</returns>
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

        /// <summary>
        /// Releases a staging buffer and its associated device memory.
        /// </summary>
        /// <param name="buffer">The staging buffer to destroy.</param>
        /// <param name="memory">The device memory backing the buffer.</param>
        protected void DestroyStagingBuffer(Buffer buffer, DeviceMemory memory)
            => Renderer.DestroyBuffer(buffer, memory);

        #endregion

        #region Mipmap Generation

        /// <summary>
        /// Generates a full mipmap chain for the current image using <c>vkCmdBlitImage</c>.
        /// Each mip level is transitioned from <see cref="ImageLayout.TransferDstOptimal"/> to
        /// <see cref="ImageLayout.TransferSrcOptimal"/>, blitted to the next smaller level,
        /// then transitioned to <see cref="ImageLayout.ShaderReadOnlyOptimal"/>.
        /// The final mip level is transitioned directly.
        /// </summary>
        /// <remarks>
        /// This method verifies that the image format supports linear blitting. If it does not,
        /// a warning is emitted and the image is transitioned to shader-read without mips.
        /// </remarks>
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
            if (_physicalGroup is not null)
                _physicalGroup.LastKnownLayout = ImageLayout.ShaderReadOnlyOptimal;
        }

        /// <summary>
        /// Builds an <see cref="ImageBlit"/> descriptor that copies from mip level
        /// <paramref name="targetLevel"/> − 1 to <paramref name="targetLevel"/>, halving
        /// the width and height (clamped to 1).
        /// </summary>
        /// <param name="targetLevel">The destination mip level (source is <c>targetLevel − 1</c>).</param>
        /// <param name="mipWidth">Width of the source mip level.</param>
        /// <param name="mipHeight">Height of the source mip level.</param>
        /// <returns>A configured <see cref="ImageBlit"/> ready for <c>CmdBlitImage</c>.</returns>
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

        #endregion
    }
}
