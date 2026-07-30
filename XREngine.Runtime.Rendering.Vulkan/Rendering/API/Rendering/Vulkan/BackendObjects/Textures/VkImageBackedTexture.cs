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
internal unsafe abstract partial class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
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

    /// <summary>
    /// Layout and format used to create the current <see cref="_image"/>. These remain
    /// separate from <see cref="_layout"/>, which streaming metadata may refresh before
    /// the replacement image is published.
    /// </summary>
    private TextureLayout? _imageStorageLayout;
    private Format? _imageStorageFormat;

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
            && Renderer.IsImageViewAvailableForDescriptor(_view)
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

            PublishPlannerBackedDescriptorIfReadyNoLock();
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

            PublishPlannerBackedDescriptorIfReadyNoLock();
            return IsDescriptorReadyNoLock();
        }
    }

    private void PublishPlannerBackedDescriptorIfReadyNoLock()
    {
        if (_physicalGroup is null || !IsDescriptorDirty)
            return;

        bool handlesReady =
            _image.Handle != 0 &&
            _view.Handle != 0 &&
            IsImageViewBackedByCurrentImage(_view) &&
            Renderer.IsImageViewAvailableForDescriptor(_view) &&
            (!CreateSampler || _sampler.Handle != 0);
        if (!handlesReady)
            return;

        // Planner-backed render targets already contain their GPU storage. A
        // dirty descriptor here means that consumers need the current
        // image/view/sampler generation, not that texture data must be
        // uploaded. Publishing it is therefore safe even in recording
        // scopes that intentionally forbid synchronous uploads.
        HasUploadedData = true;
        IsInvalidated = false;
        MarkDescriptorPublished();
    }

    private bool TryEnsureDescriptorReadyForVulkanUseNoThrow(string reason)
    {
        try
        {
            if (_physicalGroup is not null || Data.FrameBufferAttachment.HasValue || Data.RequiresStorageUsage)
            {
                Generate();
                if (!RefreshPhysicalGroupImageIfStaleNoLock())
                    return false;
                if (!RefreshPrimaryDescriptorViewForUseNoLock())
                    return false;

                PublishPlannerBackedDescriptorIfReadyNoLock();
                if (IsDescriptorReadyNoLock())
                    return true;

                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.ImageBackedDescriptorNotReady.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Image-backed texture descriptor readiness failed for '{0}' ({1}): image=0x{2:X} view=0x{3:X} sampler=0x{4:X} descriptorDirty={5}.",
                    ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
                    reason,
                    _image.Handle,
                    _view.Handle,
                    _sampler.Handle,
                    IsDescriptorDirty);
                return false;
            }

            EnsureDescriptorReadyForVulkanUse(reason);
            return RefreshPrimaryDescriptorViewForUseNoLock();
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

    /// <summary>
    /// Replaces a primary descriptor view that no longer belongs to the current live resource
    /// generation. Generation retirement can begin after a texture published its descriptor,
    /// so descriptor dirtiness alone is not sufficient to decide whether the cached view is usable.
    /// </summary>
    private bool RefreshPrimaryDescriptorViewForUseNoLock()
    {
        if (_view.Handle != 0 &&
            IsImageViewBackedByCurrentImage(_view) &&
            Renderer.IsImageViewAvailableForDescriptor(_view))
        {
            return true;
        }

        if (_image.Handle == 0)
            return false;

        if (_view.Handle != 0)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Texture.RefreshRetiringPrimaryDescriptorView.{Data.GetHashCode()}.{_view.Handle}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Replacing stale or retiring primary descriptor image view 0x{0:X} for texture '{1}' against current image 0x{2:X}.",
                _view.Handle,
                ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
                _image.Handle);
        }

        // The lifetime manager already owns retirement of an unavailable view. Forgetting it
        // avoids enqueueing the same handle again while forcing a fresh generation.
        ForgetCurrentViews(removeActiveCacheEntry: true);
        CreateImageView(default);
        bool refreshed = _view.Handle != 0 &&
            IsImageViewBackedByCurrentImage(_view) &&
            Renderer.IsImageViewAvailableForDescriptor(_view);
        if (refreshed)
            PublishDescriptorViewRefreshNoLock();
        return refreshed;
    }

    private void PublishDescriptorViewRefreshNoLock()
    {
        // The image contents did not change, but every descriptor consumer must observe the
        // replacement handle rather than reusing a fingerprint from the retired view.
        MarkDescriptorDirty();
        MarkDescriptorPublished();
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
                !IsDescriptorReadyNoLock() &&
                !TryEnsureDescriptorReadyForVulkanUseNoThrow(reason))
            {
                snapshot = default;
                return false;
            }

            if (_physicalGroup is not null &&
                (!_physicalGroup.IsAllocated || _physicalGroup.Image.Handle != _image.Handle) &&
                !RefreshPhysicalGroupImageIfStaleNoLock())
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
        if (view.Handle != 0 &&
            (!IsImageViewBackedByCurrentImage(view) || !Renderer.IsImageViewAvailableForDescriptor(view)))
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.Texture.StaleDescriptorView.{Data.GetHashCode()}.{view.Handle}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Refreshing stale or retiring descriptor image view 0x{0:X} for texture '{1}' against current image 0x{2:X}.",
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
            if (view.Handle != 0 &&
                IsImageViewBackedByCurrentImage(view) &&
                Renderer.IsImageViewAvailableForDescriptor(view))
            {
                PublishDescriptorViewRefreshNoLock();
            }
        }

        ImageLayout trackedLayout = ResolveTrackedImageLayoutNoLock();
        bool ready = IsDescriptorReadyNoLock() &&
            view.Handle != 0 &&
            IsImageViewBackedByCurrentImage(view) &&
            Renderer.IsImageViewAvailableForDescriptor(view);
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
}
