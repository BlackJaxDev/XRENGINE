using Silk.NET.Vulkan;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Core;
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

        /// <summary>
        /// Per physical-image view cache used when serial desktop/eye rendering switches resource-planner
        /// contexts for the same logical texture. Context switches should restore the matching views instead
        /// of retiring/recreating them every pass; true same-group reallocations still retire stale views.
        /// </summary>
        private readonly List<PhysicalImageViewCacheEntry> _physicalImageViewCache = [];

        private readonly object _imageStateLock = new();

        /// <summary>Layout tracking for framebuffer writes that touch only one mip/layer at a time.</summary>
        private readonly Dictionary<AttachmentLayoutKey, ImageLayout> _attachmentLayouts = new();

        /// <summary>
        /// Set after a render pass writes only part of the image. When active, unknown
        /// attachment mips/layers must stay Undefined instead of inheriting the whole-image layout.
        /// </summary>
        private bool _hasPartialAttachmentLayouts;

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
        private SampleCountFlags? _samplesOverride;

        /// <summary>Tracks the most recent image layout so transitions use the correct source layout.</summary>
        protected ImageLayout _currentImageLayout = ImageLayout.Undefined;

        /// <summary>Tracks the currently allocated GPU memory size for this texture in bytes.</summary>
        private long _allocatedVRAMBytes = 0;

        #endregion

        #region Properties

        /// <inheritdoc />
        public override bool IsGenerated
        {
            get
            {
                lock (_imageStateLock)
                {
                    if (!RefreshPhysicalGroupImageIfStaleNoLock())
                        return false;

                    return _image.Handle != 0 || _view.Handle != 0 || _sampler.Handle != 0;
                }
            }
        }

        public override bool IsDescriptorReady
        {
            get
            {
                lock (_imageStateLock)
                {
                    if (!RefreshPhysicalGroupImageIfStaleNoLock())
                        return false;

                    return IsDescriptorReadyNoLock();
                }
            }
        }

        private bool IsDescriptorReadyNoLock()
        {
            bool descriptorHandlesReady =
                (_image.Handle != 0 || _view.Handle != 0 || _sampler.Handle != 0)
                && !IsDescriptorDirty
                && _view.Handle != 0
                && IsImageViewBackedByCurrentImage(_view)
                && (!CreateSampler || _sampler.Handle != 0);
            if (!descriptorHandlesReady)
                return false;

            if (_physicalGroup is not null || Data.FrameBufferAttachment.HasValue || Data.RequiresStorageUsage)
                return true;

            return !IsInvalidated && HasUploadedData;
        }

        public override bool TryEnsureDescriptorReadyForUse(string reason)
        {
            lock (_imageStateLock)
            {
                if (!TryEnsureDescriptorReadyForVulkanUseNoThrow(reason) ||
                    !RefreshPhysicalGroupImageIfStaleNoLock())
                {
                    return false;
                }

                return IsDescriptorReadyNoLock();
            }
        }

        public override bool TryEnsureDescriptorReadyForUse(string reason, bool allowSynchronousUpload)
        {
            lock (_imageStateLock)
            {
                if (allowSynchronousUpload)
                {
                    if (!TryEnsureDescriptorReadyForVulkanUseNoThrow(reason))
                        return false;
                }

                if (!RefreshPhysicalGroupImageIfStaleNoLock())
                    return false;

                return IsDescriptorReadyNoLock();
            }
        }

        private bool TryEnsureDescriptorReadyForVulkanUseNoThrow(string reason)
        {
            try
            {
                EnsureDescriptorReadyForVulkanUse(reason);
                return true;
            }
            catch (VulkanOutOfMemoryException ex)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.DescriptorAllocationFailed.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Texture descriptor allocation failed for '{0}' ({1}): {2}",
                    ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
                    reason,
                    ex.Message);
                return false;
            }
        }

        public bool IsLayoutReadyForSampling
            => CurrentImageLayout is ImageLayout.ShaderReadOnlyOptimal
                or ImageLayout.DepthStencilReadOnlyOptimal
                or ImageLayout.General;

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
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return ResolveTrackedImageLayoutNoLock();
                }
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
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return _image;
                }
            }
        }

        DeviceMemory IVkImageDescriptorSource.DescriptorMemory
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return _memory;
                }
            }
        }

        ImageView IVkImageDescriptorSource.DescriptorView
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return _view;
                }
            }
        }

        ImageViewType IVkImageDescriptorSource.DescriptorViewType => NormalizeImageViewTypeForLayerCount(DefaultViewType, ResolvedArrayLayers);

        Sampler IVkImageDescriptorSource.DescriptorSampler
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return _sampler;
                }
            }
        }

        Format IVkImageDescriptorSource.DescriptorFormat
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return ResolvedFormat;
                }
            }
        }

        ImageAspectFlags IVkImageDescriptorSource.DescriptorAspect
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return AspectFlags;
                }
            }
        }

        ImageUsageFlags IVkImageDescriptorSource.DescriptorUsage
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return Usage;
                }
            }
        }

        SampleCountFlags IVkImageDescriptorSource.DescriptorSamples
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return SampleCount;
                }
            }
        }

        uint IVkImageDescriptorSource.DescriptorMipLevels
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return ResolvedMipLevels;
                }
            }
        }

        uint IVkImageDescriptorSource.DescriptorArrayLayers
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return ResolvedArrayLayers;
                }
            }
        }

        bool IVkImageDescriptorSource.TryGetDescriptorSnapshot(
            ImageViewType? requestedViewType,
            ImageAspectFlags? requestedAspectMask,
            string reason,
            bool allowSynchronousUpload,
            out VkImageDescriptorSnapshot snapshot)
        {
            lock (_imageStateLock)
            {
                if (allowSynchronousUpload &&
                    !TryEnsureDescriptorReadyForVulkanUseNoThrow(reason))
                {
                    snapshot = default;
                    return false;
                }

                if (!RefreshPhysicalGroupImageIfStaleNoLock())
                {
                    snapshot = default;
                    return false;
                }

                return TryBuildDescriptorSnapshotNoLock(requestedViewType, requestedAspectMask, out snapshot);
            }
        }

        /// <inheritdoc />
        ImageView IVkImageDescriptorSource.GetDepthOnlyDescriptorView()
        {
            lock (_imageStateLock)
            {
                RefreshPhysicalGroupImageIfStale();
                return GetDepthOnlyDescriptorViewNoLock();
            }
        }

        ImageView IVkImageDescriptorSource.GetStencilOnlyDescriptorView()
        {
            lock (_imageStateLock)
            {
                RefreshPhysicalGroupImageIfStale();
                return GetStencilOnlyDescriptorViewNoLock();
            }
        }

        ImageView IVkImageDescriptorSource.GetDescriptorView(ImageViewType viewType)
        {
            lock (_imageStateLock)
            {
                RefreshPhysicalGroupImageIfStale();
                return GetDescriptorViewNoLock(viewType);
            }
        }

        private bool TryBuildDescriptorSnapshotNoLock(
            ImageViewType? requestedViewType,
            ImageAspectFlags? requestedAspectMask,
            out VkImageDescriptorSnapshot snapshot)
        {
            ImageView view = requestedAspectMask switch
            {
                ImageAspectFlags.DepthBit => GetDepthOnlyDescriptorViewNoLock(),
                ImageAspectFlags.StencilBit => GetStencilOnlyDescriptorViewNoLock(),
                _ => requestedViewType is { } viewType
                    ? GetDescriptorViewNoLock(viewType)
                    : _view
            };
            if (view.Handle != 0 && !IsImageViewBackedByCurrentImage(view))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.StaleDescriptorView.{Data.GetHashCode()}.{view.Handle}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Refreshing stale descriptor image view 0x{0:X} for texture '{1}' because it is not backed by the current image 0x{2:X}.",
                    view.Handle,
                    ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
                    _image.Handle);
                ForgetCurrentViews(removeActiveCacheEntry: true);
                CreateImageView(default);
                view = requestedAspectMask switch
                {
                    ImageAspectFlags.DepthBit => GetDepthOnlyDescriptorViewNoLock(),
                    ImageAspectFlags.StencilBit => GetStencilOnlyDescriptorViewNoLock(),
                    _ => requestedViewType is { } viewType
                        ? GetDescriptorViewNoLock(viewType)
                        : _view
                };
            }

            ImageLayout trackedLayout = ResolveTrackedImageLayoutNoLock();
            bool ready = IsDescriptorReadyNoLock() && view.Handle != 0 && IsImageViewBackedByCurrentImage(view);
            snapshot = new(
                _image,
                _memory,
                view,
                requestedViewType ?? NormalizeImageViewTypeForLayerCount(DefaultViewType, ResolvedArrayLayers),
                _sampler,
                ResolvedFormat,
                AspectFlags,
                Usage,
                SampleCount,
                ResolvedMipLevels,
                ResolvedArrayLayers,
                DescriptorGeneration,
                trackedLayout,
                _physicalGroup is not null,
                ready);
            return ready;
        }

        private ImageLayout ResolveTrackedImageLayoutNoLock()
        {
            if (_physicalGroup is not null)
            {
                _currentImageLayout = ResolvePhysicalGroupWholeImageLayout();
                return _currentImageLayout;
            }

            if (_hasPartialAttachmentLayouts)
                return TryResolveWholeImageAttachmentLayout(out ImageLayout layout)
                    ? layout
                    : ImageLayout.Undefined;

            return _currentImageLayout;
        }

        private ImageView GetDepthOnlyDescriptorViewNoLock()
        {
            var key = new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, DefaultViewType, ImageAspectFlags.DepthBit);
            if (!_attachmentViews.TryGetValue(key, out ImageView cached))
            {
                cached = CreateView(key);
                _attachmentViews[key] = cached;
            }

            return cached;
        }

        private ImageView GetStencilOnlyDescriptorViewNoLock()
        {
            if (!HasStencilAspect(ResolvedFormat))
                return default;

            var key = new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, DefaultViewType, ImageAspectFlags.StencilBit);
            if (!_attachmentViews.TryGetValue(key, out ImageView cached))
            {
                cached = CreateView(key);
                _attachmentViews[key] = cached;
            }

            return cached;
        }

        private ImageView GetDescriptorViewNoLock(ImageViewType viewType)
        {
            if (viewType == DefaultViewType)
                return _view;

            if (!TryBuildDescriptorViewKey(viewType, out AttachmentViewKey key))
                return default;

            if (!_attachmentViews.TryGetValue(key, out ImageView cached))
            {
                cached = CreateView(key);
                _attachmentViews[key] = cached;
            }

            return cached;
        }

        private bool TryBuildDescriptorViewKey(ImageViewType viewType, out AttachmentViewKey key)
        {
            key = default;

            if (viewType == ImageViewType.Type2DArray)
            {
                if (TextureImageType != ImageType.Type2D || ResolvedArrayLayers < 1)
                    return false;

                key = new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, viewType, AspectFlags);
                return true;
            }

            if (viewType == ImageViewType.Type2D)
            {
                if (TextureImageType != ImageType.Type2D || ResolvedArrayLayers < 1)
                    return false;

                key = new AttachmentViewKey(0, ResolvedMipLevels, 0, 1, viewType, AspectFlags);
                return true;
            }

            if (viewType == ImageViewType.Type1DArray)
            {
                if (TextureImageType != ImageType.Type1D || ResolvedArrayLayers < 1)
                    return false;

                key = new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, viewType, AspectFlags);
                return true;
            }

            if (viewType == ImageViewType.Type1D)
            {
                if (TextureImageType != ImageType.Type1D || ResolvedArrayLayers < 1)
                    return false;

                key = new AttachmentViewKey(0, ResolvedMipLevels, 0, 1, viewType, AspectFlags);
                return true;
            }

            if (viewType == ImageViewType.TypeCube)
            {
                if (TextureImageType != ImageType.Type2D || ResolvedArrayLayers < 6)
                    return false;

                key = new AttachmentViewKey(0, ResolvedMipLevels, 0, 6, viewType, AspectFlags);
                return true;
            }

            if (viewType == ImageViewType.TypeCubeArray)
            {
                if (TextureImageType != ImageType.Type2D || ResolvedArrayLayers < 6)
                    return false;

                uint cubeCompatibleLayerCount = ResolvedArrayLayers - (ResolvedArrayLayers % 6u);
                if (cubeCompatibleLayerCount < 6)
                    return false;

                key = new AttachmentViewKey(0, ResolvedMipLevels, 0, cubeCompatibleLayerCount, viewType, AspectFlags);
                return true;
            }

            if (viewType == ImageViewType.Type3D)
            {
                if (TextureImageType != ImageType.Type3D)
                    return false;

                key = new AttachmentViewKey(0, ResolvedMipLevels, 0, 1, viewType, AspectFlags);
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        ImageLayout IVkImageDescriptorSource.TrackedImageLayout
        {
            get
            {
                lock (_imageStateLock)
                {
                    RefreshPhysicalGroupImageIfStale();
                    return ResolveTrackedImageLayoutNoLock();
                }
            }
        }

        /// <inheritdoc />
        bool IVkImageDescriptorSource.UsesAllocatorImage => _physicalGroup is not null;

        /// <inheritdoc />
        bool IVkImageDescriptorSource.TryTransitionDedicatedImageLayout(ImageLayout oldLayout, ImageLayout newLayout)
        {
            RefreshPhysicalGroupImageIfStale();
            if (_physicalGroup is not null || _image.Handle == 0)
                return false;

            if (_hasPartialAttachmentLayouts)
                return TryTransitionPartialAttachmentLayoutsTo(newLayout);

            ImageLayout currentLayout = _currentImageLayout;
            if (currentLayout != oldLayout)
                oldLayout = currentLayout;

            if (oldLayout == newLayout)
                return true;

            TransitionImageLayout(oldLayout, newLayout);
            return true;
        }

        private bool TryTransitionPartialAttachmentLayoutsTo(ImageLayout newLayout)
        {
            if (Renderer.IsDeviceLost || _image.Handle == 0)
                return false;

            newLayout = CoerceLayoutForUsage(newLayout);
            uint mipCount = Math.Max(ResolvedMipLevels, 1u);
            uint layerCount = Math.Max(ResolvedArrayLayers, 1u);
            int maxBarrierCount = checked((int)(mipCount * layerCount));
            Span<ImageMemoryBarrier> barriers = maxBarrierCount <= 64
                ? stackalloc ImageMemoryBarrier[maxBarrierCount]
                : new ImageMemoryBarrier[maxBarrierCount];
            int barrierCount = 0;
            PipelineStageFlags sourceStages = 0;
            PipelineStageFlags destinationStages = 0;

            for (uint mip = 0; mip < mipCount; mip++)
            {
                for (uint layer = 0; layer < layerCount; layer++)
                {
                    AttachmentLayoutKey key = new(mip, layer, 1u);
                    ImageLayout oldLayout = _attachmentLayouts.TryGetValue(key, out ImageLayout trackedLayout)
                        ? trackedLayout
                        : ImageLayout.Undefined;
                    oldLayout = CoerceLayoutForUsage(oldLayout);
                    if (oldLayout == newLayout)
                        continue;

                    AssembleTransitionImageLayout(oldLayout, newLayout, out ImageMemoryBarrier barrier, out PipelineStageFlags src, out PipelineStageFlags dst);
                    barrier.SubresourceRange.BaseMipLevel = mip;
                    barrier.SubresourceRange.LevelCount = 1;
                    barrier.SubresourceRange.BaseArrayLayer = layer;
                    barrier.SubresourceRange.LayerCount = 1;
                    barriers[barrierCount++] = barrier;
                    sourceStages |= src;
                    destinationStages |= dst;
                }
            }

            if (barrierCount > 0)
            {
                using var scope = Renderer.NewCommandScope();
                fixed (ImageMemoryBarrier* barriersPtr = barriers)
                {
                    Renderer.CmdPipelineBarrierTracked(
                        scope.CommandBuffer,
                        sourceStages,
                        destinationStages,
                        0,
                        0,
                        null,
                        0,
                        null,
                        (uint)barrierCount,
                        barriersPtr);
                }
            }

            _currentImageLayout = newLayout;
            ResetAttachmentLayoutTracking();
            return true;
        }

        /// <inheritdoc />
        void IVkFrameBufferAttachmentSource.UpdateTrackedLayout(ImageLayout layout)
        {
            if (_physicalGroup is not null)
                _physicalGroup.LastKnownLayout = layout;
            _currentImageLayout = layout;
            HasUploadedData = true;
            MarkDescriptorClean();
            ResetAttachmentLayoutTracking();
        }

        /// <inheritdoc />
        ImageLayout IVkFrameBufferAttachmentSource.GetAttachmentTrackedLayout(int mipLevel, int layerIndex)
        {
            RefreshPhysicalGroupImageIfStale();

            if (_physicalGroup is not null)
            {
                uint baseMip = ClampAttachmentMipLevel(mipLevel);
                uint baseLayer = layerIndex < 0 ? 0u : ClampAttachmentLayerIndex(layerIndex);
                uint layerCount = layerIndex < 0 ? Math.Max(ResolvedArrayLayers, 1u) : 1u;
                ImageLayout groupLayout = _physicalGroup.GetKnownLayout(baseMip, 1u, baseLayer, layerCount);
                return groupLayout != ImageLayout.Undefined
                    ? groupLayout
                    : _physicalGroup.LastKnownLayout;
            }

            if (!_hasPartialAttachmentLayouts)
                return _currentImageLayout;

            AttachmentLayoutKey key = BuildAttachmentLayoutKey(mipLevel, layerIndex);
            if (_attachmentLayouts.TryGetValue(key, out ImageLayout layout))
                return layout;

            if (layerIndex < 0 && TryResolveAllLayerAttachmentLayout((uint)Math.Max(mipLevel, 0), out layout))
                return layout;

            if (_hasPartialAttachmentLayouts)
                return ImageLayout.Undefined;

            return _currentImageLayout;
        }

        private ImageLayout ResolvePhysicalGroupWholeImageLayout()
        {
            if (_physicalGroup is null)
                return _currentImageLayout;

            uint mipLevels = Math.Max(ResolvedMipLevels, 1u);
            uint arrayLayers = Math.Max(ResolvedArrayLayers, 1u);
            ImageLayout knownLayout = _physicalGroup.GetKnownLayout(0u, mipLevels, 0u, arrayLayers);
            return knownLayout != ImageLayout.Undefined
                ? knownLayout
                : _physicalGroup.LastKnownLayout;
        }

        /// <inheritdoc />
        void IVkFrameBufferAttachmentSource.UpdateAttachmentTrackedLayout(ImageLayout layout, int mipLevel, int layerIndex)
        {
            if (AttachmentCoversWholeImage(mipLevel, layerIndex))
            {
                ((IVkFrameBufferAttachmentSource)this).UpdateTrackedLayout(layout);
                return;
            }

            BeginPartialAttachmentLayoutTracking();

            uint baseMip = ClampAttachmentMipLevel(mipLevel);
            uint baseLayer = layerIndex < 0 ? 0u : ClampAttachmentLayerIndex(layerIndex);
            uint layerCount = layerIndex < 0 ? Math.Max(ResolvedArrayLayers, 1u) : 1u;

            _attachmentLayouts[BuildAttachmentLayoutKey(mipLevel, layerIndex)] = layout;
            _physicalGroup?.UpdateKnownLayout(layout, baseMip, 1u, baseLayer, layerCount);
            UpdateWholeImageLayoutFromAttachmentTracking();
            HasUploadedData = true;
            MarkDescriptorClean();
        }

        #endregion

        #region Resolved Properties

        /// <summary>Effective format, respecting any override from the physical group.</summary>
        protected internal Format ResolvedFormat => _formatOverride ?? Format;

        /// <summary>Effective extent, respecting any override from the physical group.</summary>
        protected Extent3D ResolvedExtent => _extentOverride ?? _layout.Extent;

        /// <summary>Effective array layer count.</summary>
        protected uint ResolvedArrayLayers => _arrayLayersOverride ?? _layout.ArrayLayers;

        /// <summary>Effective mip level count.</summary>
        protected uint ResolvedMipLevels => SampleCount == SampleCountFlags.Count1Bit
            ? _mipLevelsOverride ?? _layout.MipLevels
            : 1u;

        /// <summary>Effective sample count, respecting any override from the physical group.</summary>
        internal SampleCountFlags SampleCount => _samplesOverride ?? ReadSampleCountFromData();

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
        protected override void LinkTextureData()
        {
            SubscribeResizeEvents();
            SubscribeChildTextureEvents();
        }

        /// <inheritdoc />
        /// <remarks>
        /// Unsubscribes all engine-texture events wired up in <see cref="LinkData"/>.
        /// </remarks>
        protected override void UnlinkTextureData()
        {
            UnsubscribeChildTextureEvents();
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
            if (_image.Handle == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.NoImageOnGenerate.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Texture '{0}' could not acquire a Vulkan image during generation.",
                    ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName());
                return InvalidBindingId;
            }

            CreateImageView(default);
            if (_view.Handle == 0)
                return InvalidBindingId;

            if (CreateSampler)
                CreateSamplerInternal();
            if (_physicalGroup is not null || Data.FrameBufferAttachment.HasValue || Data.RequiresStorageUsage)
            {
                HasUploadedData = true;
                IsInvalidated = false;
            }
            MarkDescriptorClean();
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

            RemovePhysicalImageViewCacheEntry(_physicalGroup, _image.Handle);
            DestroyPhysicalImageViewCache();

            // Report the VRAM deallocation to the stats tracker immediately
            // (the logical allocation is gone even if the GPU handle lingers).
            if (_ownsImageMemory && _allocatedVRAMBytes > 0)
            {
                RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(_allocatedVRAMBytes);
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
            _samplesOverride = null;
            _currentImageLayout = ImageLayout.Undefined;
            ResetAttachmentLayoutTracking();
            InvalidateTextureData();
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

            string? logicalResourceName = ResolveLogicalResourceName();
            if (!TryResolvePhysicalGroup(ensureAllocated: true, out VulkanPhysicalImageGroup? group, out string? physicalGroupFailureReason))
            {
                LogPhysicalGroupRefreshFailure(physicalGroupFailureReason);
                return;
            }

            if (group is null &&
                !Renderer.TryEnsurePhysicalImageForTextureResource(logicalResourceName, out group, out string? lazyPhysicalGroupFailureReason) &&
                !string.IsNullOrWhiteSpace(lazyPhysicalGroupFailureReason))
            {
                LogPhysicalGroupRefreshFailure(lazyPhysicalGroupFailureReason);
                return;
            }

            if (group is not null)
            {
                RetireDedicatedImageBeforeBorrowingPhysicalGroup();

                // Borrow the image from the resource-planner physical group.
                _physicalGroup = group;
                _image = group.Image;
                _memory = group.Memory;
                _extentOverride = group.ResolvedExtent;
                _formatOverride = group.Format;
                Usage = group.Usage;
                // Preserve storage usage if the abstract texture requires it —
                // the resource planner may not know about out-of-graph compute dispatches.
                if (Data.RequiresStorageUsage)
                    Usage |= ImageUsageFlags.StorageBit;
                _arrayLayersOverride = Math.Max(group.Template.Layers, 1u);
                _mipLevelsOverride = Math.Max(1u, group.MipLevels);
                _samplesOverride = group.Samples;
                _ownsImageMemory = false;
                AspectFlags = NormalizeAspectMaskForFormat(ResolvedFormat, AspectFlags);
                _currentImageLayout = group.LastKnownLayout;
                ResetAttachmentLayoutTracking();
                return;
            }

            // No physical group available — create a dedicated image.
            // Adjust usage before creating: add storage bit if the engine texture requests it,
            // and swap color-attachment for depth-stencil-attachment when the format is depth/stencil.
            if (Data.RequiresStorageUsage)
                Usage |= ImageUsageFlags.StorageBit;
            bool isAttachmentTexture = Data.FrameBufferAttachment.HasValue;
            if (VkFormatConversions.IsDepthStencilFormat(ResolvedFormat))
            {
                Usage &= ~ImageUsageFlags.ColorAttachmentBit;
                Usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            }
            else if (isAttachmentTexture)
            {
                Usage &= ~ImageUsageFlags.DepthStencilAttachmentBit;
                Usage |= ImageUsageFlags.ColorAttachmentBit;
            }
            CreateDedicatedImage();
            _physicalGroup = null;
            _extentOverride = null;
            _formatOverride = null;
            _arrayLayersOverride = null;
            _mipLevelsOverride = null;
            _samplesOverride = null;
            _ownsImageMemory = true;
            AspectFlags = NormalizeAspectMaskForFormat(ResolvedFormat, AspectFlags);
            _currentImageLayout = ImageLayout.Undefined;
            ResetAttachmentLayoutTracking();
        }

        private void RetireDedicatedImageBeforeBorrowingPhysicalGroup()
        {
            if (!_ownsImageMemory)
            {
                DestroyAllViews();
                return;
            }

            ImageView[] retiredAttachmentViews;
            if (_attachmentViews.Count > 0)
            {
                retiredAttachmentViews = new ImageView[_attachmentViews.Count];
                int index = 0;
                foreach ((_, ImageView attachmentView) in _attachmentViews)
                    retiredAttachmentViews[index++] = attachmentView;
            }
            else
            {
                retiredAttachmentViews = [];
            }

            Renderer.RetireImageResources(new RetiredImageResources(
                _image,
                _memory,
                _view,
                retiredAttachmentViews,
                default,
                _allocatedVRAMBytes));

            if (_allocatedVRAMBytes > 0)
            {
                RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(_allocatedVRAMBytes);
                _allocatedVRAMBytes = 0;
            }

            _view = default;
            _attachmentViews.Clear();
            _image = default;
            _memory = default;
            _ownsImageMemory = false;
        }

        /// <summary>
        /// When backed by a resource-planner physical group, checks whether the group's
        /// VkImage handle has changed (e.g. because the planner rebuilt between frames)
        /// and updates the cached <see cref="_image"/> / <see cref="_memory"/> fields.
        /// Also recreates the primary ImageView for the new image.
        /// This prevents stale-handle segfaults in CmdBlitImage and other Vulkan commands.
        /// </summary>
        private bool RefreshPhysicalGroupImageIfStale()
        {
            lock (_imageStateLock)
                return RefreshPhysicalGroupImageIfStaleNoLock();
        }

        private bool RefreshPhysicalGroupImageIfStaleNoLock()
        {
            if (_physicalGroup is null)
                return true;

            bool physicalGroupChanged = false;
            bool switchedPhysicalGroup = false;
            if (!TryResolvePhysicalGroup(ensureAllocated: true, out VulkanPhysicalImageGroup? activeGroup, out string? activeFailureReason))
            {
                LogPhysicalGroupRefreshFailure(activeFailureReason);
                return false;
            }

            if (activeGroup is not null && !ReferenceEquals(activeGroup, _physicalGroup))
            {
                SaveCurrentPhysicalImageViewCache();
                _physicalGroup = activeGroup;
                physicalGroupChanged = true;
                switchedPhysicalGroup = true;
            }

            if (!_physicalGroup.IsAllocated)
            {
                // The physical group was destroyed — the resource planner may have rebuilt
                // between frames and replaced it with a brand-new group object.
                // Try to re-resolve from the allocator.
                if (!TryResolvePhysicalGroup(ensureAllocated: true, out VulkanPhysicalImageGroup? replacement, out string? replacementFailureReason))
                {
                    LogPhysicalGroupRefreshFailure(replacementFailureReason);
                    return false;
                }

                if (replacement is not null && replacement.IsAllocated)
                {
                    physicalGroupChanged |= !ReferenceEquals(replacement, _physicalGroup);
                    _physicalGroup = replacement;
                    // Fall through to the handle-update check below.
                }
                else
                {
                    // No replacement group available. Clear the stale handle so callers
                    // don't use a destroyed VkImage.
                    if (switchedPhysicalGroup)
                    {
                        _view = default;
                        _attachmentViews.Clear();
                        _image = default;
                        _memory = default;
                    }
                    else if (_image.Handle != 0)
                    {
                        DestroyCurrentViews(removeActiveCacheEntry: true);
                        _image = default;
                        _memory = default;
                    }
                    else
                    {
                        _memory = default;
                    }
                    return false;
                }
            }

            Image current = _physicalGroup.Image;
            if (current.Handle == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.StaleImageHandle.NullPhysical.{ResolveLogicalResourceName() ?? "?"}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Physical group for '{0}' is allocated but has no image handle yet.",
                    ResolveLogicalResourceName() ?? Data.Name ?? "<unnamed>");
                if (switchedPhysicalGroup)
                {
                    _view = default;
                    _attachmentViews.Clear();
                }
                else
                {
                    DestroyCurrentViews(removeActiveCacheEntry: true);
                }
                _image = default;
                _memory = default;
                return false;
            }

            if (current.Handle == _image.Handle)
            {
                _extentOverride = _physicalGroup.ResolvedExtent;
                _formatOverride = _physicalGroup.Format;
                _arrayLayersOverride = Math.Max(_physicalGroup.Template.Layers, 1u);
                _mipLevelsOverride = Math.Max(1u, _physicalGroup.MipLevels);
                _samplesOverride = _physicalGroup.Samples;
                Usage = _physicalGroup.Usage;
                if (Data.RequiresStorageUsage)
                    Usage |= ImageUsageFlags.StorageBit;
                if (physicalGroupChanged)
                {
                    ResetAttachmentLayoutTracking();
                    _currentImageLayout = _physicalGroup.LastKnownLayout;
                    HasUploadedData = true;
                    IsInvalidated = false;
                    MarkDescriptorPublished();
                }
                return true;
            }

            if (switchedPhysicalGroup)
            {
                _image = current;
                _memory = _physicalGroup.Memory;
                _extentOverride = _physicalGroup.ResolvedExtent;
                _formatOverride = _physicalGroup.Format;
                _arrayLayersOverride = Math.Max(_physicalGroup.Template.Layers, 1u);
                _mipLevelsOverride = Math.Max(1u, _physicalGroup.MipLevels);
                _samplesOverride = _physicalGroup.Samples;
                Usage = _physicalGroup.Usage;
                if (Data.RequiresStorageUsage)
                    Usage |= ImageUsageFlags.StorageBit;

                if (!TryRestorePhysicalImageViewCache(_physicalGroup, current))
                {
                    _view = default;
                    _attachmentViews.Clear();
                    CreateImageView(default);
                }

                ResetAttachmentLayoutTracking();
                _currentImageLayout = _physicalGroup.LastKnownLayout;
                HasUploadedData = true;
                IsInvalidated = false;
                MarkDescriptorPublished();
                return true;
            }

            Debug.VulkanWarningEvery(
                $"Vulkan.StaleImageHandle.{ResolveLogicalResourceName() ?? "?"}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Physical group image handle changed for '{0}': 0x{1:X} → 0x{2:X}. Refreshing cached handle + view.",
                ResolveLogicalResourceName() ?? Data.Name ?? "<unnamed>",
                _image.Handle,
                current.Handle);

            // Same physical-group handle changes mean the underlying image was reallocated.
            // A fresh VkImage starts in UNDEFINED even if the group object still has stale
            // layout state from the previous handle.
            Renderer.ClearTrackedImageLayouts(_image);
            Renderer.ClearTrackedImageLayouts(current);
            _physicalGroup.LastKnownLayout = ImageLayout.Undefined;

            // Retire the old views before changing _image so cache removal targets the old handle.
            DestroyCurrentViews(removeActiveCacheEntry: true);
            _image = current;
            _memory = _physicalGroup.Memory;
            _extentOverride = _physicalGroup.ResolvedExtent;
            _formatOverride = _physicalGroup.Format;
            _arrayLayersOverride = Math.Max(_physicalGroup.Template.Layers, 1u);
            _mipLevelsOverride = Math.Max(1u, _physicalGroup.MipLevels);
            _samplesOverride = _physicalGroup.Samples;
            Usage = _physicalGroup.Usage;
            if (Data.RequiresStorageUsage)
                Usage |= ImageUsageFlags.StorageBit;

            // Recreate the views against the new image. Old views may still be
            // referenced by retired framebuffers or in-flight command buffers.
            ResetAttachmentLayoutTracking();
            CreateImageView(default);
            _currentImageLayout = ImageLayout.Undefined;
            HasUploadedData = true;
            IsInvalidated = false;
            MarkDescriptorPublished();

            // The barrier planner will transition the new image inside the command
            // buffer at first use.
            return true;
        }

        private void LogPhysicalGroupRefreshFailure(string? failureReason)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Texture.PhysicalGroupRefreshFailed.{ResolveLogicalResourceName() ?? Data.GetHashCode().ToString()}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Physical image refresh failed for texture '{0}': {1}",
                ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
                string.IsNullOrWhiteSpace(failureReason) ? "resource group unavailable" : failureReason);
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
                Samples = SampleCount,
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

            Renderer.ClearTrackedImageLayouts(_image);
            _currentImageLayout = ImageLayout.Undefined;
            ResetAttachmentLayoutTracking();

            Api!.GetImageMemoryRequirements(Device, _image, out MemoryRequirements memRequirements);

            VulkanMemoryAllocation allocation = Renderer.AllocateImageMemoryWithFallback(_image, MemoryProperties);
            Renderer._imageAllocations[_image.Handle] = allocation;
            _memory = allocation.Memory;

            if (Api!.BindImageMemory(Device, _image, allocation.Memory, allocation.Offset) != Result.Success)
            {
                Renderer._imageAllocations.TryRemove(_image.Handle, out _);
                Renderer.FreeMemoryAllocation(allocation);
                throw new Exception("Failed to bind memory for texture image.");
            }

            Debug.VulkanEvery(
                $"Vulkan.DedicatedTexture.{ResolveLogicalResourceName() ?? Data.Name ?? "unnamed"}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Dedicated texture image created: name='{0}' handle=0x{1:X} format={2} extent={3}x{4}x{5} usage={6} mips={7} samples={8}",
                ResolveLogicalResourceName() ?? Data.Name ?? "<unnamed>",
                _image.Handle,
                ResolvedFormat,
                ResolvedExtent.Width,
                ResolvedExtent.Height,
                ResolvedExtent.Depth,
                Usage,
                ResolvedMipLevels,
                SampleCount);

            // Record the allocation for VRAM usage statistics.
            _allocatedVRAMBytes = (long)memRequirements.Size;
            RuntimeEngine.Rendering.Stats.Vram.AddTextureAllocation(_allocatedVRAMBytes);
        }

        internal bool TryCreateSynchronizedImportedUpload(
            in VulkanImportedTextureUploadRequest request,
            TextureStreamingResidentData residentData,
            bool includeMipChain,
            ulong publicationToken,
            Func<bool>? shouldAcceptResult,
            Action<XRTexture2D>? onFinished,
            Action? onCanceled,
            Action<Exception>? onError,
            out VulkanImportedTexturePendingUpload? pendingUpload,
            out string? failureReason)
        {
            pendingUpload = null;
            failureReason = null;

            if (!TryCreateSynchronizedImportedUploadPreparation(
                    request,
                    residentData,
                    includeMipChain,
                    publicationToken,
                    shouldAcceptResult,
                    onFinished,
                    onCanceled,
                    onError,
                    out VulkanImportedTextureUploadPreparation? preparation,
                    out failureReason)
                || preparation is null)
            {
                return false;
            }

            bool completed = false;
            try
            {
                while (!completed)
                {
                    if (!TryAdvanceSynchronizedImportedUploadPreparation(
                            preparation,
                            out completed,
                            out pendingUpload,
                            out failureReason))
                    {
                        return false;
                    }
                }

                return pendingUpload is not null;
            }
            finally
            {
                if (!completed || pendingUpload is null)
                    ReleaseSynchronizedImportedUploadPreparation(preparation);
            }
        }

        internal bool TryCreateSynchronizedImportedUploadPreparation(
            in VulkanImportedTextureUploadRequest request,
            TextureStreamingResidentData residentData,
            bool includeMipChain,
            ulong publicationToken,
            Func<bool>? shouldAcceptResult,
            Action<XRTexture2D>? onFinished,
            Action? onCanceled,
            Action<Exception>? onError,
            out VulkanImportedTextureUploadPreparation? preparation,
            out string? failureReason)
        {
            preparation = null;
            failureReason = null;

            if (this is not VkTexture2D texture2D)
            {
                failureReason = "synchronized imported texture uploads are only implemented for XRTexture2D";
                return false;
            }

            if (Data is not XRTexture2D texture)
            {
                failureReason = "texture data is not XRTexture2D";
                return false;
            }

            if (Renderer.IsDeviceLost)
            {
                failureReason = "Vulkan device is lost";
                return false;
            }

            if (request.CancellationToken.IsCancellationRequested
                || (shouldAcceptResult is not null && !shouldAcceptResult()))
            {
                failureReason = "request was canceled before upload resources were prepared";
                return false;
            }

            XRTexture2D.ApplyResidentDataForVulkanPublication(texture, residentData, includeMipChain);
            RefreshLayout();

            Format format = Format;
            ImageAspectFlags aspectMask = NormalizeAspectMaskForFormat(format, AspectFlags);
            AspectFlags = aspectMask;
            Extent3D extent = _layout.Extent;
            uint mipLevels = Math.Max(_layout.MipLevels, 1u);
            uint arrayLayers = Math.Max(_layout.ArrayLayers, 1u);
            string debugName = BuildImportedUploadDebugName(request, publicationToken);

            preparation = new VulkanImportedTextureUploadPreparation(
                request,
                texture2D,
                residentData,
                includeMipChain,
                publicationToken,
                shouldAcceptResult,
                onFinished,
                onCanceled,
                onError,
                format,
                aspectMask,
                extent,
                mipLevels,
                arrayLayers,
                debugName);
            return true;
        }

        internal bool TryAdvanceSynchronizedImportedUploadPreparation(
            VulkanImportedTextureUploadPreparation preparation,
            out bool completed,
            out VulkanImportedTexturePendingUpload? pendingUpload,
            out string? failureReason)
        {
            completed = false;
            pendingUpload = null;
            failureReason = null;

            if (Renderer.IsDeviceLost)
            {
                failureReason = "Vulkan device is lost";
                return false;
            }

            if (!preparation.ShouldAccept())
            {
                failureReason = "request was canceled before upload resources were prepared";
                return false;
            }

            try
            {
                switch (preparation.Step)
                {
                    case VulkanImportedTextureUploadPreparationStep.CreateImage:
                        if (!TryCreateImportedUploadImage(
                                preparation.Extent,
                                preparation.MipLevels,
                                preparation.ArrayLayers,
                                preparation.Format,
                                out preparation.Image,
                                out preparation.Memory,
                                out preparation.CommittedBytes,
                                out failureReason))
                        {
                            return false;
                        }

                        Renderer.SetDebugObjectName(ObjectType.Image, preparation.Image.Handle, $"{preparation.DebugName}.Image");
                        Renderer.SetDebugObjectName(ObjectType.DeviceMemory, preparation.Memory.Handle, $"{preparation.DebugName}.Memory");
                        preparation.Step = VulkanImportedTextureUploadPreparationStep.CreateImageView;
                        return true;

                    case VulkanImportedTextureUploadPreparationStep.CreateImageView:
                        preparation.ImageView = CreateImportedUploadImageView(
                            preparation.Image,
                            preparation.Format,
                            preparation.AspectMask,
                            preparation.MipLevels,
                            preparation.ArrayLayers);
                        Renderer.SetDebugObjectName(ObjectType.ImageView, preparation.ImageView.Handle, $"{preparation.DebugName}.View");
                        preparation.Step = CreateSampler
                            ? VulkanImportedTextureUploadPreparationStep.CreateSampler
                            : VulkanImportedTextureUploadPreparationStep.CreateNextStagingMip;
                        return true;

                    case VulkanImportedTextureUploadPreparationStep.CreateSampler:
                        preparation.Sampler = CreateImportedUploadSampler();
                        Renderer.SetDebugObjectName(ObjectType.Sampler, preparation.Sampler.Handle, $"{preparation.DebugName}.Sampler");
                        preparation.Step = VulkanImportedTextureUploadPreparationStep.CreateNextStagingMip;
                        return true;

                    case VulkanImportedTextureUploadPreparationStep.CreateNextStagingMip:
                        if (TryPrepareNextImportedUploadStagingMip(preparation, out failureReason))
                            return true;

                        if (!string.IsNullOrEmpty(failureReason))
                            return false;

                        if (preparation.StagingResources.Count == 0)
                        {
                            failureReason = "resident data did not produce any staging uploads";
                            return false;
                        }

                        preparation.Step = VulkanImportedTextureUploadPreparationStep.Complete;
                        return true;

                    case VulkanImportedTextureUploadPreparationStep.Complete:
                        if (!VulkanImportedTextureUploadValidation.TryValidateCopyRegions(
                                preparation.Request.TextureName,
                                preparation.PublicationToken,
                                preparation.Extent,
                                preparation.MipLevels,
                                preparation.ArrayLayers,
                                preparation.StagingResources,
                                out failureReason))
                        {
                            return false;
                        }

                        pendingUpload = new VulkanImportedTexturePendingUpload(
                            preparation.Request,
                            preparation.Texture,
                            preparation.Image,
                            preparation.Memory,
                            preparation.ImageView,
                            preparation.Sampler,
                            preparation.Format,
                            preparation.AspectMask,
                            preparation.Extent,
                            preparation.MipLevels,
                            preparation.ArrayLayers,
                            preparation.CommittedBytes,
                            preparation.PublicationToken,
                            [.. preparation.StagingResources],
                            preparation.ShouldAcceptResult,
                            preparation.OnFinished,
                            preparation.OnCanceled,
                            preparation.OnError);

                        preparation.Image = default;
                        preparation.Memory = default;
                        preparation.ImageView = default;
                        preparation.Sampler = default;
                        preparation.StagingResources.Clear();
                        completed = true;
                        return true;
                }
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }

            failureReason = $"unknown upload preparation step {preparation.Step}";
            return false;
        }

        private bool TryPrepareNextImportedUploadStagingMip(
            VulkanImportedTextureUploadPreparation preparation,
            out string? failureReason)
        {
            failureReason = null;
            uint levelCount = Math.Min((uint)preparation.ResidentData.Mipmaps.Length, preparation.MipLevels);

            while (preparation.NextMipLevel < levelCount)
            {
                uint level = (uint)preparation.NextMipLevel++;
                Mipmap2D? mip = preparation.ResidentData.Mipmaps[level];
                if (mip is null)
                    continue;

                DataSource? uploadData = VkFormatConversions.CreateNormalizedUploadData2D(mip, preparation.Format, out bool ownsUploadData);
                try
                {
                    if (!TryCreateStagingBuffer(uploadData, out Buffer stagingBuffer, out DeviceMemory stagingMemory))
                    {
                        failureReason = $"could not create staging buffer for mip {level}";
                        return false;
                    }

                    Renderer.SetDebugObjectName(ObjectType.Buffer, stagingBuffer.Handle, $"{preparation.DebugName}.StagingMip{level}");

                    Extent3D mipExtent = new(Math.Max(mip.Width, 1u), Math.Max(mip.Height, 1u), 1u);
                    BufferImageCopy region = new()
                    {
                        BufferOffset = 0,
                        BufferRowLength = 0,
                        BufferImageHeight = 0,
                        ImageSubresource = new ImageSubresourceLayers
                        {
                            AspectMask = preparation.AspectMask,
                            MipLevel = level,
                            BaseArrayLayer = 0,
                            LayerCount = 1,
                        },
                        ImageOffset = new Offset3D(0, 0, 0),
                        ImageExtent = mipExtent,
                    };

                    preparation.StagingResources.Add(new VulkanImportedTextureUploadStagingResource(
                        stagingBuffer,
                        stagingMemory,
                        region,
                        (ulong)(uploadData?.Length ?? 0u)));
                    return true;
                }
                finally
                {
                    if (ownsUploadData)
                        uploadData?.Dispose();
                }
            }

            return false;
        }

        internal void ReleaseSynchronizedImportedUploadPreparation(VulkanImportedTextureUploadPreparation preparation)
        {
            if (preparation.Image.Handle == 0
                && preparation.Memory.Handle == 0
                && preparation.ImageView.Handle == 0
                && preparation.Sampler.Handle == 0
                && preparation.StagingResources.Count == 0)
            {
                return;
            }

            ReleasePreparedImportedUploadResources(
                preparation.Image,
                preparation.Memory,
                preparation.ImageView,
                preparation.Sampler,
                preparation.CommittedBytes,
                [.. preparation.StagingResources]);
            preparation.Image = default;
            preparation.Memory = default;
            preparation.ImageView = default;
            preparation.Sampler = default;
            preparation.StagingResources.Clear();
        }

        private static string BuildImportedUploadDebugName(in VulkanImportedTextureUploadRequest request, ulong publicationToken)
        {
            string textureName = string.IsNullOrWhiteSpace(request.TextureName)
                ? "ImportedTexture"
                : request.TextureName!;
            return $"ImportedTextureUpload.{textureName}.gen{request.StreamingGeneration}.token{publicationToken}";
        }

        private bool TryCreateImportedUploadImage(
            Extent3D extent,
            uint mipLevels,
            uint arrayLayers,
            Format format,
            out Image image,
            out DeviceMemory memory,
            out long committedBytes,
            out string? failureReason)
        {
            image = default;
            memory = default;
            committedBytes = 0L;
            failureReason = null;

            ImageUsageFlags usage = DefaultUsage;
            if (Data.RequiresStorageUsage)
                usage |= ImageUsageFlags.StorageBit;
            if (VkFormatConversions.IsDepthStencilFormat(format))
            {
                usage &= ~ImageUsageFlags.ColorAttachmentBit;
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            }

            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                Flags = AdditionalImageFlags,
                ImageType = TextureImageType,
                Extent = extent,
                MipLevels = mipLevels,
                ArrayLayers = arrayLayers,
                Format = format,
                Tiling = Tiling,
                InitialLayout = ImageLayout.Undefined,
                Usage = usage,
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = SharingMode.Exclusive,
            };

            Result createResult = Api!.CreateImage(Device, ref imageInfo, null, out image);
            if (createResult != Result.Success || image.Handle == 0)
            {
                image = default;
                failureReason = $"failed to create synchronized imported texture image ({createResult})";
                return false;
            }

            Renderer.ClearTrackedImageLayouts(image);
            Api!.GetImageMemoryRequirements(Device, image, out MemoryRequirements memRequirements);
            if (!Renderer.TryAllocateImageMemoryWithFallback(
                    image,
                    MemoryProperties,
                    out VulkanMemoryAllocation allocation,
                    out string allocationFailure))
            {
                Api!.DestroyImage(Device, image, null);
                image = default;
                failureReason = allocationFailure;
                return false;
            }

            Renderer._imageAllocations[image.Handle] = allocation;
            memory = allocation.Memory;

            Result bindResult = Api!.BindImageMemory(Device, image, allocation.Memory, allocation.Offset);
            if (bindResult != Result.Success)
            {
                Renderer._imageAllocations.TryRemove(image.Handle, out _);
                Renderer.FreeMemoryAllocation(allocation);
                Api!.DestroyImage(Device, image, null);
                image = default;
                memory = default;
                failureReason = $"failed to bind synchronized imported texture image memory ({bindResult})";
                return false;
            }

            committedBytes = (long)memRequirements.Size;
            RuntimeEngine.Rendering.Stats.Vram.AddTextureAllocation(committedBytes);
            return true;
        }

        private ImageView CreateImportedUploadImageView(
            Image image,
            Format format,
            ImageAspectFlags aspectMask,
            uint mipLevels,
            uint arrayLayers)
        {
            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = NormalizeImageViewTypeForLayerCount(DefaultViewType, arrayLayers),
                Format = format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = 0,
                    LevelCount = mipLevels,
                    BaseArrayLayer = 0,
                    LayerCount = arrayLayers,
                }
            };

            if (Api!.CreateImageView(Device, ref viewInfo, null, out ImageView created) != Result.Success)
                throw new Exception("Failed to create synchronized imported texture image view.");

            Renderer.TrackLiveImageView(created, in viewInfo, "VkImageBackedTexture.ImportedUploadView");
            return created;
        }

        private Sampler CreateImportedUploadSampler()
        {
            var (minFilter, magFilter, mipmapMode, uWrap, vWrap, wWrap, lodBias) = ReadSamplerSettingsFromData();
            var (minLod, maxLod) = ResolveSamplerLodRange();
            var (compareEnable, compareOp) = ReadCompareSettingsFromData();

            uint anisotropyEnable = Vk.False;
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
                CompareEnable = compareEnable,
                CompareOp = compareOp,
                MipmapMode = mipmapMode,
                MipLodBias = lodBias,
                MinLod = minLod,
                MaxLod = maxLod,
            };

            if (Api!.CreateSampler(Device, ref samplerInfo, null, out Sampler created) != Result.Success)
                throw new Exception("Failed to create synchronized imported texture sampler.");

            Renderer.RegisterLiveSampler(created, in samplerInfo);
            return created;
        }

        internal void ReleasePreparedImportedUploadResources(VulkanImportedTexturePendingUpload pendingUpload)
        {
            ReleasePreparedImportedUploadResources(
                pendingUpload.Image,
                pendingUpload.Memory,
                pendingUpload.ImageView,
                pendingUpload.Sampler,
                pendingUpload.CommittedBytes,
                pendingUpload.StagingResources);
            pendingUpload.DetachPublishedImageHandles();
        }

        private void ReleasePreparedImportedUploadResources(
            Image image,
            DeviceMemory memory,
            ImageView imageView,
            Sampler sampler,
            long committedBytes,
            VulkanImportedTextureUploadStagingResource[] stagingResources)
        {
            for (int i = 0; i < stagingResources.Length; i++)
            {
                VulkanImportedTextureUploadStagingResource staging = stagingResources[i];
                Renderer.RetireBuffer(staging.Buffer, staging.Memory);
            }

            if (image.Handle != 0 || memory.Handle != 0 || imageView.Handle != 0 || sampler.Handle != 0)
            {
                Renderer.RetireImageResources(new RetiredImageResources(
                    image,
                    memory,
                    imageView,
                    [],
                    sampler,
                    committedBytes));
            }

            if (committedBytes > 0)
                RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(committedBytes);
        }

        internal void PublishSynchronizedImportedTextureUpload(VulkanImportedTexturePendingUpload pendingUpload)
        {
            if (!ReferenceEquals(pendingUpload.Texture, this))
                throw new InvalidOperationException("Imported texture upload publication target does not match the prepared texture wrapper.");

            ImageView[] retiredAttachmentViews;
            if (_attachmentViews.Count > 0)
            {
                retiredAttachmentViews = new ImageView[_attachmentViews.Count];
                int index = 0;
                foreach ((_, ImageView attachmentView) in _attachmentViews)
                    retiredAttachmentViews[index++] = attachmentView;
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

            if (_ownsImageMemory && _allocatedVRAMBytes > 0)
                RuntimeEngine.Rendering.Stats.Vram.RemoveTextureAllocation(_allocatedVRAMBytes);

            _image = pendingUpload.Image;
            _memory = pendingUpload.Memory;
            _view = pendingUpload.ImageView;
            _sampler = pendingUpload.Sampler;
            _ownsImageMemory = true;
            _physicalGroup = null;
            _extentOverride = null;
            _formatOverride = null;
            _arrayLayersOverride = null;
            _mipLevelsOverride = null;
            _samplesOverride = null;
            _allocatedVRAMBytes = pendingUpload.CommittedBytes;
            _layout = new TextureLayout(
                pendingUpload.Extent,
                Math.Max(pendingUpload.ArrayLayers, 1u),
                Math.Max(pendingUpload.MipLevels, 1u));
            _layoutInitialized = true;
            Format = pendingUpload.Format;
            AspectFlags = pendingUpload.AspectMask;
            _attachmentViews.Clear();
            _currentImageLayout = ImageLayout.ShaderReadOnlyOptimal;
            ResetAttachmentLayoutTracking();
            MarkUploaded();
            if (!IsActive)
            {
                PreGenerated();
                _bindingId = CacheObject(this);
                PostGenerated();
            }

            pendingUpload.DetachPublishedImageHandles();
            Renderer.MarkCommandBuffersDirty(
                $"ImportedTextureUploadPublished texture='{ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName()}' descriptorGeneration={DescriptorGeneration}");
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
            if (_image.Handle == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.ViewWithoutImage.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping image-view creation for texture '{0}' because no VkImage is available.",
                    ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName());
                return;
            }

            ImageAspectFlags normalizedAspect = NormalizeAspectMaskForFormat(ResolvedFormat, AspectFlags);
            AspectFlags = normalizedAspect;

            AttachmentViewKey descriptor = key == default
                ? new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, DefaultViewType, normalizedAspect)
                : key;

            descriptor = NormalizeAttachmentViewKey(descriptor);
            _view = CreateView(descriptor);
        }

        /// <summary>
        /// Creates a Vulkan <see cref="ImageView"/> for the given subresource descriptor.
        /// The aspect mask is normalised to ensure depth/stencil formats don't include the
        /// color bit.
        /// </summary>
        private ImageView CreateView(AttachmentViewKey descriptor)
        {
            if (_image.Handle == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.SubresourceViewWithoutImage.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping subresource image-view creation for texture '{0}' because no VkImage is available.",
                    ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName());
                return default;
            }

            ImageAspectFlags aspectMask = NormalizeAspectMaskForFormat(ResolvedFormat, descriptor.AspectMask);
            descriptor = NormalizeAttachmentViewKey(descriptor with { AspectMask = aspectMask });

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
            Renderer.TrackLiveImageView(created, in viewInfo, "VkImageBackedTexture.View");
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

        private static bool HasStencilAspect(Format format)
            => format is Format.D16UnormS8Uint
                or Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint;

        private static AttachmentViewKey NormalizeAttachmentViewKey(AttachmentViewKey descriptor)
            => descriptor with
            {
                LevelCount = Math.Max(descriptor.LevelCount, 1u),
                LayerCount = Math.Max(descriptor.LayerCount, 1u),
                ViewType = NormalizeImageViewTypeForLayerCount(descriptor.ViewType, descriptor.LayerCount),
            };

        private static ImageViewType NormalizeImageViewTypeForLayerCount(ImageViewType viewType, uint layerCount)
        {
            if (layerCount <= 1u)
                return viewType;

            return viewType switch
            {
                ImageViewType.Type1D => ImageViewType.Type1DArray,
                ImageViewType.Type2D => ImageViewType.Type2DArray,
                _ => viewType,
            };
        }

        /// <summary>Destroys a single image view and resets the handle to <c>default</c>.</summary>
        private void DestroyView(ref ImageView view)
        {
            if (view.Handle != 0)
            {
                Renderer.RetireImageResources(new RetiredImageResources(
                    default,
                    default,
                    view,
                    [],
                    default,
                    0));
                view = default;
            }
        }

        /// <summary>Destroys the primary view and all cached attachment views.</summary>
        private void DestroyAllViews()
        {
            DestroyCurrentViews(removeActiveCacheEntry: true);
            DestroyPhysicalImageViewCache();
        }

        /// <summary>Destroys only the views for the currently active physical image.</summary>
        private void DestroyCurrentViews(bool removeActiveCacheEntry)
        {
            ImageView primaryView = _view;
            ImageView[] attachmentViews;
            if (_attachmentViews.Count > 0)
            {
                attachmentViews = new ImageView[_attachmentViews.Count];
                int index = 0;
                foreach ((_, ImageView attachmentView) in _attachmentViews)
                    attachmentViews[index++] = attachmentView;
            }
            else
            {
                attachmentViews = [];
            }

            if (primaryView.Handle != 0 || attachmentViews.Length != 0)
            {
                Renderer.RetireImageResources(new RetiredImageResources(
                    default,
                    default,
                    primaryView,
                    attachmentViews,
                    default,
                    0));
            }

            _view = default;
            _attachmentViews.Clear();

            if (removeActiveCacheEntry && _image.Handle != 0)
                RemovePhysicalImageViewCacheEntry(_physicalGroup, _image.Handle);
        }

        private void ForgetCurrentViews(bool removeActiveCacheEntry)
        {
            _view = default;
            _attachmentViews.Clear();

            if (removeActiveCacheEntry && _image.Handle != 0)
                RemovePhysicalImageViewCacheEntry(_physicalGroup, _image.Handle);
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
            lock (_imageStateLock)
            {
                RefreshPhysicalGroupImageIfStaleNoLock();
                if (_image.Handle == 0)
                {
                    AcquireImageHandle();
                    RefreshPhysicalGroupImageIfStaleNoLock();
                }

                if (_image.Handle == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Texture.AttachmentViewWithoutImage.{Data.GetHashCode()}",
                        TimeSpan.FromSeconds(2),
                        "[Vulkan] Texture '{0}' has no VkImage for framebuffer attachment view.",
                        ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName());
                    return default;
                }

                if (_view.Handle != 0 && !IsImageViewBackedByCurrentImage(_view))
                    _view = default;

                if (_view.Handle == 0)
                    CreateImageView(default);

                AttachmentViewKey key = BuildAttachmentViewKey(mipLevel, layerIndex);
                if (key == default)
                {
                    if (BloomVulkanDiagnosticsEnabled && IsBloomDiagnosticName(ResolveLogicalResourceName() ?? Data.Name))
                    {
                        Debug.VulkanEvery(
                            $"Vulkan.BloomDiag.AttachmentView.Primary.{ResolveLogicalResourceName() ?? Data.Name}.{mipLevel}.{layerIndex}",
                            TimeSpan.FromSeconds(1),
                            "[BloomDiag][Vulkan] attachmentView texture='{0}' requestedMip={1} resolvedMip=0 layer={2} key=primary view=0x{3:X} image=0x{4:X} mips={5} layers={6}",
                            ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
                            mipLevel,
                            layerIndex,
                            _view.Handle,
                            _image.Handle,
                            ResolvedMipLevels,
                            ResolvedArrayLayers);
                    }
                    return _view;
                }

                if (_attachmentViews.TryGetValue(key, out ImageView cached) &&
                    !IsImageViewBackedByCurrentImage(cached))
                {
                    _attachmentViews.Remove(key);
                    cached = default;
                }

                if (cached.Handle == 0)
                {
                    cached = CreateView(key);
                    if (cached.Handle != 0)
                        _attachmentViews[key] = cached;
                }

                if (BloomVulkanDiagnosticsEnabled && IsBloomDiagnosticName(ResolveLogicalResourceName() ?? Data.Name))
                {
                    Debug.VulkanEvery(
                        $"Vulkan.BloomDiag.AttachmentView.{ResolveLogicalResourceName() ?? Data.Name}.{mipLevel}.{layerIndex}.{key.BaseMipLevel}.{key.BaseArrayLayer}.{cached.Handle}",
                        TimeSpan.FromSeconds(1),
                        "[BloomDiag][Vulkan] attachmentView texture='{0}' requestedMip={1} resolvedMip={2} requestedLayer={3} baseLayer={4} levelCount={5} layerCount={6} view=0x{7:X} image=0x{8:X} mips={9} layers={10}",
                        ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
                        mipLevel,
                        key.BaseMipLevel,
                        layerIndex,
                        key.BaseArrayLayer,
                        key.LevelCount,
                        key.LayerCount,
                        cached.Handle,
                        _image.Handle,
                        ResolvedMipLevels,
                        ResolvedArrayLayers);
                }

                return cached;
            }
        }

        private bool IsImageViewBackedByCurrentImage(ImageView view)
        {
            if (view.Handle == 0 || _image.Handle == 0)
                return false;

            return Renderer.TryGetImageViewBackingImage(view, out Image backingImage) &&
                backingImage.Handle == _image.Handle &&
                Renderer.IsLiveImageViewBackedByLiveImage(view);
        }

        bool IVkFrameBufferAttachmentSource.TryGetAttachmentExtent(int mipLevel, int layerIndex, out Extent2D extent)
        {
            lock (_imageStateLock)
            {
                RefreshPhysicalGroupImageIfStaleNoLock();
                if (_image.Handle == 0)
                {
                    AcquireImageHandle();
                    RefreshPhysicalGroupImageIfStaleNoLock();
                }

                Extent3D resolvedExtent = ResolvedExtent;
                if (resolvedExtent.Width == 0 || resolvedExtent.Height == 0)
                {
                    extent = default;
                    return false;
                }

                uint baseMip = ClampAttachmentMipLevel(mipLevel);
                uint width = Math.Max(resolvedExtent.Width >> (int)baseMip, 1u);
                uint height = Math.Max(resolvedExtent.Height >> (int)baseMip, 1u);
                extent = new Extent2D(width, height);
                return true;
            }
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
            uint baseMip = ClampAttachmentMipLevel(mipLevel);

            // Framebuffer attachments require single-mip-level views (levelCount=1).
            // Only reuse the default full-mip view when it already has exactly 1 level
            // and 1 layer — otherwise we must create a single-mip view.
            if (baseMip == 0 && layerIndex < 0 && ResolvedMipLevels <= 1 && ResolvedArrayLayers <= 1)
                return default;

            return new AttachmentViewKey(baseMip, 1, 0, 1, ImageViewType.Type2D, AspectFlags);
        }

        protected uint ClampAttachmentMipLevel(int mipLevel)
        {
            uint mipCount = Math.Max(ResolvedMipLevels, 1u);
            uint requested = (uint)Math.Max(mipLevel, 0);
            return Math.Min(requested, mipCount - 1u);
        }

        protected uint ClampAttachmentLayerIndex(int layerIndex)
        {
            uint layerCount = Math.Max(ResolvedArrayLayers, 1u);
            uint requested = (uint)Math.Max(layerIndex, 0);
            return Math.Min(requested, layerCount - 1u);
        }

        private AttachmentLayoutKey BuildAttachmentLayoutKey(int mipLevel, int layerIndex)
        {
            uint baseMip = (uint)Math.Max(mipLevel, 0);
            if (layerIndex < 0)
                return new AttachmentLayoutKey(baseMip, 0u, Math.Max(ResolvedArrayLayers, 1u));

            return new AttachmentLayoutKey(baseMip, (uint)layerIndex, 1u);
        }

        private bool TryResolveAllLayerAttachmentLayout(uint mipLevel, out ImageLayout layout)
        {
            layout = ImageLayout.Undefined;

            ImageLayout? common = null;
            uint layerCount = Math.Max(ResolvedArrayLayers, 1u);
            for (uint layer = 0; layer < layerCount; layer++)
            {
                AttachmentLayoutKey key = new(mipLevel, layer, 1u);
                if (!_attachmentLayouts.TryGetValue(key, out ImageLayout layerLayout))
                    return false;

                if (common.HasValue && common.Value != layerLayout)
                    return false;

                common = layerLayout;
            }

            if (!common.HasValue)
                return false;

            layout = common.Value;
            return true;
        }

        private bool TryResolveWholeImageAttachmentLayout(out ImageLayout layout)
        {
            layout = ImageLayout.Undefined;

            if (!_hasPartialAttachmentLayouts)
            {
                layout = _physicalGroup is not null
                    ? ResolvePhysicalGroupWholeImageLayout()
                    : _currentImageLayout;
                return layout != ImageLayout.Undefined;
            }

            ImageLayout? common = null;
            uint mipCount = Math.Max(ResolvedMipLevels, 1u);
            uint layerCount = Math.Max(ResolvedArrayLayers, 1u);
            for (uint mip = 0; mip < mipCount; mip++)
            {
                for (uint layer = 0; layer < layerCount; layer++)
                {
                    AttachmentLayoutKey key = new(mip, layer, 1u);
                    if (!_attachmentLayouts.TryGetValue(key, out ImageLayout subresourceLayout) ||
                        subresourceLayout == ImageLayout.Undefined)
                    {
                        return false;
                    }

                    if (common.HasValue && common.Value != subresourceLayout)
                        return false;

                    common = subresourceLayout;
                }
            }

            if (!common.HasValue)
                return false;

            layout = common.Value;
            return true;
        }

        private bool AttachmentCoversWholeImage(int mipLevel, int layerIndex)
        {
            uint resolvedMip = ClampAttachmentMipLevel(mipLevel);
            bool coversAllMips = resolvedMip == 0 && Math.Max(ResolvedMipLevels, 1u) == 1u;
            bool coversAllLayers = layerIndex < 0 || Math.Max(ResolvedArrayLayers, 1u) == 1u;
            return coversAllMips && coversAllLayers;
        }

        private void UpdateWholeImageLayoutFromAttachmentTracking()
        {
            if (TryResolveWholeImageAttachmentLayout(out ImageLayout commonLayout))
            {
                _currentImageLayout = commonLayout;
                if (_physicalGroup is not null && Math.Max(ResolvedMipLevels, 1u) == 1u && Math.Max(ResolvedArrayLayers, 1u) == 1u)
                    _physicalGroup.LastKnownLayout = commonLayout;
                return;
            }

            _currentImageLayout = ImageLayout.Undefined;
        }

        private void BeginPartialAttachmentLayoutTracking()
        {
            if (_hasPartialAttachmentLayouts)
                return;

            _hasPartialAttachmentLayouts = true;
            _attachmentLayouts.Clear();

            ImageLayout wholeImageLayout = _physicalGroup is not null
                ? _physicalGroup.LastKnownLayout
                : _currentImageLayout;

            if (wholeImageLayout == ImageLayout.Undefined)
                return;

            uint mipCount = Math.Max(ResolvedMipLevels, 1u);
            uint layerCount = Math.Max(ResolvedArrayLayers, 1u);
            for (uint mip = 0; mip < mipCount; mip++)
            {
                for (uint layer = 0; layer < layerCount; layer++)
                    _attachmentLayouts[new AttachmentLayoutKey(mip, layer, 1u)] = wholeImageLayout;
            }
        }

        private void ResetAttachmentLayoutTracking()
        {
            _attachmentLayouts.Clear();
            _hasPartialAttachmentLayouts = false;
        }

        #endregion

        #region Sampler Management

        /// <summary>Destroys the sampler and resets the handle.</summary>
        private void DestroySampler()
        {
            if (_sampler.Handle != 0)
            {
                Renderer.RetireSampler(_sampler);
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

        private SampleCountFlags ReadSampleCountFromData()
            => Data switch
            {
                XRTexture2D tex2D => ToSampleCountFlags(tex2D.MultiSampleCount),
                XRTexture2DArray texArray when texArray.MultiSample && texArray.Textures.Length > 0
                    => ToSampleCountFlags(Math.Max(2u, texArray.Textures[0].MultiSampleCount)),
                _ => SampleCountFlags.Count1Bit,
            };

        private static SampleCountFlags ToSampleCountFlags(uint samples)
            => samples switch
            {
                <= 1u => SampleCountFlags.Count1Bit,
                2u => SampleCountFlags.Count2Bit,
                3u or 4u => SampleCountFlags.Count4Bit,
                <= 8u => SampleCountFlags.Count8Bit,
                <= 16u => SampleCountFlags.Count16Bit,
                <= 32u => SampleCountFlags.Count32Bit,
                _ => SampleCountFlags.Count64Bit,
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
                    engineU = t.UWrap; engineV = t.VWrap; lodBias = t.LodBias;
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
            var (minLod, maxLod) = ResolveSamplerLodRange();
            var (compareEnable, compareOp) = ReadCompareSettingsFromData();

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
                CompareEnable = compareEnable,
                CompareOp = compareOp,
                MipmapMode = mipmapMode,
                MipLodBias = lodBias,
                MinLod = minLod,
                MaxLod = maxLod,
            };

            if (Api!.CreateSampler(Device, ref samplerInfo, null, out _sampler) != Result.Success)
                throw new Exception("Failed to create sampler.");

            Renderer.RegisterLiveSampler(_sampler, in samplerInfo);
        }

        private (float minLod, float maxLod) ResolveSamplerLodRange()
        {
            float maxMip = Math.Max(0f, ResolvedMipLevels - 1u);
            float min = Math.Clamp(Math.Max(Data.MinLOD, Data.LargestMipmapLevel), 0f, maxMip);
            float max = Math.Clamp(Math.Min(Data.MaxLOD, Data.SmallestAllowedMipmapLevel), min, maxMip);
            return (min, max);
        }

        private (uint compareEnable, CompareOp compareOp) ReadCompareSettingsFromData()
        {
            bool enabled = false;
            ETextureCompareFunc func = ETextureCompareFunc.LessOrEqual;

            switch (Data)
            {
                case XRTexture2D texture2D:
                    enabled = texture2D.EnableComparison;
                    func = texture2D.CompareFunc;
                    break;
                case XRTexture2DArray texture2DArray:
                    enabled = texture2DArray.EnableComparison;
                    func = texture2DArray.CompareFunc;
                    break;
            }

            return (enabled ? Vk.True : Vk.False, enabled ? SamplerConversions.FromCompareOp(func) : CompareOp.Always);
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
            if (Renderer.IsDeviceLost || Image.Handle == 0)
                return;

            RefreshPhysicalGroupImageIfStale();

            ImageLayout liveLayout = CurrentImageLayout;
            if (liveLayout != oldLayout)
                oldLayout = liveLayout;

            oldLayout = CoerceLayoutForUsage(oldLayout);
            newLayout = CoerceLayoutForUsage(newLayout);
            AssembleTransitionImageLayout(oldLayout, newLayout, out ImageMemoryBarrier barrier, out PipelineStageFlags src, out PipelineStageFlags dst);
            using var scope = Renderer.NewCommandScope();
            Renderer.CmdPipelineBarrierTracked(scope.CommandBuffer, src, dst, 0, 0, null, 0, null, 1, &barrier);
            _currentImageLayout = newLayout;
            if (_physicalGroup is not null)
                _physicalGroup.LastKnownLayout = newLayout;
            ResetAttachmentLayoutTracking();
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

            bool canSample = (Usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0;
            bool isDepthOrStencil = (AspectFlags & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
                VkFormatConversions.IsDepthStencilFormat(ResolvedFormat);
            if (canSample && isDepthOrStencil)
                return ImageLayout.DepthStencilReadOnlyOptimal;

            if (canSample)
                return requested;

            if ((Usage & ImageUsageFlags.StorageBit) != 0)
                return ImageLayout.General;

            return ImageLayout.TransferSrcOptimal;
        }

        /// <summary>
        /// Builds the <see cref="ImageMemoryBarrier"/> and selects appropriate pipeline stages
        /// for transitioning from <paramref name="oldLayout"/> to <paramref name="newLayout"/>.
        /// Common transitions (undefined→transfer-dst, transfer-dst→shader-read) use precise
        /// stages; other pairs derive stages/access per layout role, falling back to
        /// <c>AllCommands</c> only for unrecognized layouts.
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
            else if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.ColorAttachmentOptimal)
            {
                barrier.SrcAccessMask = 0;
                barrier.DstAccessMask = AccessFlags.ColorAttachmentWriteBit;
                sourceStage = PipelineStageFlags.TopOfPipeBit;
                destinationStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }
            else if (oldLayout == ImageLayout.Undefined && (newLayout == ImageLayout.DepthStencilAttachmentOptimal || newLayout == ImageLayout.DepthAttachmentOptimal))
            {
                barrier.SrcAccessMask = 0;
                barrier.DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit;
                sourceStage = PipelineStageFlags.TopOfPipeBit;
                destinationStage = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
            }
            else if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.General)
            {
                barrier.SrcAccessMask = 0;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                sourceStage = PipelineStageFlags.TopOfPipeBit;
                destinationStage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
            }
            else if (oldLayout == ImageLayout.Undefined && (newLayout == ImageLayout.ShaderReadOnlyOptimal || newLayout == ImageLayout.DepthStencilReadOnlyOptimal))
            {
                barrier.SrcAccessMask = 0;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                sourceStage = PipelineStageFlags.TopOfPipeBit;
                destinationStage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
            }
            else if (oldLayout == ImageLayout.ColorAttachmentOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
            {
                barrier.SrcAccessMask = AccessFlags.ColorAttachmentWriteBit;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                sourceStage = PipelineStageFlags.ColorAttachmentOutputBit;
                destinationStage = PipelineStageFlags.FragmentShaderBit;
            }
            else if ((oldLayout == ImageLayout.DepthStencilAttachmentOptimal || oldLayout == ImageLayout.DepthAttachmentOptimal) &&
                (newLayout == ImageLayout.ShaderReadOnlyOptimal || newLayout == ImageLayout.DepthStencilReadOnlyOptimal))
            {
                barrier.SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                sourceStage = PipelineStageFlags.LateFragmentTestsBit;
                destinationStage = PipelineStageFlags.FragmentShaderBit;
            }
            else if (oldLayout == ImageLayout.ShaderReadOnlyOptimal && newLayout == ImageLayout.TransferSrcOptimal)
            {
                barrier.SrcAccessMask = AccessFlags.ShaderReadBit;
                barrier.DstAccessMask = AccessFlags.TransferReadBit;
                sourceStage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
                destinationStage = PipelineStageFlags.TransferBit;
            }
            else if (oldLayout == ImageLayout.TransferSrcOptimal && newLayout == ImageLayout.ShaderReadOnlyOptimal)
            {
                barrier.SrcAccessMask = AccessFlags.TransferReadBit;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit;
                sourceStage = PipelineStageFlags.TransferBit;
                destinationStage = PipelineStageFlags.FragmentShaderBit;
            }
            else if (oldLayout == ImageLayout.ColorAttachmentOptimal && newLayout == ImageLayout.TransferSrcOptimal)
            {
                barrier.SrcAccessMask = AccessFlags.ColorAttachmentWriteBit;
                barrier.DstAccessMask = AccessFlags.TransferReadBit;
                sourceStage = PipelineStageFlags.ColorAttachmentOutputBit;
                destinationStage = PipelineStageFlags.TransferBit;
            }
            else if (oldLayout == ImageLayout.General && newLayout == ImageLayout.TransferDstOptimal)
            {
                barrier.SrcAccessMask = AccessFlags.ShaderWriteBit;
                barrier.DstAccessMask = AccessFlags.TransferWriteBit;
                sourceStage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
                destinationStage = PipelineStageFlags.TransferBit;
            }
            else if (oldLayout == ImageLayout.TransferDstOptimal && newLayout == ImageLayout.General)
            {
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                sourceStage = PipelineStageFlags.TransferBit;
                destinationStage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
            }
            else
            {
                // Derive stages/access from the layout roles instead of AllCommands.
                // Unrecognized layouts still fall back to broad masks inside the helpers.
                GetLayoutSourceSync(oldLayout, out sourceStage, out AccessFlags srcAccess);
                GetLayoutDestinationSync(newLayout, out destinationStage, out AccessFlags dstAccess);
                barrier.SrcAccessMask = srcAccess;
                barrier.DstAccessMask = dstAccess;
            }
        }

        /// <summary>
        /// Derives the pipeline stages and access mask covering all prior GPU work for an
        /// image leaving <paramref name="layout"/>. Unrecognized layouts fall back to broad masks.
        /// </summary>
        private static void GetLayoutSourceSync(ImageLayout layout, out PipelineStageFlags stage, out AccessFlags access)
        {
            switch (layout)
            {
                case ImageLayout.Undefined:
                case ImageLayout.Preinitialized:
                    stage = PipelineStageFlags.TopOfPipeBit;
                    access = 0;
                    break;
                case ImageLayout.General:
                    // Storage-image usage: written by compute or fragment shaders.
                    stage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
                    access = AccessFlags.ShaderWriteBit;
                    break;
                case ImageLayout.ColorAttachmentOptimal:
                    stage = PipelineStageFlags.ColorAttachmentOutputBit;
                    access = AccessFlags.ColorAttachmentWriteBit;
                    break;
                case ImageLayout.DepthStencilAttachmentOptimal:
                case ImageLayout.DepthAttachmentOptimal:
                    stage = PipelineStageFlags.LateFragmentTestsBit;
                    access = AccessFlags.DepthStencilAttachmentWriteBit;
                    break;
                case ImageLayout.ShaderReadOnlyOptimal:
                case ImageLayout.DepthStencilReadOnlyOptimal:
                    // Prior reads need execution ordering only; no writes to make available.
                    stage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
                    access = 0;
                    break;
                case ImageLayout.TransferSrcOptimal:
                    stage = PipelineStageFlags.TransferBit;
                    access = 0;
                    break;
                case ImageLayout.TransferDstOptimal:
                    stage = PipelineStageFlags.TransferBit;
                    access = AccessFlags.TransferWriteBit;
                    break;
                case ImageLayout.PresentSrcKhr:
                    stage = PipelineStageFlags.BottomOfPipeBit;
                    access = 0;
                    break;
                default:
                    stage = PipelineStageFlags.AllCommandsBit;
                    access = AccessFlags.MemoryWriteBit;
                    break;
            }
        }

        /// <summary>
        /// Derives the pipeline stages and access mask covering the first GPU work consuming
        /// an image entering <paramref name="layout"/>. Unrecognized layouts fall back to broad masks.
        /// </summary>
        private static void GetLayoutDestinationSync(ImageLayout layout, out PipelineStageFlags stage, out AccessFlags access)
        {
            switch (layout)
            {
                case ImageLayout.General:
                    stage = PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;
                    access = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
                    break;
                case ImageLayout.ColorAttachmentOptimal:
                    stage = PipelineStageFlags.ColorAttachmentOutputBit;
                    access = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
                    break;
                case ImageLayout.DepthStencilAttachmentOptimal:
                case ImageLayout.DepthAttachmentOptimal:
                    stage = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
                    access = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;
                    break;
                case ImageLayout.ShaderReadOnlyOptimal:
                case ImageLayout.DepthStencilReadOnlyOptimal:
                    stage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit;
                    access = AccessFlags.ShaderReadBit;
                    break;
                case ImageLayout.TransferSrcOptimal:
                    stage = PipelineStageFlags.TransferBit;
                    access = AccessFlags.TransferReadBit;
                    break;
                case ImageLayout.TransferDstOptimal:
                    stage = PipelineStageFlags.TransferBit;
                    access = AccessFlags.TransferWriteBit;
                    break;
                case ImageLayout.PresentSrcKhr:
                    stage = PipelineStageFlags.BottomOfPipeBit;
                    access = 0;
                    break;
                default:
                    stage = PipelineStageFlags.AllCommandsBit;
                    access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                    break;
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
            if (!ValidateCopyBufferToImageRegion(mipLevel, baseArrayLayer, layerCount, extent))
                return;

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

            ImageLayout currentLayout = CurrentImageLayout;
            if (currentLayout != ImageLayout.TransferDstOptimal)
                TransitionImageLayout(currentLayout, ImageLayout.TransferDstOptimal);

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

                    Renderer.CmdPipelineBarrierTracked(
                        transferScope.CommandBuffer,
                        PipelineStageFlags.BottomOfPipeBit,
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

                    Renderer.CmdPipelineBarrierTracked(
                        transferScope.CommandBuffer,
                        PipelineStageFlags.TransferBit,
                        PipelineStageFlags.BottomOfPipeBit,
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

                Renderer.CmdPipelineBarrierTracked(
                    graphicsScope.CommandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                    DependencyFlags.None,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &acquireOnGraphics);
            }
        }

        private bool ValidateCopyBufferToImageRegion(uint mipLevel, uint baseArrayLayer, uint layerCount, Extent3D extent)
        {
            if (_image.Handle == 0)
                return false;

            uint mipCount = Math.Max(ResolvedMipLevels, 1u);
            uint arrayLayerCount = Math.Max(ResolvedArrayLayers, 1u);
            if (mipLevel >= mipCount)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.CopyMipOutOfRange.{Data.GetHashCode()}.{mipLevel}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping CopyBufferToImage for '{0}': mip {1} is outside image mip count {2}.",
                    Data.Name ?? GetDescribingName(),
                    mipLevel,
                    mipCount);
                return false;
            }

            if (layerCount == 0 || baseArrayLayer >= arrayLayerCount || layerCount > arrayLayerCount - baseArrayLayer)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.CopyLayerOutOfRange.{Data.GetHashCode()}.{baseArrayLayer}.{layerCount}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping CopyBufferToImage for '{0}': layers {1}+{2} exceed image layer count {3}.",
                    Data.Name ?? GetDescribingName(),
                    baseArrayLayer,
                    layerCount,
                    arrayLayerCount);
                return false;
            }

            if (extent.Width == 0 || extent.Height == 0 || extent.Depth == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.CopyZeroExtent.{Data.GetHashCode()}.{mipLevel}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Skipping CopyBufferToImage for '{0}': requested extent {1}x{2}x{3} is invalid.",
                    Data.Name ?? GetDescribingName(),
                    extent.Width,
                    extent.Height,
                    extent.Depth);
                return false;
            }

            Extent3D baseExtent = ResolvedExtent;
            Extent3D mipExtent = ResolveMipExtent(baseExtent, mipLevel);
            if (extent.Width <= mipExtent.Width && extent.Height <= mipExtent.Height && extent.Depth <= mipExtent.Depth)
                return true;

            Debug.VulkanWarningEvery(
                $"Vulkan.Texture.CopyExtentOutOfRange.{Data.GetHashCode()}.{mipLevel}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Skipping CopyBufferToImage for '{0}': requested extent {1}x{2}x{3} exceeds mip {4} extent {5}x{6}x{7} (base {8}x{9}x{10}, mips={11}).",
                Data.Name ?? GetDescribingName(),
                extent.Width,
                extent.Height,
                extent.Depth,
                mipLevel,
                mipExtent.Width,
                mipExtent.Height,
                mipExtent.Depth,
                baseExtent.Width,
                baseExtent.Height,
                baseExtent.Depth,
                mipCount);
            return false;
        }

        private static Extent3D ResolveMipExtent(Extent3D baseExtent, uint mipLevel)
        {
            uint width = Math.Max(baseExtent.Width, 1u);
            uint height = Math.Max(baseExtent.Height, 1u);
            uint depth = Math.Max(baseExtent.Depth, 1u);

            for (uint i = 0; i < mipLevel; i++)
            {
                if (width > 1)
                    width >>= 1;
                if (height > 1)
                    height >>= 1;
                if (depth > 1)
                    depth >>= 1;
            }

            return new Extent3D(width, height, depth);
        }

        #endregion

        #region Descriptor Helpers

        /// <summary>
        /// Convenience method to build a <see cref="DescriptorImageInfo"/> for this texture
        /// using the primary view, sampler, and <see cref="ImageLayout.ShaderReadOnlyOptimal"/>.
        /// </summary>
        public DescriptorImageInfo CreateImageInfo()
        {
            lock (_imageStateLock)
            {
                RefreshPhysicalGroupImageIfStaleNoLock();
                return new DescriptorImageInfo
                {
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    ImageView = _view,
                    Sampler = _sampler,
                };
            }
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
        private void SaveCurrentPhysicalImageViewCache()
        {
            if (_physicalGroup is null || _image.Handle == 0)
                return;

            int cacheIndex = FindPhysicalImageViewCacheIndex(_physicalGroup, _image.Handle);
            PhysicalImageViewCacheEntry entry = new(
                _physicalGroup,
                _image.Handle,
                _view,
                new Dictionary<AttachmentViewKey, ImageView>(_attachmentViews));

            if (cacheIndex >= 0)
                _physicalImageViewCache[cacheIndex] = entry;
            else
                _physicalImageViewCache.Add(entry);
        }

        private bool TryRestorePhysicalImageViewCache(VulkanPhysicalImageGroup group, Image image)
        {
            int cacheIndex = FindPhysicalImageViewCacheIndex(group, image.Handle);
            if (cacheIndex < 0)
                return false;

            PhysicalImageViewCacheEntry entry = _physicalImageViewCache[cacheIndex];
            if (!IsCachedImageViewBackedByImage(entry.PrimaryView, image))
                return false;

            _view = entry.PrimaryView;
            _attachmentViews.Clear();
            foreach (KeyValuePair<AttachmentViewKey, ImageView> pair in entry.AttachmentViews)
            {
                if (IsCachedImageViewBackedByImage(pair.Value, image))
                    _attachmentViews[pair.Key] = pair.Value;
            }
            return _view.Handle != 0;
        }

        private bool IsCachedImageViewBackedByImage(ImageView view, Image image)
        {
            if (view.Handle == 0 || image.Handle == 0)
                return false;

            return Renderer.TryGetImageViewBackingImage(view, out Image backingImage) &&
                backingImage.Handle == image.Handle &&
                Renderer.IsLiveImageViewBackedByLiveImage(view);
        }

        private int FindPhysicalImageViewCacheIndex(VulkanPhysicalImageGroup? group, ulong imageHandle)
        {
            if (group is null || imageHandle == 0)
                return -1;

            for (int i = 0; i < _physicalImageViewCache.Count; i++)
            {
                PhysicalImageViewCacheEntry entry = _physicalImageViewCache[i];
                if (ReferenceEquals(entry.Group, group) && entry.ImageHandle == imageHandle)
                    return i;
            }

            return -1;
        }

        private void RemovePhysicalImageViewCacheEntry(VulkanPhysicalImageGroup? group, ulong imageHandle)
        {
            int cacheIndex = FindPhysicalImageViewCacheIndex(group, imageHandle);
            if (cacheIndex >= 0)
                _physicalImageViewCache.RemoveAt(cacheIndex);
        }

        private void DestroyPhysicalImageViewCache()
        {
            if (_physicalImageViewCache.Count == 0)
                return;

            List<ImageView> cachedViews = [];
            HashSet<ulong> seenHandles = [];
            foreach (PhysicalImageViewCacheEntry entry in _physicalImageViewCache)
            {
                AddUniqueView(entry.PrimaryView);
                foreach (ImageView view in entry.AttachmentViews.Values)
                    AddUniqueView(view);
            }

            if (cachedViews.Count > 0)
            {
                Renderer.RetireImageResources(new RetiredImageResources(
                    default,
                    default,
                    default,
                    [.. cachedViews],
                    default,
                    0));
            }

            _physicalImageViewCache.Clear();

            void AddUniqueView(ImageView view)
            {
                if (view.Handle == 0 || !seenHandles.Add(view.Handle))
                    return;
                cachedViews.Add(view);
            }
        }

        private sealed record class PhysicalImageViewCacheEntry(
            VulkanPhysicalImageGroup Group,
            ulong ImageHandle,
            ImageView PrimaryView,
            Dictionary<AttachmentViewKey, ImageView> AttachmentViews);

        protected internal readonly record struct AttachmentViewKey(uint BaseMipLevel, uint LevelCount, uint BaseArrayLayer, uint LayerCount, ImageViewType ViewType, ImageAspectFlags AspectMask);

        /// <summary>Key identifying the layout state for one framebuffer attachment range.</summary>
        private readonly record struct AttachmentLayoutKey(uint BaseMipLevel, uint BaseArrayLayer, uint LayerCount);

        /// <summary>
        /// Uploads texture pixel data to the GPU via staging buffers.
        /// The default implementation logs a warning; concrete types override this.
        /// </summary>
        protected virtual void PushTextureData()
        {
            Debug.VulkanWarning($"{GetType().Name} does not implement texture data uploads yet.");
        }

        /// <summary>
        /// Full texture uploads replace the whole mip chain. Recreate any active
        /// dedicated image so imported low-res resident images cannot be reused
        /// as storage for a later larger CPU mip chain.
        /// </summary>
        protected void RecreateImageForFullTextureDataUpload(string reason)
        {
            if (!IsActive || _image.Handle == 0 || Renderer.IsDeviceLost)
                return;

            WaitForInFlightWorkBeforeImportedTextureReplacement(reason);
            Destroy();
        }

        /// <summary>
        /// Generates mipmaps on the GPU. Defaults to <see cref="GenerateMipmapsWithBlit"/>.
        /// </summary>
        protected virtual void GenerateMipmapsGPU()
            => GenerateMipmapsWithBlit();

        #endregion

        #region Event Handlers

        protected override void DataPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
            base.DataPropertyChanging(sender, e);

            if (e.PropertyName is nameof(XRTexture1DArray.Textures)
                or nameof(XRTexture2DArray.Textures)
                or nameof(XRTextureCubeArray.Cubes))
            {
                UnsubscribeChildTextureEvents(e.CurrentValue);
            }
        }

        protected override void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            base.DataPropertyChanged(sender, e);

            if (IsSamplerDataProperty(e.PropertyName))
                RecreateSamplerForPropertyChange();

            if (IsStorageDataProperty(e.PropertyName))
                RecreateImageForPropertyChange();

            if (e.PropertyName is nameof(XRTexture1DArray.Textures)
                or nameof(XRTexture2DArray.Textures)
                or nameof(XRTextureCubeArray.Cubes))
            {
                SubscribeChildTextureEvents(e.NewValue);
            }
        }

        /// <summary>
        /// Uploads invalidated texture data on the render thread.
        /// </summary>
        public override void PushData()
        {
            if (RuntimeEngine.InvokeOnMainThread(PushData, "VkTexture.PushData"))
                return;

            if (Renderer.IsDeviceLost)
                return;

            if (Data is XRTexture2D { RuntimeManagedProgressiveUploadActive: true })
                return;

            if (!TryBeginPushData(out bool allowPostPushCallback))
                return;

            PushTextureData();
            if (IsGenerated)
                MarkUploaded();

            CompletePushData(allowPostPushCallback);
        }

        /// <summary>
        /// Generates mipmaps on the render thread.
        /// </summary>
        public override void GenerateMipmaps()
        {
            if (RuntimeEngine.InvokeOnMainThread(GenerateMipmaps, "VkTexture.GenerateMipmaps"))
                return;

            if (Renderer.IsDeviceLost)
                return;

            GenerateMipmapsGPU();
            if (IsGenerated)
                MarkUploaded();
        }

        public override void Bind()
        {
            EnsureDescriptorReadyForVulkanUse("BindRequested");
            if (IsGenerated && _view.Handle != 0 && (!CreateSampler || _sampler.Handle != 0))
                MarkDescriptorClean();
        }

        public override void Clear(ColorF4 color, int level = 0)
        {
            if (RuntimeEngine.InvokeOnMainThread(() => Clear(color, level), "VkTexture.Clear"))
                return;

            Generate();
            if (!IsGenerated || _image.Handle == 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.ClearNotGenerated.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] ClearRequested could not generate image-backed texture '{0}' level={1}.",
                    Data.Name ?? Data.GetDescribingName(),
                    level);
                return;
            }

            uint baseMip = (uint)Math.Clamp(level, 0, Math.Max((int)ResolvedMipLevels - 1, 0));
            ImageLayout previousLayout = CurrentImageLayout;
            if (previousLayout != ImageLayout.TransferDstOptimal)
                TransitionImageLayout(previousLayout, ImageLayout.TransferDstOptimal);

            ImageSubresourceRange range = new()
            {
                AspectMask = AspectFlags,
                BaseMipLevel = baseMip,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = ResolvedArrayLayers,
            };

            using var scope = Renderer.NewCommandScope();
            if ((AspectFlags & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0)
            {
                ClearDepthStencilValue clearDepthStencil = new()
                {
                    Depth = color.R,
                    Stencil = (uint)Math.Clamp((int)color.G, 0, 255),
                };
                Api!.CmdClearDepthStencilImage(scope.CommandBuffer, _image, ImageLayout.TransferDstOptimal, ref clearDepthStencil, 1, ref range);
            }
            else
            {
                ClearColorValue clearColor = new()
                {
                    Float32_0 = color.R,
                    Float32_1 = color.G,
                    Float32_2 = color.B,
                    Float32_3 = color.A,
                };
                Api!.CmdClearColorImage(scope.CommandBuffer, _image, ImageLayout.TransferDstOptimal, ref clearColor, 1, ref range);
            }

            ImageLayout targetLayout = previousLayout == ImageLayout.Undefined
                ? ImageLayout.ShaderReadOnlyOptimal
                : previousLayout;
            if (targetLayout != ImageLayout.TransferDstOptimal)
                TransitionImageLayout(ImageLayout.TransferDstOptimal, targetLayout);

            MarkUploaded();
        }

        private void RecreateSamplerForPropertyChange()
        {
            MarkDescriptorDirty();
            if (!IsActive || !CreateSampler)
                return;

            DestroySampler();
            if (_image.Handle != 0)
                CreateSamplerInternal();
        }

        private void RecreateImageForPropertyChange()
        {
            InvalidateTextureData();
            _layoutInitialized = false;
            if (!IsActive)
                return;

            WaitForInFlightWorkBeforeImportedTextureReplacement("storage property changed");
            Destroy();
            Generate();
        }

        private void WaitForInFlightWorkBeforeImportedTextureReplacement(string reason)
        {
            if (!ShouldSynchronizeDedicatedImportedTextureReplacement())
                return;

            Debug.VulkanEvery(
                $"Vulkan.ImportedTextureReplacementSync.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Waiting for in-flight frames before replacing imported texture '{0}' ({1}).",
                Data.Name ?? Data.GetDescribingName(),
                reason);
            Renderer.WaitForAllInFlightWork();
        }

        private bool ShouldSynchronizeDedicatedImportedTextureReplacement()
        {
            if (Renderer.IsDeviceLost || _image.Handle == 0 || _physicalGroup is not null)
                return false;

            if (Data is not XRTexture2D texture)
                return false;

            if (texture.FrameBufferAttachment.HasValue
                || texture.Resizable
                || texture.RequiresStorageUsage
                || string.IsNullOrWhiteSpace(texture.FilePath))
            {
                return false;
            }

            return texture.Mipmaps is { Length: > 0 };
        }

        private static bool IsSamplerDataProperty(string? propertyName)
            => propertyName is null
                or ""
                or nameof(XRTexture.MinLOD)
                or nameof(XRTexture.MaxLOD)
                or nameof(XRTexture.LargestMipmapLevel)
                or nameof(XRTexture.SmallestAllowedMipmapLevel)
                or nameof(XRTexture1D.MinFilter)
                or nameof(XRTexture1D.MagFilter)
                or nameof(XRTexture1D.UWrap)
                or nameof(XRTexture1D.LodBias)
                or nameof(XRTexture2D.VWrap)
                or nameof(XRTexture3D.WWrap)
                or nameof(XRTexture2D.EnableComparison)
                or nameof(XRTexture2D.CompareFunc);

        private static bool IsStorageDataProperty(string? propertyName)
            => propertyName is null
                or ""
                or nameof(XRTexture.RequiresStorageUsage)
                or nameof(XRTexture.FrameBufferAttachment)
                or nameof(XRTexture1D.Mipmaps)
                or nameof(XRTexture1D.SizedInternalFormat)
                or nameof(XRTexture1DArray.Textures)
                or nameof(XRTexture2DArray.Textures)
                or nameof(XRTextureCubeArray.Cubes)
                or nameof(XRTexture2D.MultiSampleCount)
                or nameof(XRTexture2D.FixedSampleLocations)
                or nameof(XRTextureRectangle.Width)
                or nameof(XRTextureRectangle.Height)
                or nameof(XRTextureRectangle.Data)
                or nameof(XRTextureRectangle.PixelFormat)
                or nameof(XRTextureRectangle.PixelType);

        private void OnChildTextureResized()
            => OnTextureResized();

        private void OnChildTexturePropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (IsSamplerDataProperty(e.PropertyName))
                RecreateSamplerForPropertyChange();

            if (IsStorageDataProperty(e.PropertyName))
                RecreateImageForPropertyChange();
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
                case XRTextureRectangle rectangle:
                    rectangle.Resized += OnTextureResized;
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
                case XRTextureRectangle rectangle:
                    rectangle.Resized -= OnTextureResized;
                    break;
            }
        }

        private void SubscribeChildTextureEvents()
            => SubscribeChildTextureEvents(Data);

        private void UnsubscribeChildTextureEvents()
            => UnsubscribeChildTextureEvents(Data);

        private void SubscribeChildTextureEvents(object? value)
        {
            foreach (XRTexture texture in EnumerateChildTextures(value))
            {
                SubscribeChildResize(texture);
                texture.PropertyChanged += OnChildTexturePropertyChanged;
            }
        }

        private void UnsubscribeChildTextureEvents(object? value)
        {
            foreach (XRTexture texture in EnumerateChildTextures(value))
            {
                UnsubscribeChildResize(texture);
                texture.PropertyChanged -= OnChildTexturePropertyChanged;
            }
        }

        private static IEnumerable<XRTexture> EnumerateChildTextures(object? value)
        {
            switch (value)
            {
                case XRTexture1DArray tex1DArray:
                    return tex1DArray.Textures;
                case XRTexture2DArray tex2DArray:
                    return tex2DArray.Textures;
                case XRTextureCubeArray texCubeArray:
                    return texCubeArray.Cubes;
                case XRTexture1D[] tex1D:
                    return tex1D;
                case XRTexture2D[] tex2D:
                    return tex2D;
                case XRTextureCube[] texCube:
                    return texCube;
                default:
                    return [];
            }
        }

        private void SubscribeChildResize(XRTexture texture)
        {
            switch (texture)
            {
                case XRTexture1D tex1D:
                    tex1D.Resized += OnChildTextureResized;
                    break;
                case XRTexture2D tex2D:
                    tex2D.Resized += OnChildTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized += OnChildTextureResized;
                    break;
            }
        }

        private void UnsubscribeChildResize(XRTexture texture)
        {
            switch (texture)
            {
                case XRTexture1D tex1D:
                    tex1D.Resized -= OnChildTextureResized;
                    break;
                case XRTexture2D tex2D:
                    tex2D.Resized -= OnChildTextureResized;
                    break;
                case XRTextureCube texCube:
                    texCube.Resized -= OnChildTextureResized;
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
            InvalidateTextureData();
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

            bool preferIndirectCopy = Renderer.CanUseNvIndirectBufferCopyUploads;
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

            bool preferIndirectCopy = Renderer.CanUseNvIndirectBufferCopyUploads;
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
            if (!Renderer.TryMapBufferMemory(buffer, memory, 0, (ulong)length, out mappedPtr))
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
                RuntimeDirectStorageIO.TryReadInto(filePath, offset, length, mappedPtr);
            }
            catch
            {
                Renderer.UnmapBufferMemory(buffer, memory);
                Renderer.DestroyBuffer(buffer, memory);
                buffer = default;
                memory = default;
                return false;
            }

            Renderer.UnmapBufferMemory(buffer, memory);
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
                ImageLayout currentLayout = CurrentImageLayout;
                if (currentLayout != ImageLayout.ShaderReadOnlyOptimal &&
                    currentLayout != ImageLayout.DepthStencilReadOnlyOptimal)
                {
                    TransitionImageLayout(currentLayout, ImageLayout.ShaderReadOnlyOptimal);
                }
                return;
            }

            Api!.GetPhysicalDeviceFormatProperties(PhysicalDevice, ResolvedFormat, out FormatProperties props);
            if ((props.OptimalTilingFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) == 0)
            {
                Debug.VulkanWarning($"Texture format '{ResolvedFormat}' does not support linear blitting; skipping mipmap generation.");
                TransitionImageLayout(CurrentImageLayout, ImageLayout.ShaderReadOnlyOptimal);
                return;
            }

            ImageLayout sourceLayout = CurrentImageLayout;
            if (sourceLayout != ImageLayout.TransferDstOptimal)
                TransitionImageLayout(sourceLayout, ImageLayout.TransferDstOptimal);

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
