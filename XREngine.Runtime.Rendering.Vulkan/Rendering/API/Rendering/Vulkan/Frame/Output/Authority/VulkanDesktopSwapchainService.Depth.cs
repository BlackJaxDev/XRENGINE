using System.Threading;
using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanDesktopSwapchainService
{
    /// <summary>Creates and atomically publishes the depth attachment for the live desktop WSI generation.</summary>
    internal void CreateDepthResources()
    {
        Format depthFormat = FindDepthFormatForOutput();
        ImageAspectFlags depthAspect = IsDepthStencilFormatForOutput(depthFormat)
            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : ImageAspectFlags.DepthBit;

        lock (_output.Desktop.DepthMutationGate)
        {
            if (_output.DesktopDepthResources is not null)
                return;

            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent = new Extent3D(_output.Desktop.Extent.Width, _output.Desktop.Extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Format = depthFormat,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = SharingMode.Exclusive,
            };

            if (_services.CreateVulkanImageTracked(ref imageInfo, out Image depthImage, "Swapchain.Depth") != Result.Success)
                throw new InvalidOperationException("Failed to create the swapchain depth image.");

            VulkanMemoryAllocation allocation = _services.AllocateImageMemoryWithFallback(depthImage, MemoryPropertyFlags.DeviceLocalBit);
            _resources.Allocations.Images.Allocations[depthImage.Handle] = allocation;
            if (_api.BindImageMemory(_device.Device, depthImage, allocation.Memory, allocation.Offset) != Result.Success)
            {
                _resources.Allocations.Images.Allocations.TryRemove(depthImage.Handle, out _);
                _services.DestroyVulkanImageImmediateTracked(depthImage, "Swapchain.Depth.BindFailure");
                _services.FreeMemoryAllocation(allocation);
                throw new InvalidOperationException("Failed to bind swapchain depth memory.");
            }

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = depthImage,
                ViewType = ImageViewType.Type2D,
                Format = depthFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = depthAspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            if (_api.CreateImageView(_device.Device, ref viewInfo, null, out ImageView depthView) != Result.Success)
            {
                _resources.Allocations.Images.Allocations.TryRemove(depthImage.Handle, out _);
                _services.DestroyVulkanImageImmediateTracked(depthImage, "Swapchain.Depth.ViewFailure");
                _services.FreeMemoryAllocation(allocation);
                throw new InvalidOperationException("Failed to create the swapchain depth view.");
            }

            _services.TrackLiveImageView(depthView, in viewInfo, "Swapchain.Depth");
            VulkanSwapchainDepthResources resources = new(
                depthImage,
                allocation.Memory,
                depthView,
                depthFormat,
                depthAspect,
                _output.Desktop.Extent);
            Volatile.Write(ref _output.Desktop.DepthResources, resources);
            Debug.Vulkan(
                "[Vulkan] Published swapchain depth target. Image=0x{0:X} Generation={1} Extent={2}x{3}.",
                depthImage.Handle,
                _resources.GetPublishedGeneration(ObjectType.Image, depthImage.Handle),
                resources.Extent.Width,
                resources.Extent.Height);
        }
    }

    /// <summary>
    /// Detaches the live depth target before a generation is retired.  The
    /// caller owns queueing the returned bundle with the generation's existing
    /// retirement proof; this method intentionally performs no native destroy.
    /// </summary>
    internal VulkanSwapchainDepthResources? DetachDepthResources()
    {
        lock (_output.Desktop.DepthMutationGate)
            return Interlocked.Exchange(ref _output.Desktop.DepthResources, null);
    }

    internal Format FindDepthFormatForOutput()
        => FindSupportedFormat(
            [Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint],
            ImageTiling.Optimal,
            FormatFeatureFlags.DepthStencilAttachmentBit);

    private Format FindSupportedFormat(
        IEnumerable<Format> candidates,
        ImageTiling tiling,
        FormatFeatureFlags features)
    {
        foreach (Format format in candidates)
        {
            _api.GetPhysicalDeviceFormatProperties(_device.PhysicalDevice, format, out FormatProperties properties);
            if ((tiling == ImageTiling.Linear && (properties.LinearTilingFeatures & features) == features) ||
                (tiling == ImageTiling.Optimal && (properties.OptimalTilingFeatures & features) == features))
            {
                return format;
            }
        }

        throw new InvalidOperationException("No supported Vulkan depth format was found.");
    }

    internal static bool IsDepthStencilFormatForOutput(Format format)
        => format is Format.D32SfloatS8Uint or Format.D24UnormS8Uint or Format.D16UnormS8Uint;
}
