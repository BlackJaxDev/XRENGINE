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
            ReleaseCurrentImageBeforeBorrowingPhysicalGroup(group);

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
            RecordCurrentImageStorageDescription();
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
        RecordCurrentImageStorageDescription();
        AspectFlags = NormalizeAspectMaskForFormat(ResolvedFormat, AspectFlags);
        _currentImageLayout = ImageLayout.Undefined;
        ResetAttachmentLayoutTracking();
    }

    private void ReleaseCurrentImageBeforeBorrowingPhysicalGroup(VulkanPhysicalImageGroup targetGroup)
    {
        if (!_ownsImageMemory)
        {
            bool switchingBorrowedImage = _physicalGroup is not null &&
                !ReferenceEquals(_physicalGroup, targetGroup);
            bool replacingUnownedImage = _physicalGroup is null && _image.Handle != 0;
            if (switchingBorrowedImage || replacingUnownedImage)
                DestroyCurrentViews(removeActiveCacheEntry: true);
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
        _imageStorageLayout = null;
        _imageStorageFormat = null;
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
				// Views are reusable only while their physical group still owns a live image.
				// Keeping views from a destroyed group alive creates an ownership cycle: the
				// views prevent the old image retirement from draining, but this wrapper will
				// never revisit that dead group to retire the cached views.
				if (_physicalGroup.IsAllocated)
					SaveCurrentPhysicalImageViewCache();
				else
					DestroyCurrentViews(removeActiveCacheEntry: true);
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
        if (!Renderer.IsDeviceOperational)
            throw new InvalidOperationException($"Cannot create a Vulkan image while device state is {Renderer.DeviceState}.");

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
            Result result = Renderer.CreateVulkanImageTracked(ref imageInfo, imagePtr, "VkImageBackedTexture.Image");
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
        Renderer._imageAllocationTracker.Allocations[_image.Handle] = allocation;
        Renderer.TrackImageAllocation(
            _image,
            allocation,
            ResolveLogicalResourceName() ?? Data.Name ?? GetDescribingName(),
            "dedicated-texture",
            ResolvedExtent.Width,
            ResolvedExtent.Height,
            ResolvedExtent.Depth,
            ResolvedArrayLayers,
            ResolvedMipLevels,
            ResolvedFormat,
            Usage,
            SampleCount);
        _memory = allocation.Memory;

        if (Api!.BindImageMemory(Device, _image, allocation.Memory, allocation.Offset) != Result.Success)
        {
            Renderer._imageAllocationTracker.Allocations.TryRemove(_image.Handle, out _);
            Renderer.UntrackImageAllocation(_image);
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

    #endregion
}
