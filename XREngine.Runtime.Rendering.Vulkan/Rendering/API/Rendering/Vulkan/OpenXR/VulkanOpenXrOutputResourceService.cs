using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns OpenXR output images, views, and depth attachments. OpenXR command
/// recording/pool ownership intentionally remains with the command runtime.
/// </summary>
internal sealed unsafe class VulkanOpenXrOutputResourceService
{
    private readonly VulkanOutputRuntime _output;
    private readonly Vk _api;
    private readonly VulkanDeviceContext _device;
    private readonly VulkanCommandRuntime _commands;
    private readonly VulkanResourceRuntime _resources;
    private readonly VulkanTargetOutputServices _services;

    internal VulkanOpenXrOutputResourceService(
        VulkanOutputRuntime output,
        Vk api,
        VulkanDeviceContext device,
        VulkanCommandRuntime commands,
        VulkanResourceRuntime resources,
        VulkanFrameTelemetry telemetry)
    {
        _output = output;
        _api = api;
        _device = device;
        _commands = commands;
        _resources = resources;
        _services = new VulkanTargetOutputServices(api, device, commands, resources, telemetry, output);
    }

    internal ImageView GetOrCreateSwapchainImageView(Image image, Format format)
    {
        ulong key = image.Handle;
        if (_output.OpenXrBackend.SwapchainImageViews.TryGetValue(key, out VulkanOpenXrSwapchainImageViewCacheEntry cached))
        {
            if (cached.Format == format && cached.View.Handle != 0)
                return cached.View;
            DestroySwapchainImageView(cached.View, "OpenXR.SwapchainImageViewFormatChanged");
            _output.OpenXrBackend.SwapchainImageViews.Remove(key);
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
        _output.OpenXrBackend.SwapchainImageViews[key] = new VulkanOpenXrSwapchainImageViewCacheEntry(imageView, format);
        return imageView;
    }

    internal VulkanOpenXrDepthTarget GetOrCreateDepthTarget(int targetIndex, Extent2D extent)
    {
        VulkanOpenXrBackend backend = _output.OpenXrBackend;
        ref VulkanOpenXrDepthTarget cached = ref backend.CachedDepthTargets[targetIndex];
        ref Extent2D cachedExtent = ref backend.CachedDepthExtents[targetIndex];
        if (cached.Image.Handle != 0 && cachedExtent.Width == extent.Width && cachedExtent.Height == extent.Height)
            return cached;

        RetireDepthTarget(cached);
        cached = CreateDepthTarget(extent);
        cachedExtent = extent;
        return cached;
    }

    internal void RetireResources()
    {
        foreach (VulkanOpenXrSwapchainImageViewCacheEntry entry in _output.OpenXrBackend.SwapchainImageViews.Values)
            DestroySwapchainImageView(entry.View, "OpenXR.SwapchainImageViewCache");
        _output.OpenXrBackend.SwapchainImageViews.Clear();

        for (int index = 0; index < _output.OpenXrBackend.CachedDepthTargets.Length; index++)
        {
            RetireDepthTarget(_output.OpenXrBackend.CachedDepthTargets[index]);
            _output.OpenXrBackend.CachedDepthTargets[index] = default;
            _output.OpenXrBackend.CachedDepthExtents[index] = default;
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
