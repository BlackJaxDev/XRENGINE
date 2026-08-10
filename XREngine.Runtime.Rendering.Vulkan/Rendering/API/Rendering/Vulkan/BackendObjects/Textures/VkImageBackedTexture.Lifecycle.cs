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
        MarkDescriptorPublished();
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

        BackendContext.Images.RetireOwnedResources(new RetiredImageResources(
            _ownsImageMemory ? _image : default,
            _ownsImageMemory ? _memory : default,
            _view,
            retiredAttachmentViews,
            _sampler,
            _ownsImageMemory ? _allocatedVRAMBytes : 0),
            "VkImageBackedTexture.DeleteObjectInternal");

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
        _imageStorageLayout = null;
        _imageStorageFormat = null;
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
}
