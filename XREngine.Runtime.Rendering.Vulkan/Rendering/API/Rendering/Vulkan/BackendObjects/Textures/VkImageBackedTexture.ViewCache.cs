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

internal unsafe abstract partial class VkImageBackedTexture<TTexture> : VkTexture<TTexture>, IVkFrameBufferAttachmentSource where TTexture : XRTexture
{
    #region Image View Management

    /// <summary>
    /// Destroys the current primary view and creates a new one. When <paramref name="key"/>
    /// is <c>default</c>, builds a view covering all mip levels, all array layers, and using
    /// the <see cref="DefaultViewType"/>.
    /// </summary>
    private void CreateImageView(AttachmentViewKey key)
    {
        if (_image.Handle == 0)
        {
            DestroyView(ref _view);
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
        ImageView replacement = CreateView(descriptor, _view);
        if (replacement.Handle == _view.Handle)
            return;

        DestroyView(ref _view);
        _view = replacement;
    }

    /// <summary>
    /// Creates a Vulkan <see cref="ImageView"/> for the given subresource descriptor.
    /// The aspect mask is normalised to ensure depth/stencil formats don't include the
    /// color bit.
    /// </summary>
    private ImageView CreateView(AttachmentViewKey descriptor, ImageView reusableView = default)
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

        if (BackendContext.Resources.Images.IsAvailableForDescriptor(reusableView) &&
            BackendContext.Resources.Images.IsStructurallyEquivalent(reusableView, in viewInfo))
            return reusableView;

        if (Api!.CreateImageView(Device, ref viewInfo, null, out ImageView created) != Result.Success)
            throw new Exception("Failed to create image view.");
        BackendContext.Resources.Images.RegisterView(
            created,
            in viewInfo,
            $"VkImageBackedTexture.View:{ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName()}");
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
            BackendContext.Resources.Images.RetireOwnedResources(new RetiredImageResources(
                default,
                default,
                view,
                [],
                default,
                0),
                "VkImageBackedTexture.DestroyView");
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
            BackendContext.Resources.Images.RetireOwnedResources(new RetiredImageResources(
                default,
                default,
                primaryView,
                attachmentViews,
                default,
                0),
                "VkImageBackedTexture.DestroyCurrentViews");
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

            // Physical images can be reused across render-resource generations, so matching
            // the current VkImage is not enough: every cached view must also be outside the
            // pending-retirement state before a new command buffer records it.
            if (!RefreshPrimaryDescriptorViewForUseNoLock())
                return default;

            AttachmentViewKey key = BuildAttachmentViewKey(mipLevel, layerIndex);
            if (key == default)
            {
                if (BloomDiagnosticsEnabled && IsBloomDiagnosticName(ResolveLogicalResourceName() ?? Data.Name))
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
                (!IsImageViewBackedByCurrentImage(cached) ||
                 !BackendContext.Resources.Images.IsAvailableForDescriptor(cached)))
            {
                _attachmentViews.Remove(key);
                cached = default;
            }

            if (cached.Handle == 0)
            {
                cached = CreateView(key);
                if (cached.Handle != 0)
                {
                    _attachmentViews[key] = cached;
                    PublishDescriptorViewRefreshNoLock();
                }
            }

            if (BloomDiagnosticsEnabled && IsBloomDiagnosticName(ResolveLogicalResourceName() ?? Data.Name))
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

    /// <summary>
    /// Returns a cached view covering exactly one storage-image mip and either
    /// one layer or the complete layered range requested by the binding.
    /// </summary>
    public ImageView GetStorageDescriptorView(int mipLevel, bool layered, int layerIndex)
    {
        lock (_imageStateLock)
        {
            RefreshPhysicalGroupImageIfStaleNoLock();
            if (_image.Handle == 0)
            {
                AcquireImageHandle();
                RefreshPhysicalGroupImageIfStaleNoLock();
            }

            if (_image.Handle == 0 || !RefreshPrimaryDescriptorViewForUseNoLock())
                return default;

            AttachmentViewKey key = BuildStorageDescriptorViewKey(mipLevel, layered, layerIndex);
            AttachmentViewKey primaryKey = NormalizeAttachmentViewKey(new AttachmentViewKey(
                0u,
                ResolvedMipLevels,
                0u,
                ResolvedArrayLayers,
                DefaultViewType,
                AspectFlags));
            if (key == primaryKey)
                return _view;

            if (_attachmentViews.TryGetValue(key, out ImageView cached) &&
                (!IsImageViewBackedByCurrentImage(cached) ||
                 !BackendContext.Resources.Images.IsAvailableForDescriptor(cached)))
            {
                _attachmentViews.Remove(key);
                cached = default;
            }

            if (cached.Handle != 0)
                return cached;

            cached = CreateView(key);
            if (cached.Handle == 0)
                return default;

            _attachmentViews[key] = cached;
            PublishDescriptorViewRefreshNoLock();
            return cached;
        }
    }

    bool IVkImageDescriptorSource.TryGetPublishedStorageDescriptorView(
        int mipLevel,
        bool layered,
        int layerIndex,
        out ImageView view)
    {
        lock (_imageStateLock)
        {
            view = default;
            if (_image.Handle == 0 || !IsDescriptorReadyNoLock())
                return false;

            AttachmentViewKey key = BuildStorageDescriptorViewKey(
                mipLevel,
                layered,
                layerIndex);
            AttachmentViewKey primaryKey = NormalizeAttachmentViewKey(
                new AttachmentViewKey(
                    0u,
                    ResolvedMipLevels,
                    0u,
                    ResolvedArrayLayers,
                    DefaultViewType,
                    AspectFlags));
            view = key == primaryKey
                ? _view
                : _attachmentViews.TryGetValue(key, out ImageView cached)
                    ? cached
                    : default;
            return view.Handle != 0 &&
                IsImageViewBackedByCurrentImage(view) &&
                BackendContext.Resources.Images.IsAvailableForDescriptor(view);
        }
    }

    private AttachmentViewKey BuildStorageDescriptorViewKey(int mipLevel, bool layered, int layerIndex)
    {
        uint baseMip = ClampAttachmentMipLevel(mipLevel);
        uint resolvedLayers = Math.Max(ResolvedArrayLayers, 1u);
        if (TextureImageType == ImageType.Type3D)
            return NormalizeAttachmentViewKey(new AttachmentViewKey(
                baseMip,
                1u,
                0u,
                1u,
                ImageViewType.Type3D,
                AspectFlags));

        if (layered)
            return NormalizeAttachmentViewKey(new AttachmentViewKey(
                baseMip,
                1u,
                0u,
                resolvedLayers,
                DefaultViewType,
                AspectFlags));

        ImageViewType singleLayerViewType = TextureImageType == ImageType.Type1D
            ? ImageViewType.Type1D
            : ImageViewType.Type2D;
        return NormalizeAttachmentViewKey(new AttachmentViewKey(
            baseMip,
            1u,
            ClampAttachmentLayerIndex(layerIndex),
            1u,
            singleLayerViewType,
            AspectFlags));
    }

    private bool IsImageViewBackedByCurrentImage(ImageView view)
    {
        if (view.Handle == 0 || _image.Handle == 0)
            return false;

        return BackendContext.Resources.Images.TryGetBackingImage(view, out Image backingImage) &&
            backingImage.Handle == _image.Handle &&
            BackendContext.Resources.Images.IsLiveBackedByLiveImage(view);
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
        // transition from Undefined â†’ attachment-optimal via its initialLayout
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
        // and 1 layer â€” otherwise we must create a single-mip view.
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
        PhysicalImageViewCacheEntry entry;
        if (cacheIndex >= 0)
        {
            entry = _physicalImageViewCache[cacheIndex];
            entry.PrimaryView = CreatePhysicalImageViewCacheValue(_view);
            entry.AttachmentViews.Clear();
        }
        else
        {
            entry = new PhysicalImageViewCacheEntry(
                _physicalGroup,
                _image.Handle,
                BackendContext.Resources.Lifetime.Tracker.GetPublishedGeneration(
                    new VulkanResourceLifetimeKey(ObjectType.Image, _image.Handle)),
                CreatePhysicalImageViewCacheValue(_view),
                new Dictionary<AttachmentViewKey, PhysicalImageViewCacheValue>(_attachmentViews.Count));
            _physicalImageViewCache.Add(entry);
        }

        foreach (KeyValuePair<AttachmentViewKey, ImageView> pair in _attachmentViews)
            entry.AttachmentViews[pair.Key] = CreatePhysicalImageViewCacheValue(pair.Value);
    }

    private PhysicalImageViewCacheValue CreatePhysicalImageViewCacheValue(ImageView view)
        => new(
            view,
            BackendContext.Resources.Lifetime.Tracker.GetPublishedGeneration(
                new VulkanResourceLifetimeKey(ObjectType.ImageView, view.Handle)));

    private bool TryRestorePhysicalImageViewCache(VulkanPhysicalImageGroup group, Image image)
    {
        int cacheIndex = FindPhysicalImageViewCacheIndex(group, image.Handle);
        if (cacheIndex < 0)
            return false;

        PhysicalImageViewCacheEntry entry = _physicalImageViewCache[cacheIndex];
        if (!IsCachedImageViewBackedByImage(entry.PrimaryView, image))
            return false;

        _view = entry.PrimaryView.View;
        _attachmentViews.Clear();
        foreach (KeyValuePair<AttachmentViewKey, PhysicalImageViewCacheValue> pair in entry.AttachmentViews)
        {
            if (IsCachedImageViewBackedByImage(pair.Value, image))
                _attachmentViews[pair.Key] = pair.Value.View;
        }
        return _view.Handle != 0;
    }

    private bool IsCachedImageViewBackedByImage(
        PhysicalImageViewCacheValue cached,
        Image image)
    {
        ImageView view = cached.View;
        if (view.Handle == 0 || image.Handle == 0)
            return false;

        if (BackendContext.Resources.Lifetime.Tracker.GetPublishedGeneration(
                new VulkanResourceLifetimeKey(ObjectType.ImageView, view.Handle)) != cached.Generation)
        {
            return false;
        }

        return BackendContext.Resources.Images.TryGetBackingImage(view, out Image backingImage) &&
            backingImage.Handle == image.Handle &&
            BackendContext.Resources.Images.IsLiveBackedByLiveImage(view) &&
            BackendContext.Resources.Images.IsAvailableForDescriptor(view);
    }

    private int FindPhysicalImageViewCacheIndex(VulkanPhysicalImageGroup? group, ulong imageHandle)
    {
        if (group is null || imageHandle == 0)
            return -1;

        ulong imageGeneration = BackendContext.Resources.Lifetime.Tracker.GetPublishedGeneration(
            new VulkanResourceLifetimeKey(ObjectType.Image, imageHandle));
        for (int i = 0; i < _physicalImageViewCache.Count; i++)
        {
            PhysicalImageViewCacheEntry entry = _physicalImageViewCache[i];
            if (ReferenceEquals(entry.Group, group) &&
                entry.ImageHandle == imageHandle &&
                entry.ImageGeneration == imageGeneration)
            {
                return i;
            }
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
            foreach (PhysicalImageViewCacheValue view in entry.AttachmentViews.Values)
                AddUniqueView(view);
        }

        if (cachedViews.Count > 0)
        {
            BackendContext.Resources.Images.RetireOwnedResources(new RetiredImageResources(
                default,
                default,
                default,
                [.. cachedViews],
                default,
                0),
                "VkImageBackedTexture.DestroyPhysicalImageViewCache");
        }

        _physicalImageViewCache.Clear();

        void AddUniqueView(PhysicalImageViewCacheValue cached)
        {
            ImageView view = cached.View;
            if (view.Handle == 0 ||
                BackendContext.Resources.Lifetime.Tracker.GetPublishedGeneration(
                    new VulkanResourceLifetimeKey(ObjectType.ImageView, view.Handle)) != cached.Generation ||
                !seenHandles.Add(view.Handle))
            {
                return;
            }
            cachedViews.Add(view);
        }
    }

    private sealed class PhysicalImageViewCacheEntry(
        VulkanPhysicalImageGroup group,
        ulong imageHandle,
        ulong imageGeneration,
        PhysicalImageViewCacheValue primaryView,
        Dictionary<AttachmentViewKey, PhysicalImageViewCacheValue> attachmentViews)
    {
        public VulkanPhysicalImageGroup Group { get; } = group;
        public ulong ImageHandle { get; } = imageHandle;
        public ulong ImageGeneration { get; } = imageGeneration;
        public PhysicalImageViewCacheValue PrimaryView { get; set; } = primaryView;
        public Dictionary<AttachmentViewKey, PhysicalImageViewCacheValue> AttachmentViews { get; } = attachmentViews;
    }

    private readonly record struct PhysicalImageViewCacheValue(
        ImageView View,
        ulong Generation);

    private static bool BloomDiagnosticsEnabled
        => XREnvironment.IsEnabled(XREngineEnvironmentVariables.BloomDiag);

    private static bool IsBloomDiagnosticName(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           name.Contains("Bloom", StringComparison.OrdinalIgnoreCase);

    protected internal readonly record struct AttachmentViewKey(uint BaseMipLevel, uint LevelCount, uint BaseArrayLayer, uint LayerCount, ImageViewType ViewType, ImageAspectFlags AspectMask);

    /// <summary>Key identifying the layout state for one framebuffer attachment range.</summary>
    private readonly record struct AttachmentLayoutKey(uint BaseMipLevel, uint BaseArrayLayer, uint LayerCount);
}
