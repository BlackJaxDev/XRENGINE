using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns OpenXR output images, views, and depth attachments. OpenXR command
/// recording/pool ownership intentionally remains with the command runtime.
/// </summary>
internal sealed unsafe class VulkanOpenXrOutputResourceService
{
    private readonly VulkanOpenXrBackend _backend;
    private readonly Vk _api;
    private readonly VulkanDeviceContext _device;
    private readonly VulkanCommandRuntime _commands;
    private readonly VulkanResourceRuntime _resources;
    private readonly IVulkanTargetOutputHost _services;

    internal VulkanOpenXrOutputResourceService(
        VulkanOpenXrBackend backend,
        Vk api,
        VulkanDeviceContext device,
        VulkanCommandRuntime commands,
        VulkanResourceRuntime resources,
        VulkanFrameTelemetry telemetry,
        IVulkanTargetOutputHost services)
    {
        _backend = backend;
        _api = api;
        _device = device;
        _commands = commands;
        _resources = resources;
        _services = services;
    }

    internal ImageView GetOrCreateSwapchainImageView(Image image, Format format)
    {
        ulong key = image.Handle;
        if (_backend.SwapchainImageViews.TryGetValue(key, out VulkanOpenXrSwapchainImageViewCacheEntry cached))
        {
            if (cached.Format == format && cached.View.Handle != 0)
                return cached.View;
            RetireSwapchainImageView(cached.View, "OpenXR.SwapchainImageViewFormatChanged");
            _backend.SwapchainImageViews.Remove(key);
        }

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };
        if (_api.CreateImageView(_device.Device, ref viewInfo, null, out ImageView imageView) != Result.Success)
            throw new InvalidOperationException("Failed to create an OpenXR Vulkan swapchain image view.");

        _services.TrackLiveImageView(imageView, in viewInfo, "OpenXR.SwapchainImageView");
        _backend.SwapchainImageViews[key] = new VulkanOpenXrSwapchainImageViewCacheEntry(imageView, format);
        return imageView;
    }

    internal VulkanOpenXrDepthTarget GetOrCreateDepthTarget(int targetIndex, Extent2D extent)
    {
        VulkanOpenXrBackend backend = _backend;
        ref VulkanOpenXrDepthTarget cached = ref backend.CachedDepthTargets[targetIndex];
        ref Extent2D cachedExtent = ref backend.CachedDepthExtents[targetIndex];
        if (cached.Image.Handle != 0 && cachedExtent.Width == extent.Width && cachedExtent.Height == extent.Height)
            return cached;

        RetireDepthTarget(cached);
        cached = CreateDepthTarget(extent);
        cachedExtent = extent;
        return cached;
    }

    internal VulkanOpenXrSwapchainChildRetirementReceipt RetireSwapchainChildren(
        ReadOnlySpan<Image> retiringImages)
    {
        if (retiringImages.IsEmpty)
            return VulkanOpenXrSwapchainChildRetirementReceipt.Empty;

        HashSet<ulong> imageHandles = new(retiringImages.Length);
        for (int i = 0; i < retiringImages.Length; i++)
            if (retiringImages[i].Handle != 0)
                imageHandles.Add(retiringImages[i].Handle);

        List<ImageView> views = [];
        List<Framebuffer> framebuffers = [];
        List<VulkanPinnedResourceGeneration> generations = [];
        HashSet<ulong> viewHandles = [];
        VulkanResourceLifetimeTracker tracker = _resources.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            foreach ((ulong viewHandle, ulong backingImage) in tracker.ImageViewBackingImages)
            {
                if (!imageHandles.Contains(backingImage))
                    continue;

                if (!tracker.ResourceLifetimes.TryGetValue(new(ObjectType.ImageView, viewHandle), out VulkanResourceLifetimeRecord? view))
                    return new([], [], [], false);
                if ((view.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                    continue;

                views.Add(new ImageView(viewHandle));
                viewHandles.Add(viewHandle);
                generations.Add(new VulkanPinnedResourceGeneration(
                    new(ObjectType.ImageView, viewHandle), view.Generation));
            }

            foreach ((ulong framebufferHandle, VulkanResourceLifetimeKey[] attachments) in tracker.FramebufferAttachments)
            {
                bool referencesRetiringView = false;
                for (int i = 0; i < attachments.Length; i++)
                    if (attachments[i].Type == ObjectType.ImageView && viewHandles.Contains(attachments[i].Handle))
                    {
                        referencesRetiringView = true;
                        break;
                    }
                if (!referencesRetiringView)
                    continue;
                if (!tracker.ResourceLifetimes.TryGetValue(new(ObjectType.Framebuffer, framebufferHandle), out VulkanResourceLifetimeRecord? framebuffer))
                    return new([], [], [], false);
                if ((framebuffer.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                    continue;

                framebuffers.Add(new Framebuffer(framebufferHandle));
                generations.Add(new VulkanPinnedResourceGeneration(
                    new(ObjectType.Framebuffer, framebufferHandle), framebuffer.Generation));
            }

            foreach ((ulong imageHandle, VulkanOpenXrSwapchainImageViewCacheEntry cached) in _backend.SwapchainImageViews)
                if (imageHandles.Contains(imageHandle) && cached.View.Handle != 0 &&
                    !viewHandles.Contains(cached.View.Handle))
                    return new([], [], [], false);
        }

        // A partial native-queue failure must not leave an active cache entry
        // pointing at a PendingRetirement view. Future recording rebuilds it,
        // while ImageViewBackingImages keeps the old closure discoverable.
        foreach (ulong imageHandle in imageHandles)
            _backend.SwapchainImageViews.Remove(imageHandle);

        // Framebuffers retain their views. Queue their destruction first.
        for (int i = 0; i < framebuffers.Count; i++)
            _resources.RetireFramebuffer(framebuffers[i], "OpenXR.RetiredSwapchain.Framebuffer");
        for (int i = 0; i < views.Count; i++)
            RetireSwapchainImageView(views[i], "OpenXR.RetiredSwapchain.ImageView");

        return new VulkanOpenXrSwapchainChildRetirementReceipt(
            views.ToArray(), framebuffers.ToArray(), generations.ToArray(), true);
    }

    internal void RetireResources()
    {
        // Terminal resource cleanup includes unrelated depth targets. Swapchain
        // replacement uses RetireSwapchainChildren so those targets stay live.
        foreach (VulkanOpenXrSwapchainImageViewCacheEntry entry in _backend.SwapchainImageViews.Values)
            RetireSwapchainImageView(entry.View, "OpenXR.SwapchainImageViewCache");
        _backend.SwapchainImageViews.Clear();

        for (int index = 0; index < _backend.CachedDepthTargets.Length; index++)
        {
            RetireDepthTarget(_backend.CachedDepthTargets[index]);
            _backend.CachedDepthTargets[index] = default;
            _backend.CachedDepthExtents[index] = default;
        }
    }

    private VulkanOpenXrDepthTarget CreateDepthTarget(Extent2D extent)
    {
        Format depthFormat = FindDepthFormat();
        ImageAspectFlags depthAspect = VulkanDesktopSwapchainService.IsDepthStencilFormatForOutput(depthFormat)
            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : ImageAspectFlags.DepthBit;
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = depthFormat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        Image image = default;
        ImageView view = default;
        VulkanMemoryAllocation allocation = VulkanMemoryAllocation.Null;
        try
        {
            if (_services.CreateVulkanImageTracked(ref imageInfo, out image, "OpenXR.DepthTarget") != Result.Success)
                throw new InvalidOperationException("Failed to create an OpenXR Vulkan depth image.");
            allocation = _services.AllocateImageMemoryWithFallback(image, MemoryPropertyFlags.DeviceLocalBit);
            _resources.Allocations.Images.Allocations[image.Handle] = allocation;
            if (_api.BindImageMemory(_device.Device, image, allocation.Memory, allocation.Offset) != Result.Success)
                throw new InvalidOperationException("Failed to bind the OpenXR Vulkan depth image memory.");

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = depthFormat,
                SubresourceRange = new ImageSubresourceRange { AspectMask = depthAspect, LevelCount = 1, LayerCount = 1 },
            };
            if (_api.CreateImageView(_device.Device, ref viewInfo, null, out view) != Result.Success)
                throw new InvalidOperationException("Failed to create an OpenXR Vulkan depth image view.");
            _services.TrackLiveImageView(view, in viewInfo, "OpenXR.DepthTarget");
            return new VulkanOpenXrDepthTarget(image, allocation.Memory, view, depthFormat, depthAspect);
        }
        catch
        {
            DestroySwapchainImageView(view, "OpenXR.DepthTarget.CreateFailure");
            if (image.Handle != 0)
            {
                _resources.Allocations.Images.Allocations.TryRemove(image.Handle, out VulkanMemoryAllocation trackedAllocation);
                _services.DestroyVulkanImageImmediateTracked(image, "OpenXR.DepthTarget.CreateFailure");
                _services.FreeMemoryAllocation(trackedAllocation.IsNull ? allocation : trackedAllocation);
            }
            throw;
        }
    }

    private void RetireDepthTarget(VulkanOpenXrDepthTarget target)
    {
        if (target.Image.Handle != 0 || target.View.Handle != 0)
            _resources.Images.RetireOwnedResources(new RetiredImageResources(
                target.Image, target.Memory, target.View, [], default, 0), "OpenXR.DepthTarget");
    }

    /// <summary>
    /// Drops a cache-owned view only after every tracked OpenXR submission that
    /// referenced its externally owned swapchain image has completed.
    /// </summary>
    private void RetireSwapchainImageView(ImageView view, string owner)
    {
        if (view.Handle != 0)
            _resources.Images.RetireOwnedResources(
                new RetiredImageResources(default, default, view, [], default, 0),
                owner);
    }

    private void DestroySwapchainImageView(ImageView view, string owner)
    {
        if (view.Handle != 0 && _services.TryBeginDestroyImageView(view, owner))
            _api.DestroyImageView(_device.Device, view, null);
    }

    private Format FindDepthFormat()
    {
        Format[] candidates = [Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint];
        for (int index = 0; index < candidates.Length; index++)
        {
            _api.GetPhysicalDeviceFormatProperties(_device.PhysicalDevice, candidates[index], out FormatProperties properties);
            if ((properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                return candidates[index];
        }
        throw new InvalidOperationException("No supported Vulkan depth format was found for OpenXR output.");
    }
}
